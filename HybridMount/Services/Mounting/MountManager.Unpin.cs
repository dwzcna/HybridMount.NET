using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>只消费已有本地释放意图；等待/退避不占云额度，单根串行而不持根锁跨本地解码或原生操作。</summary>
    private async Task<long> CompletePendingUnpinsAsync(CancellationToken token)
    {
        long freed=0;foreach(var root in _roots.Values)freed+=await CompletePendingUnpinsAsync(root,token).ConfigureAwait(false);return freed;
    }
    private async Task<long> CompletePendingUnpinsAsync(Runtime root,CancellationToken token,IReadOnlyList<MountEntry>? candidates=null,bool wait=false)
    {
        var serial=_releaseGates.GetOrAdd(root.Definition.Id,_=>new(1,1));
        if(wait)await serial.WaitAsync(token).ConfigureAwait(false);else if(!await serial.WaitAsync(0,token).ConfigureAwait(false))return 0;
        long freed=0;
        try
        {
            if(root.Session is not {} session||root.RemovalRequested||root.Definition.RemovalPending)return 0;
            var entries=_repository.Entries(root.Definition.Id);var history=_repository.LoadFileOperations(root.Definition.Id).ToDictionary(row=>row.Id);
            foreach(var entry in candidates??entries)
            {
                token.ThrowIfCancellationRequested();if(entry.IsDirectory||entry.Deleted||!entry.Materialized)continue;
                var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);FileOperation? task=null;
                try
                {
                    MountPathGuard.ValidateOperation(root.Definition,path);
                    var observed=CloudFilesNative.InspectPersisted(path);
                    var id="release:"+root.Definition.Id+":"+entry.Id;
                    if(!_fileOperations.TryGetValue(id,out var saved)&&history.TryGetValue(id,out var previous)&&previous.State is "Deferred" or "Running" or "Interrupted")saved=FileTask(root,session,entry,"freeSpace",id);
                    task=saved;
                    var marker=MountPathGuard.ReadEntry(observed.Identity);
                    if(marker?.MountId!=root.Definition.Id||marker.EntryId!=entry.Id)throw new IOException("释放意图不属于此文件，已保留内容。");
                    if(saved?.Snapshot.OperationFileId is {} expected&&LocalFileState.Read(path).Identity!=expected){PublishFileTask(saved,"Failed","releaseIdentity",new IOException("释放文件身份已变化。"));continue;}
                    if(saved?.Snapshot.OperationVersion is {} expectedVersion&&expectedVersion!=entry.Version){PublishFileTask(saved,"Failed","releaseVersion",new IOException("释放内容版本已变化，请重新核对。"));continue;}
                    if(observed.Info.PinState!=2)
                    {
                        if(_fileOperations.TryGetValue(id,out var old)&&old.Snapshot.State is "Deferred" or "Running")PublishFileTask(old,"Superseded",reason:"文件的固定或释放意图已改变。");
                        continue;
                    }
                    // 已冷项目没有新的释放工作，现有暂缓项则核验后收尾。
                    if(observed.Info.OnDiskSize==0&&!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length))
                    {if(_fileOperations.TryGetValue(id,out var done)&&done.Snapshot.State is "Deferred" or "Running")PublishFileTask(done,"Completed");continue;}
                    if(saved is null&&history.TryGetValue(id,out var failed)&&failed.State=="Failed")continue;
                    task=FileTask(root,session,entry,"freeSpace",id);
                    if(task.Snapshot.OperationFileId!=LocalFileState.Read(path).Identity)throw new IOException("释放前文件身份已变化，保留内容。");
                    if(task.Snapshot.State=="Failed")continue;
                    string? reason=null;
                    await root.Gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if(!ReferenceEquals(root.Session,session)||root.RemovalRequested)return freed;
                        if(entry.Protected||observed.Info.ModifiedSize>0||observed.Info.InSyncState!=1||FileExclusionRules.Matches(root.Definition.ExclusionRules,entry.LocalPath))reason="文件被本地修改或排除规则保护。";
                        else if(_repository.Uploads(root.Definition.Id).Any(upload=>upload.LocalPath==entry.LocalPath&&upload.State!="Completed"))reason="等待本地上传完成。";
                        else if(_repository.HasPendingRemote(root.Definition.Id,entry.SourcePath)||entry.RemotePresence==RemotePresence.Deleted||UploadRules.DeletedSourceAncestor(entries,entry.SourcePath) is not null)reason="等待远端变更核对。";
                        else if(session.IsActive(entry.Id))reason="文件仍有活动读取，等待占用释放。";
                    }
                    finally{root.Gate.Release();}
                    if(reason is not null){PublishFileTask(task,"Deferred","releaseGuard",reason:reason);continue;}
                    PublishFileTask(task,"Running");
                    await PreserveBeforeDehydrateAsync(root,entry,token).ConfigureAwait(false);
                    var bytes=session.CompletePendingUnpin(entry);
                    if(bytes is null)
                    {
                        var after=CloudFilesNative.InspectPersisted(path);
                        PublishFileTask(task,after.Info.PinState!=2?"Superseded":after.Info.OnDiskSize==0&&!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length)?"Completed":"Deferred","dehydratePending",reason:after.Info.OnDiskSize>0?"等待释放保护条件就绪。":null);
                    }
                    else
                    {
                        freed+=bytes.Value;var after=CloudFilesNative.InspectPersisted(path);
                        var released=after.Info.OnDiskSize==0&&!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length);
                        PublishFileTask(task,after.Info.PinState!=2?"Superseded":released?"Completed":"Deferred",released?null:"dehydratePending",reason:released?null:"等待释放保护条件就绪。");NotifyFileState(path);
                    }
                }
                catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
                {
                    if(task is not null)PublishFileTask(task,ReleaseTransient(error)?"Deferred":"Failed","dehydratePending",error);
                    else _logger.QueueException("释放空间","Warning","释放意图核验失败，文件继续保护",error,entry.Id);
                }
            }
        }
        finally{serial.Release();RefreshReleaseParents(root);}
        return freed;
    }
    /// <summary>显式入口先设置原生UNPINNED意图，再由同一执行器核验释放；目录本身没有正文。</summary>
    private async Task<MountOperationSnapshot?> RequestReleaseAsync(Runtime root,MountEntry entry,CancellationToken token)
    {
        var session=root.Session??throw new IOException("挂载已断开。");
        if(entry.IsDirectory){await session.Runtime.InvokeAsync("requestUnpin",session.EntryArgs(entry),token:token).ConfigureAwait(false);return null;}
        var task=FileTask(root,session,entry,"freeSpace","release:"+root.Definition.Id+":"+entry.Id,renew:true);
        try{await session.Runtime.InvokeAsync("requestUnpin",session.EntryArgs(entry),token:token).ConfigureAwait(false);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {PublishFileTask(task,ReleaseTransient(error)?"Deferred":"Failed","requestUnpin",error);return task.Snapshot;}
        PublishFileTask(task,"Deferred",reason:"等待本地释放核验。");
        await CompletePendingUnpinsAsync(root,token,[entry],wait:true).ConfigureAwait(false);return task.Snapshot;
    }
    /// <summary>显式批次只聚合自身已暂缓的条目；子项收尾后更新原批次，不新增历史或误报成功。</summary>
    private void RefreshReleaseParents(Runtime root)
    {
        var completed=_repository.LoadFileOperations(root.Definition.Id).Where(row=>row.Operation=="freeSpace").ToArray();
        foreach(var job in _jobs.Values.Where(job=>job.Root==root.Definition.Id&&job.Snapshot.Operation=="freeSpace"&&job.Snapshot.State=="Waiting"))
        {
            var results=job.Snapshot.Results.Select(result=>
            {
                if(result.Status!="Waiting")return result;
                var task=completed.FirstOrDefault(row=>row.ItemPath is {} relative&&RelativePaths.ToLocal(root.Definition.LocalPath,relative).Equals(result.Path,StringComparison.OrdinalIgnoreCase));
                return task?.State switch{"Completed"=>result with{Status="Completed",Message="释放已完成。"},"Failed"=>result with{Status="Failed",Message=task.Error??"释放失败。"},"Superseded"=>result with{Status="Skipped",Message="释放意图已改变。"},_=>result};
            }).ToArray();
            if(results.SequenceEqual(job.Snapshot.Results))continue;
            job.Results.Clear();job.Results.AddRange(results);job.Snapshot=job.Snapshot with{Results=results};Update(job,results.Any(row=>row.Status=="Waiting")?"Waiting":results.Any(row=>row.Status=="Failed")?"PartialFailure":"Completed");
        }
    }
}
