using System.Text.Json;
using HybridMount.Common;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>测试交错点只作用于本实例；生产不通过配置或IPC开放。</summary>
    internal Func<string,Task>? ConflictCheckpointForTests{get;set;}
    internal Action? ConflictWriteForTests{get;set;}
    /// <summary>冲突列表来自持久事务，清除展示历史不丢失上传或远端冲突。</summary>
    public IReadOnlyList<MountOperationSnapshot> Conflicts=>_repository.Uploads().Where(x=>x.State=="Conflict"||x.Resolution is not null).Select(x=>x.ToSnapshot())
        .Concat(_roots.Keys.SelectMany(_repository.AllRemotePending).Where(x=>x.State=="Conflict"||x.State is "Committing" or "Hydrating"&&x.Error is not null).Select(x=>RemoteTask(x))).ToArray();
    /// <summary>同步问题也包括远端更新冲突；相同路径的独立事务保持各自ID。</summary>
    public IReadOnlyList<MountOperationSnapshot> SyncIssues=>UploadIssues.Concat(Conflicts).DistinctBy(x=>x.Id).ToArray();
    /// <summary>仅解析本机索引路径供Shell定位；不联网、不下载。</summary>
    public string SyncIssueLocalPath(string id)
    {
        if(_repository.Upload(id) is not null)return UploadIssueLocalPath(id);
        var pending=_roots.Keys.SelectMany(_repository.AllRemotePending).FirstOrDefault(x=>x.Id==id)??throw new IOException("同步问题不存在。");
        var root=Root(pending.MountId);var entry=_repository.Entries(pending.MountId).FirstOrDefault(x=>x.SourcePath==pending.Path);
        return RelativePaths.ToLocal(root.Definition.LocalPath,entry?.LocalPath??pending.Path);
    }
    private bool HasConflictResolution(Guid mount,string path)=>_repository.Uploads(mount).Any(x=>x.Resolution is not null&&MountRepository.ConflictRelated(path,x.SourcePath));
    /// <summary>比较仅查询元数据，不水合、不计算正文摘要、不执行写能力探测；网络期间不持根锁。</summary>
    public Task<ConflictComparison> CompareConflictAsync(string taskId,CancellationToken token=default)=>OwnAsync(cancel=>Task.Run(()=>CompareConflictCoreAsync(taskId,cancel),cancel),token);
    private async Task<ConflictComparison> CompareConflictCoreAsync(string taskId,CancellationToken token)
    {
        var selected=_repository.Upload(taskId);var remoteTask=selected is null?_roots.Keys.SelectMany(_repository.AllRemotePending).FirstOrDefault(x=>x.Id==taskId):null;
        var root=Root(selected?.MountId??remoteTask?.MountId??throw new IOException("冲突任务不存在。"));
        MountDefinition definition;CloudMountSession session;IFileSource source;LocalUpload? upload;MountEntry? entry;LocalFileStamp? stamp=null;string pending,path,localError="";
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(root.RemovalRequested||root.Definition.RemovalPending||root.Session is null||root.Source is null)throw new IOException("请先连接挂载，再核对冲突。");
            definition=root.Definition;session=root.Session;source=root.Source;
            if(selected is not null)
            {upload=_repository.Upload(taskId);if(upload is null||upload.IsCopy||upload.State is not("Conflict" or "Failed" or "ResolutionPending"))throw new IOException("请选择仍未解决的文件冲突。");path=upload.SourcePath;}
            else
            {
                remoteTask=_repository.AllRemotePending(definition.Id).FirstOrDefault(x=>x.Id==taskId);
                if(remoteTask is null||remoteTask.State!="Conflict")throw new IOException("远端冲突状态已变化，请重新选择。");
                if(root.RemoteSync?.Engine.Scope!=remoteTask.Scope)throw new IOException("此冲突属于旧连接范围，请重新检测当前远端。");
                path=remoteTask.Path;upload=_repository.Uploads(definition.Id).FirstOrDefault(x=>!x.IsCopy&&x.SourcePath==path);
            }
            entry=_repository.Entries(definition.Id).FirstOrDefault(x=>upload?.EntryId is {} id?x.Id==id:x.SourcePath==path);
            var local=upload?.LocalPath??entry?.LocalPath??path;
            if(local!=path)throw new IOException("改名或映射路径不同的冲突请先另存副本，本版不直接替换。");
            var absolute=RelativePaths.ToLocal(definition.LocalPath,local);MountPathGuard.ValidateOperation(definition,absolute);
            try{stamp=LocalFileState.Read(absolute);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){localError="本地元数据读取失败："+error.Message;}
            pending=_repository.ConflictPendingJson(definition.Id,path);
        }
        finally{root.Gate.Release();}
        SourceEntry? remote=null;string remoteError="";
        try{remote=await source.TryGetMetadataAsync(path,token).ConfigureAwait(false);if(remote is null)remoteError="远端文件已不存在；请另存副本或重新检测。";}
        catch(OperationCanceledException){throw;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException){remoteError="远端元数据读取失败："+error.Message;}
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if(root.Definition!=definition||root.Session!=session||root.Source!=source||root.RemovalRequested||root.Definition.RemovalPending||
                upload is not null&&_repository.Upload(upload.Id)!=upload||_repository.ConflictPendingJson(definition.Id,path)!=pending||
                _repository.Entries(definition.Id).FirstOrDefault(x=>entry is null?x.SourcePath==path:x.Id==entry.Id)!=entry)
                throw new SourceConflictException("核对期间会话、索引或任务变化，请重新核对。");
            var absolute=RelativePaths.ToLocal(definition.LocalPath,path);if(stamp is not null&&LocalFileState.Read(absolute).Key!=stamp.Key)throw new SourceConflictException("核对期间本地文件变化，请重新核对。");
            var common=localError.Length!=0?localError:remoteError;
            if(common.Length==0&&(stamp!.IsDirectory||remote!.IsDirectory||entry?.IsDirectory==true))common="文件／目录类型冲突需先另存处理，本版仅直接替换普通文件。";
            var related=_repository.Uploads(definition.Id).Where(x=>x.Id!=upload?.Id&&UploadRules.Unfinished(x)&&MountRepository.ConflictRelated(x.SourcePath,path)).ToArray();
            if(common.Length==0&&(related.Length!=0||upload is not null&&UploadRules.InFlight(upload)))common="相关路径仍有上传或结果待核对，请先完成核对。";
            if(common.Length==0&&_repository.HasConflictHints(definition.Id,path))common="远端事件仍待完整核对，请先刷新。";
            var events=JsonSerializer.Deserialize<RemotePendingChange[]>(pending)??[];
            if(common.Length==0&&events.Any(x=>x.Path!=path||x.State is "Committing" or "Hydrating"))common="相关目录或本地提交尚未完成，不能绕过其保护。";
            var info=stamp is null?null:CloudFilesNative.Inspect(absolute);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            if(common.Length==0&&info is not null&&(entry is null||marker?.MountId!=definition.Id||marker.EntryId!=entry.Id))common="占位身份未通过核验，未提供直接覆盖。";
            if(common.Length==0&&(info is not null&&!CloudFilesNative.HasFullContent(absolute,stamp!.Length)))common="本地文件尚未完整下载；为保留全部本地内容，请先打开或下载后再处理。";
            if(common.Length==0&&entry is not null&&session.IsActive(entry.Id))common="文件正在读取或下载，请稍后重新核对。";
            var localRestriction=common;
            if(localRestriction.Length==0&&FileExclusionRules.Matches(definition.ExclusionRules,path))localRestriction="文件匹配上传排除规则，请先调整规则。";
            if(localRestriction.Length==0&&source.WriteCapabilities.ConditionalUpdate==CapabilitySupport.Unsupported)localRestriction="服务器不支持可靠的条件更新，请另存副本。";
            if(localRestriction.Length==0&&upload?.Resolution?.Phase=="Committing")localRestriction="上次本地替换中断；先使用远端完成恢复，原始本地备份仍保留。";
            return new(){TaskId=taskId,Definition=definition,Session=session,Source=source,Upload=upload,Entry=entry,Stamp=stamp,Remote=remote,PendingJson=pending,Path=path,
                LocalText=stamp is null?localError:$"{stamp.Length:N0} 字节\n修改于 {DateTime.FromFileTimeUtc(stamp.LastWriteTime).ToLocalTime():G}\n{(info is null?"本地普通文件":CloudFilesNative.HasFullContent(absolute,stamp.Length)?"本地完整内容":"部分占位")}\n当前本地内容（可能不同于原上传快照）",
                RemoteText=remote is null?remoteError:$"{remote.Size:N0} 字节\n修改于 {remote.ModifiedUtc.ToLocalTime():G}\n版本：{remote.Version}",
                BaselineText="原同步版本："+(upload?.ExpectedVersion??entry?.Version??"无（本地新增）")+(upload?.Resolution is {} recovery?"\n上次阶段："+recovery.Phase+"\n恢复备份："+recovery.BackupPath:""),
                LocalRestriction=localRestriction,RemoteRestriction=common};
        }
        finally{root.Gate.Release();}
    }
    private void ValidateConflict(Runtime root,ConflictComparison comparison)
    {
        if(root.Definition!=comparison.Definition||root.Session!=comparison.Session||root.Source!=comparison.Source||root.RemovalRequested||root.Definition.RemovalPending||IsStopping)
            throw new SourceConflictException("挂载连接或设置变化，请重新核对。");
        if(comparison.Upload is not null&&_repository.Upload(comparison.Upload.Id)!=comparison.Upload||comparison.Upload is null&&_repository.Uploads(comparison.Definition.Id).Any(x=>!x.IsCopy&&x.SourcePath==comparison.Path)||
            _repository.ConflictPendingJson(comparison.Definition.Id,comparison.Path)!=comparison.PendingJson||_repository.Entries(comparison.Definition.Id).FirstOrDefault(x=>comparison.Entry is null?x.SourcePath==comparison.Path:x.Id==comparison.Entry.Id)!=comparison.Entry)
            throw new SourceConflictException("任务、索引或远端事件变化，请重新核对。");
        var path=RelativePaths.ToLocal(comparison.Definition.LocalPath,comparison.Path);MountPathGuard.ValidateOperation(comparison.Definition,path);
        if(comparison.Stamp is null||LocalFileState.Read(path).Key!=comparison.Stamp.Key)throw new SourceConflictException("本地文件在确认后变化，未覆盖。");
    }
    /// <summary>两个直接选择的实际执行入口；调用方必须传入本次比较，不接受裸路径或新的远端版本。</summary>
    public Task<string> ResolveConflictAsync(ConflictComparison comparison,bool useLocal,CancellationToken token=default)
        =>OwnAsync(cancel=>Task.Run(()=>ResolveConflictCoreAsync(comparison,useLocal,cancel),cancel),token);
    private async Task<string> ResolveConflictCoreAsync(ConflictComparison comparison,bool useLocal,CancellationToken token)
    {
        if(!(useLocal?comparison.CanUseLocal:comparison.CanUseRemote))throw new IOException(useLocal?comparison.LocalRestriction:comparison.RemoteRestriction);
        var root=Root(comparison.Definition.Id);var remoteRuntime=root.RemoteSync;var uploads=root.Uploads;var serial=false;var reserved=false;LocalUpload? intent=null;LocalUpload? completed=null;
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(token,uploads?.Stop.Token??token,remoteRuntime?.Stop.Token??token);token=cancel.Token;
        var local=RelativePaths.ToLocal(comparison.Definition.LocalPath,comparison.Path);var stagedUpload="";var published=false;
        try
        {
            if(remoteRuntime is not null){await remoteRuntime.Serial.WaitAsync(token).ConfigureAwait(false);serial=true;}
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();ValidateConflict(root,comparison);
                if(root.Uploads!=uploads||root.RemoteSync!=remoteRuntime||uploads is null)throw new IOException("上传队列尚未就绪，请重新连接。");
                lock(uploads.Sync){if(uploads.CurrentId is not null||uploads.PropertyPath is not null)throw new IOException("当前挂载仍有文件任务执行，请稍后重试。");uploads.PropertyPath=comparison.Path;reserved=true;}
                var id=Guid.NewGuid().ToString("N");var directory=ConflictDirectory(comparison.Definition.Id,id);var entry=comparison.Entry??new(Guid.NewGuid().ToString("N"),comparison.Path,comparison.Path,comparison.Remote!.Version,false);
                var recovery=new ConflictResolution(id,useLocal?"Local":"Remote","Preparing",comparison.Remote!,comparison.Stamp!,entry,comparison.PendingJson,Path.Combine(directory,"local.snapshot"),PreviousRecoveryPath:comparison.Upload?.Resolution is {Phase:"Committing"} prior?prior.PreviousRecoveryPath??prior.BackupPath:null);
                var previous=comparison.Upload??new(Guid.NewGuid().ToString("N"),comparison.Definition.Id,comparison.Path,comparison.Path,false,comparison.Stamp!.Key,0,"Conflict",DateTimeOffset.UtcNow,entry.Id,comparison.Entry?.Version);
                intent=_repository.BeginConflict(comparison.Upload,comparison.Entry,previous with{State="ResolutionPending",Resolution=recovery,CancelRequested=false,Error="正在准备用户选择的冲突处理；本地备份完成后才允许写入。"},comparison.PendingJson);PublishUpload(intent);
            }
            finally{root.Gate.Release();}
            var backupHash=await UploadPreparation.CopyAsync(local,intent.Resolution!.BackupPath,comparison.Stamp!,token).ConfigureAwait(false);
            intent=AdvanceConflict(intent,r=>r with{BackupSha256=backupHash,Phase="Prepared"});
            if(ConflictCheckpointForTests is {} checkpoint)await checkpoint("BackedUp").ConfigureAwait(false);
            var remote=await comparison.Source.GetMetadataAsync(comparison.Path,token).ConfigureAwait(false);
            if(remote.IsDirectory||!comparison.Source.IsSameVersion(remote.Version,comparison.Remote!.Version)||remote.Size!=comparison.Remote.Size)throw new SourceConflictException("远端版本在确认后变化，请重新核对；未覆盖任何一端。");
            if(useLocal)
            {
                var directory=Path.Combine(_repository.DataDirectory,"UploadStaging",comparison.Definition.Id.ToString("N"));Directory.CreateDirectory(directory);stagedUpload=Path.Combine(directory,Guid.NewGuid().ToString("N")+".snapshot");
                File.Copy(intent.Resolution!.BackupPath,stagedUpload,false);using(var file=new FileStream(stagedUpload,FileMode.Open,FileAccess.Write,FileShare.None))file.Flush(true);
                await root.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();ValidateConflictSession(root,comparison,intent);var stamp=LocalFileState.Read(local);if(stamp.Key!=comparison.Stamp!.Key)throw new SourceConflictException("准备期间本地文件变化，请重新核对。");
                    if(FileExclusionRules.Matches(root.Definition.ExclusionRules,comparison.Path))throw new IOException("文件已匹配排除规则，未上传。");
                    var prepared=intent with{Generation=checked(intent.Generation+1),State="Prepared",ExpectedVersion=remote.Version,BaselineSha256=_repository.Baseline(intent.MountId,intent.SourcePath,remote.Version),
                        SnapshotPath=stagedUpload,SnapshotSha256=backupHash,SnapshotStamp=stamp.Key,SnapshotIdentity=stamp.Identity,SnapshotGeneration=checked(intent.Generation+1),SnapshotAttemptId=null,
                        Total=stamp.Length,Bytes=0,Receipt=null,Error=null,CancelRequested=false,ExplicitRequest=true,LastCheckedUtc=null,AutoRetryCount=0,NextAttemptUtc=null,TransientFailureCount=0,
                        Resolution=null,ConflictRecoveryPath=intent.Resolution!.BackupPath};
                    var result=_repository.PublishConflictLocal(intent,prepared);published=true;PublishUpload(result);
                }
                finally{root.Gate.Release();}
                return "已按确认的远端版本提交条件上传；远端再次变化时仍会保留冲突。";
            }
            var remotePath=Path.Combine(Path.GetDirectoryName(intent.Resolution!.BackupPath)!,"remote.snapshot");
            var remoteHash=await RemoteStaging.DownloadAsync(comparison.Source,remote,remotePath,token).ConfigureAwait(false);
            intent=AdvanceConflict(intent,r=>r with{RemotePath=remotePath,RemoteSha256=remoteHash});
            if(ConflictCheckpointForTests is {} preparedPoint)await preparedPoint("Downloaded").ConfigureAwait(false);
            var finalRemote=await comparison.Source.GetMetadataAsync(comparison.Path,token).ConfigureAwait(false);
            if(finalRemote.IsDirectory||!comparison.Source.IsSameVersion(finalRemote.Version,remote.Version)||finalRemote.Size!=remote.Size)throw new SourceConflictException("下载后远端再次变化，请重新核对。");
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();ValidateConflictSession(root,comparison,intent);
                if(LocalFileState.Read(local).Key!=comparison.Stamp!.Key)throw new SourceConflictException("下载期间本地文件变化，未替换。");
                _previews.Invalidate(root.Definition,intent.Resolution!.Entry.Id);
                var final=CloudConflictReplacement.Apply(root.Definition,intent,stamp=>completed=_repository.CommitConflictRemote(intent,stamp),()=>intent=AdvanceConflict(intent,r=>r with{Phase="Committing"}),ConflictWriteForTests);
                root.Session!.RecordLocalWrite(intent.Resolution!.Entry,final);
            }
            finally{root.Gate.Release();}
            PublishUpload(completed);NotifyFileState(local);DeleteUploadSnapshot(comparison.Upload?.SnapshotPath);
            return "已使用已核验的远端内容替换本地。原本地内容备份："+completed!.ConflictRecoveryPath;
        }
        catch(Exception error)
        {
            if(intent is not null&&completed is null)
            {
                var current=_repository.Upload(intent.Id);
                if(current?.Resolution?.Id==intent.Resolution?.Id)PublishUpload(_repository.UpdateUpload(intent.Id,x=>x.Resolution?.Id!=intent.Resolution?.Id?x:x with{State="ResolutionPending",Error="冲突处理未完成："+error.Message+"；请重新核对后明确选择，恢复备份保留。"}));
            }
            throw;
        }
        finally
        {
            if(!published&&stagedUpload.Length!=0)DeleteUploadSnapshot(stagedUpload);
            if(reserved&&uploads is not null){lock(uploads.Sync)if(uploads.PropertyPath==comparison.Path)uploads.PropertyPath=null;uploads.Wake();}
            if(serial)remoteRuntime!.Serial.Release();Notify();
        }
    }
    private LocalUpload AdvanceConflict(LocalUpload captured,Func<ConflictResolution,ConflictResolution> update)
    {
        var accepted=false;var next=_repository.UpdateUpload(captured.Id,current=>{if(current!=captured)return current;accepted=true;return current with{Resolution=update(current.Resolution!)};});
        if(!accepted||next is null)throw new SourceConflictException("冲突处理记录已变化，保留备份并停止。");return next;
    }
    private void ValidateConflictSession(Runtime root,ConflictComparison comparison,LocalUpload intent)
    {
        if(root.Definition!=comparison.Definition||root.Session!=comparison.Session||root.Source!=comparison.Source||root.RemovalRequested||root.Definition.RemovalPending||IsStopping||
            _repository.Upload(intent.Id)!=intent||_repository.ConflictPendingJson(intent.MountId,intent.SourcePath)!=comparison.PendingJson||
            _repository.Entries(intent.MountId).FirstOrDefault(x=>comparison.Entry is null?x.SourcePath==comparison.Path:x.Id==comparison.Entry.Id)!=comparison.Entry)
            throw new SourceConflictException("处理期间连接、设置或事务变化，保留备份并停止。");
        if(_repository.HasConflictHints(intent.MountId,intent.SourcePath)||_repository.Uploads(intent.MountId).Any(x=>x.Id!=intent.Id&&UploadRules.Unfinished(x)&&MountRepository.ConflictRelated(x.SourcePath,intent.SourcePath)))throw new SourceConflictException("处理期间相关路径出现新任务，请重新核对。");
        if(comparison.Entry is {} active&&root.Session!.IsActive(active.Id))throw new IOException("文件正在读取或下载，请稍后重新核对。");
        MountPathGuard.ValidateOperation(root.Definition,RelativePaths.ToLocal(root.Definition.LocalPath,comparison.Path));
    }
    /// <summary>只在应用数据根内建立独占恢复目录；拒绝任一级联接，不将备份置于同步根内。</summary>
    private string ConflictDirectory(Guid mount,string id)
    {
        var directory=Path.Combine(_repository.DataDirectory,"ConflictRecovery",mount.ToString("N"),id);
        if(_roots.Values.Any(x=>MountPathGuard.IsUnder(x.Definition.LocalPath,directory)))throw new IOException("恢复目录不能位于任何同步根内。");
        Directory.CreateDirectory(directory);
        for(var cursor=directory;;cursor=Path.GetDirectoryName(cursor)!)
        {if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new IOException("恢复目录包含重解析点，拒绝写入。");if(cursor.Equals(_repository.DataDirectory,StringComparison.OrdinalIgnoreCase))break;}
        return directory;
    }
}
