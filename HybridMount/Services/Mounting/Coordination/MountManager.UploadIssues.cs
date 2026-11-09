using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    internal Func<string,Task>? IssuePropertyWrittenForTests {get;set;}
    internal Func<string,Task>? IssuePropertyReleasedForTests {get;set;}
    private Task? _issuePublishing;
    private readonly SemaphoreSlim _issuePublishGate=new(1,1);
    private readonly Dictionary<string,(long Generation,UploadAttention Attention,DateTimeOffset Next)> _issuePublicationRetry=[];
    private bool _legacyIssueClearsQueued;
    /// <summary>问题变化使用独立只读快照；清除任务历史不影响此事件和持久状态。</summary>
    public event Action? UploadIssuesChanged;
    private void NotifyIssues()
    {
        if(UploadIssuesChanged is not {} handlers)return;
        foreach(Action action in handlers.GetInvocationList())try{action();}catch(Exception error){_logger.Queue("同步问题","Warning",error.GetType().Name);}
    }
    private async Task PublishIssueLoopAsync()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                await PublishUploadIssuesCoreAsync(_stop.Token).ConfigureAwait(false);
                await Task.Delay(750,_stop.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        catch(Exception error){_logger.QueueException("同步问题提示","Error","操作未完成",error);}
    }
    /// <summary>仅实例测试调用实际生产发布链；应用生命周期拥有操作，退出等待保护句柄释放。</summary>
    internal Task PublishUploadIssuesAsync(CancellationToken token=default)=>OwnAsync(PublishUploadIssuesCoreAsync,token);
    private async Task PublishUploadIssuesCoreAsync(CancellationToken token)
    {
        await _issuePublishGate.WaitAsync(token).ConfigureAwait(false);
        try{await PublishUploadIssuesLockedAsync(token).ConfigureAwait(false);}
        finally{_issuePublishGate.Release();}
    }
    private async Task PublishUploadIssuesLockedAsync(CancellationToken token)
    {
        // 旧版Cleared可能仍有空白编号；每次应用会话只检查一遍，继续走原身份、路径预留和恢复日志。
        if(!_legacyIssueClearsQueued)
        {
            foreach(var legacy in _repository.Uploads().Where(x=>x.Attention is {ResolvedUtc:not null,ShellState:"Cleared"}))
            {
                token.ThrowIfCancellationRequested();var attention=legacy.Attention!;
                _repository.UpdateUpload(legacy.Id,current=>current.Generation!=legacy.Generation||current.Attention!=attention?current:
                    current with{Attention=attention with{ShellState="ClearPending",ShellError=null}});
            }
            _legacyIssueClearsQueued=true;
        }
        foreach(var selected in _repository.Uploads().Where(x=>x.Attention?.ShellState is "Pending" or "ClearPending" or "Publishing" or "Clearing"))
        {
            token.ThrowIfCancellationRequested();
            if(_issuePublicationRetry.TryGetValue(selected.Id,out var retry)&&retry.Generation==selected.Generation&&retry.Attention==selected.Attention&&retry.Next>DateTimeOffset.UtcNow)continue;
            if(!_roots.TryGetValue(selected.MountId,out var root)||root.RemovalRequested||root.Definition.RemovalPending)continue;
            await root.Gate.WaitAsync(token).ConfigureAwait(false);UploadRuntime? runtime=null;var reserved=false;var updated=false;string? notify=null;var attempted=selected;UploadAttention? journal=null;
            try
            {
                if(IsStopping||root.RemovalRequested||root.Definition.RemovalPending)continue;
                var item=_repository.Upload(selected.Id);if(item?.Attention is not {} a||a.ShellState is not("Pending" or "ClearPending" or "Publishing" or "Clearing"))continue;
                attempted=item;
                lock(root.ConnectionSync)
                {
                    runtime=root.Uploads;
                    if(runtime is not null)lock(runtime.Sync)
                    {if(runtime.CurrentId is not null||runtime.PropertyPath is not null||runtime.ConflictWaiters>0)continue;runtime.PropertyPath=item.LocalPath;reserved=true;}
                }
                // 缺失文件无需调用Shell；只有已解决的问题才能结束清理，未解决原因继续保留。
                if(!MountPathGuard.TryValidateOperation(root.Definition,RelativePaths.ToLocal(root.Definition.LocalPath,item.LocalPath)))
                {
                    if(a.ResolvedUtc is null)updated=RecordIssuePublicationFailure(item,null,"Explorer提示未发布：本地文件不存在，保留同步问题。");
                    else
                    {
                        _repository.UpdateUpload(item.Id,current=>
                        {
                            if(current.Generation!=item.Generation||current.Attention!=a)return current;
                            updated=true;return current with{Attention=a with{ShellState="Cleared",ShellError=null,ShellOriginalStamp=null}};
                        });
                        _issuePublicationRetry.Remove(item.Id);
                    }
                    continue;
                }
                using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(TimeSpan.FromSeconds(3));
                if(!root.IssueDefinitionReady){await CloudRootRegistration.EnsureIssueDefinitionAsync(root.Definition,limit.Token).ConfigureAwait(false);root.IssueDefinitionReady=true;}
                await CloudUploadIssueProperty.SetAsync(root.Definition,item,before=>
                {
                    var accepted=false;
                    var saved=_repository.UpdateUpload(item.Id,current=>
                    {
                        if(current.Generation!=item.Generation||current.Attention!=a)return current;
                        accepted=true;return current with{Attention=a with{ShellState=a.ResolvedUtc is null?"Publishing":"Clearing",ShellOriginalStamp=before.Key}};
                    });
                    journal=accepted?saved?.Attention:null;return accepted;
                },(before,after)=>
                {
                    root.ConflictMetadata.Record(item.LocalPath,before,after);
                    _repository.UpdateUpload(item.Id,current=>current.Generation!=item.Generation||current.Attention!=journal?current:current with
                    {
                        ObservedStamp=current.ObservedStamp==before.Key||a.ShellOriginalStamp==current.ObservedStamp&&!UploadRules.ContentChanged(current.ObservedStamp,before)?after.Key:current.ObservedStamp,
                        SnapshotStamp=(current.SnapshotStamp==before.Key||a.ShellOriginalStamp is not null&&a.ShellOriginalStamp==current.SnapshotStamp&&!UploadRules.ContentChanged(current.SnapshotStamp!,before))&&current.SnapshotIdentity==before.Identity&&current.SnapshotGeneration==current.Generation?after.Key:current.SnapshotStamp,
                        Attention=a with{ShellState=a.ResolvedUtc is null?"Published":"Cleared",ShellError=null,PublishedIdentity=after.Identity,ShellOriginalStamp=null}
                    });
                },limit.Token,UploadAttentionRules.ShellText(_repository.Uploads(item.MountId),item.LocalPath,item.ObservedStamp.Split(':')[0]),IssuePropertyWrittenForTests is {} written?()=>written(item.Id):null,proof:(before,after)=>
                {
                    root.ConflictMetadata.Record(item.LocalPath,before,after);_repository.UpdateUpload(item.Id,current=>current.Generation!=item.Generation||current.Attention!=journal?current:current with
                    {
                        ObservedStamp=current.ObservedStamp==before.Key?after.Key:current.ObservedStamp,
                        SnapshotStamp=current.SnapshotStamp==before.Key&&current.SnapshotIdentity==before.Identity&&current.SnapshotGeneration==current.Generation?after.Key:current.SnapshotStamp
                    });
                }).ConfigureAwait(false);
                if(IssuePropertyReleasedForTests is {} checkpoint)await checkpoint(item.Id).ConfigureAwait(false);
                _issuePublicationRetry.Remove(item.Id);notify=RelativePaths.ToLocal(root.Definition.LocalPath,item.LocalPath);
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
            catch(Exception error)
            {
                updated=RecordIssuePublicationFailure(attempted,journal,"Explorer提示未发布："+error.Message,error);
            }
            finally
            {
                if(reserved&&runtime is not null)lock(runtime.Sync)runtime.PropertyPath=null;
                root.Gate.Release();if(reserved)runtime?.PulseIdle();if(notify is not null)CloudFilesNative.NotifyItemUpdated(notify);if(notify is not null||updated)Notify();
            }
        }
    }
    /// <summary>仅为本次捕获或已写日志的同代问题保存失败及30秒退避；新问题、成功或取消不继承旧等待和迟到错误。</summary>
    private bool RecordIssuePublicationFailure(LocalUpload attempted,UploadAttention? journal,string reason,Exception? error=null)
    {
        var accepted=false;var changed=false;
        var saved=_repository.UpdateUpload(attempted.Id,current=>
        {
            if(current.Generation!=attempted.Generation||current.Attention is not {} attention||(attention!=attempted.Attention&&attention!=journal))return current;
            accepted=true;changed=attention.ShellError!=reason;return current with{Attention=attention with{ShellError=reason}};
        });
        if(accepted&&saved?.Attention is {} failed)
        {
            _issuePublicationRetry[attempted.Id]=(saved.Generation,failed,DateTimeOffset.UtcNow.AddSeconds(30));
            if(changed){if(error is null)_logger.Queue("同步问题提示","Warning","本地文件缺失，Explorer提示未发布；保留原同步问题。");else _logger.QueueException("同步问题提示","Warning","Explorer提示未发布",error,attempted.Id);}
        }
        return changed;
    }
    /// <summary>只解析本程序根内的IPC目标；不连接、不读取正文、不创建上传或任务。</summary>
    public (Guid MountId,IReadOnlyList<string> Paths) LocateUploadIssues(IReadOnlyList<string> paths)
    {
        if(paths.Count==0)throw new ArgumentException("缺少文件路径。");Guid? mount=null;var relative=new List<string>();
        foreach(var path in paths)
        {
            var root=_roots.Values.SingleOrDefault(x=>MountPathGuard.IsUnder(x.Definition.LocalPath,path))??throw new IOException("目标不在本程序挂载中。");
            if(mount is not null&&mount!=root.Definition.Id)throw new IOException("请只选择同一挂载中的文件。");
            MountPathGuard.ValidateOperation(root.Definition,path);mount=root.Definition.Id;
            var name=Path.GetRelativePath(root.Definition.LocalPath,path).Replace('\\','/');relative.Add(name=="."?"":RelativePaths.Normalize(name));
        }
        return(mount!.Value,relative);
    }
    /// <summary>打开所在位置的安全路径，不要求文件仍存在；页面负责Shell显示。</summary>
    public string UploadIssueLocalPath(string id)
    {
        var upload=_repository.Upload(id)??throw new IOException("上传记录不存在。");var root=Root(upload.MountId);
        var path=RelativePaths.ToLocal(root.Definition.LocalPath,upload.LocalPath);var existing=path;
        while(LocalFileMetadata.TryRead(existing) is null&&Path.GetDirectoryName(existing) is {} parent)existing=parent;
        MountPathGuard.ValidateOperation(root.Definition,existing);return path;
    }
}
