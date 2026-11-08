using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>实例级受控交错屏障，不经配置、日志或 IPC 启用；调用时不持根锁。</summary>
    internal Func<string,RemotePendingChange,Task>? RemoteCheckpointForTests {get;set;}
    /// <summary>逐项应用已持久化事件；网络快照在根锁外下载，提交重新核对会话、身份及上传代次。</summary>
    private async Task ApplyRemotePendingAsync(Runtime root,RemoteSyncRuntime runtime,CancellationToken token)
    {
        foreach(var obsolete in _repository.RemotePending(root.Definition.Id,runtime.Engine.Scope).Where(x=>x.State=="Superseded"))DeleteRemoteSnapshot(obsolete.SnapshotPath);
        var pending=_repository.RemotePending(root.Definition.Id,runtime.Engine.Scope)
            .Where(x=>x.State is not("Completed" or "Superseded" or "Canceled"))
            .OrderBy(x=>x.Entry is null?1:0).ThenBy(x=>x.Path.Count(c=>c=='/')).ToArray();
        foreach(var prior in pending)
        {
            token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);
            if(runtime.StagingRetries.ShouldWait(prior,DateTimeOffset.UtcNow)||HasConflictResolution(root.Definition.Id,prior.Path))continue;
            using var request=CancellationTokenSource.CreateLinkedTokenSource(token);
            lock(runtime.Sync){runtime.CurrentId=prior.Id;runtime.Request=request;}
            var current=prior;
            try
            {
                current=_repository.RemotePending(root.Definition.Id,runtime.Engine.Scope).First(x=>x.Id==prior.Id);
                if(current.State is "Completed" or "Canceled" or "Superseded")continue;
                if(!await SuppressExcludedRenameEchoAsync(root,runtime,current,request.Token).ConfigureAwait(false)&&!await CompleteUnchangedObservationAsync(root,runtime,current,request.Token).ConfigureAwait(false))
                    current=await ApplyRemoteItemAsync(root,runtime,current,request.Token).ConfigureAwait(false);
                else current=_repository.RemotePending(root.Definition.Id,runtime.Engine.Scope).First(x=>x.Id==current.Id);
                if(current.State=="Completed")DeleteRemoteSnapshot(current.SnapshotPath);
            }
            catch(OperationCanceledException)when(request.IsCancellationRequested)
            {
                if(!token.IsCancellationRequested)SaveRemote(current with{State="Canceled",Error="已取消此远端版本，请显式重试。"});
                if(token.IsCancellationRequested)throw;
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            {
                var latest=_repository.RemotePending(root.Definition.Id,runtime.Engine.Scope).First(x=>x.Id==prior.Id);
                // 已开始本地提交必须保留原阶段及证据，不能转为新下载或覆盖失败。
                var state=latest.State is "Committing" or "Hydrating"?latest.State:error is SourceConflictException?"Conflict":"Waiting";
                if(error is StagingSpaceException&&state=="Waiting")runtime.StagingRetries.Defer(latest.Id,DateTimeOffset.UtcNow);
                SaveRemote(latest with{State=state,Error=error.Message});
            }
            finally{lock(runtime.Sync){runtime.Request=null;runtime.CurrentId=null;}}
        }
    }
    /// <summary>迟到旧版本回显必须再读当前元数据；只消除确认已不存在的旧观测，重新出现的真实远端对象仍按正常同步处理。</summary>
    private async Task<bool> SuppressExcludedRenameEchoAsync(Runtime root,RemoteSyncRuntime runtime,RemotePendingChange pending,CancellationToken token)
    {
        if(pending.Entry is not {IsDirectory:false} metadata||!RenameIntents(root.Definition.Id).Any(x=>x.Finished&&x.RemoteMoved&&x.SourcePath==pending.Path&&(x.ExpectedVersion==metadata.Version||x.OriginalVersion==metadata.Version)))return false;
        var current=await runtime.Source.TryGetMetadataAsync(pending.Path,token).ConfigureAwait(false);
        if(current is not null)return false;
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try{if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);SaveRemote(pending with{State="Completed",Error=null});return true;}
        finally{root.Gate.Release();}
    }
    private async Task<RemotePendingChange> ApplyRemoteItemAsync(Runtime root,RemoteSyncRuntime runtime,RemotePendingChange pending,CancellationToken token)
    {
        MountEntry? entry;var pinned=false;
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);
            MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
            if(RenameBlocksSource(root.Definition.Id,pending.Path))return pending;
            var entries=_repository.Entries(root.Definition.Id);entry=entries.FirstOrDefault(x=>x.SourcePath.Equals(pending.Path,StringComparison.OrdinalIgnoreCase));
            var uploads=_repository.Uploads(root.Definition.Id).Where(x=>UploadRules.SourceContains(pending.Path,x.SourcePath)&&UploadRules.Unfinished(x)).ToArray();
            if(uploads.Any(UploadRules.InFlight))throw new IOException("同路径上传已发送，等待其回执或未知结果核对后再应用远端变化。");
            if(pending.Entry is not {} metadata)
            {
                var missing=entries.Where(x=>UploadRules.SourceContains(pending.Path,x.SourcePath)).ToArray();
                var results=ApplyRemoteDeletions(root,missing,token);
                if(results.Any(x=>x.Status!="Completed"))throw new SourceConflictException(string.Join("；",results.Where(x=>x.Status!="Completed").Select(x=>x.Message).Distinct()));
                return SaveRemote(pending with{State="Completed",Error=null});
            }
            if(entry is not null&&entry.IsDirectory!=metadata.IsDirectory)throw new SourceConflictException("远端文件与目录类型互换，保持本地保护，不能自动替换。");
            // 自己上传的回显不再下载；先确认同一索引版本，并且没有中断的本地提交。
            if(entry is not null&&pending.State is not("Committing" or "Hydrating")&&runtime.Source.IsSameVersion(entry.Version,metadata.Version)&&entry.RemotePresence!=RemotePresence.Deleted)
            {
                _repository.SetRemotePresence(root.Definition.Id,entry,RemotePresence.Present);
                return SaveRemote(pending with{State="Completed",Error=null});
            }
            if(uploads.Length!=0)
            {
                foreach(var upload in uploads)ChangeUpload(upload.Id,x=>x with{State="Conflict",Error="远端内容已变化，保留本地修改；请重试原始基线或另存副本。"});
                throw new SourceConflictException("存在未发送的本地修改，已停止旧基线上传，保留两端内容。");
            }
            if(entry?.Deleted==true)return SaveRemote(pending with{State="Completed",Error="本地删除意图保留，未重新创建占位。"});
            var parent=entries.FirstOrDefault(x=>x.SourcePath.Equals(RelativePaths.Parent(metadata.Path),StringComparison.OrdinalIgnoreCase));
            if(parent is {Protected:true} or {Deleted:true})throw new SourceConflictException("父目录含本地修改或删除意图，子项保留待处理。");
            entry??=new(Guid.NewGuid().ToString("N"),metadata.Path,parent is null?metadata.Path:parent.LocalPath+"/"+Path.GetFileName(metadata.Path),metadata.Version,metadata.IsDirectory);
            var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);
            if(entry.RemotePresence==RemotePresence.Deleted&&LocalMissing(path))
            {_repository.CompleteRemoteCleanup(root.Definition.Id,entry);entry=entry with{Materialized=false,RemotePresence=RemotePresence.Present};}
            _repository.SetRemotePresence(root.Definition.Id,entry,RemotePresence.Present);
            if(LocalMissing(path)&&!entry.Materialized)
            {
                entry=entry with{Version=metadata.Version};_repository.SaveEntry(root.Definition.Id,entry);root.Session!.CreatePlaceholder(entry,metadata);
                _repository.SetRemotePresence(root.Definition.Id,entry,RemotePresence.Present);
                return SaveRemote(pending with{State="Completed",Error=null});
            }
            if(RenameIntents(root.Definition.Id).Any(x=>x.LocalDetached&&x.MappingRebound&&x.FileId==LocalFileState.Read(path).Identity&&x.DestinationPath==metadata.Path))
                throw new SourceConflictException("排除文件的远端版本已变化，保留本地内容，请在冲突视图核对。");
            if(!entry.Materialized||entry.Protected)throw new SourceConflictException("本地新增、修改或身份不明，未覆盖远端变化。");
            MountPathGuard.ValidateOperation(root.Definition,path);
            var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            if(marker?.MountId!=root.Definition.Id||marker.EntryId!=entry.Id||info!.Value.Info.ModifiedSize!=0||info.Value.Info.InSyncState!=1)
                throw new SourceConflictException("本地内容或占位身份已变化，保留两端文件。");
            if(root.Session!.IsActive(entry.Id))throw new IOException("文件正在使用，待下次检测或重试。");
            if(metadata.IsDirectory)
            {_repository.SaveEntry(root.Definition.Id,entry with{Version=metadata.Version});return SaveRemote(pending with{State="Completed",Error=null});}
            pinned=info.Value.Info.PinState==1||pending.State is "Committing" or "Hydrating";
            if(!pinned)
            {
                _previews.Invalidate(root.Definition,entry.Id);
                if(!root.Session.RefreshPlaceholder(entry,metadata))throw new IOException("文件正被使用或无法原生刷新，保留旧版本。");
                _repository.SaveEntry(root.Definition.Id,entry with{Version=metadata.Version});CloudFilesNative.NotifyItemUpdated(path);
                return SaveRemote(pending with{State="Completed",Error=null});
            }
            if(pending.State=="Committing")throw new SourceConflictException("上次进程在占位版本切换时中断；已保留快照和提交日志，请核对本地内容后处理。");
            if(pending.Sha256 is null||pending.SnapshotPath is null||!File.Exists(pending.SnapshotPath))
            {
                var stamp=LocalFileState.Read(path);
                pending=SaveRemote(pending with{State="Downloading",EntryId=entry.Id,OriginalStamp=stamp.Key,OldVersion=entry.Version,PinState=info.Value.Info.PinState,
                    UploadGeneration=_repository.Uploads(root.Definition.Id).Where(x=>x.LocalPath==entry.LocalPath).Select(x=>x.Generation).DefaultIfEmpty().Max(),Error=null});
            }
        }
        finally{root.Gate.Release();}
        token.ThrowIfCancellationRequested();
        if(pinned&&(pending.Sha256 is null||pending.SnapshotPath is null||!File.Exists(pending.SnapshotPath)))pending=await StageRemoteAsync(root,runtime,pending,token).ConfigureAwait(false);
        if(RemoteCheckpointForTests is {} checkpoint)await checkpoint("SnapshotPrepared",pending).ConfigureAwait(false);
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);
            var current=_repository.RemotePending(root.Definition.Id,runtime.Engine.Scope).First(x=>x.Id==pending.Id);
            if(current.State is "Superseded" or "Canceled")return current;
            entry=_repository.Entries(root.Definition.Id).Single(x=>x.Id==pending.EntryId);
            var generation=_repository.Uploads(root.Definition.Id).Where(x=>x.LocalPath==entry.LocalPath).Select(x=>x.Generation).DefaultIfEmpty().Max();
            if(generation!=pending.UploadGeneration||_repository.Uploads(root.Definition.Id).Any(x=>x.LocalPath==entry.LocalPath&&UploadRules.Unfinished(x)))throw new SourceConflictException("快照下载期间本地上传代次变化，未提交远端更新。");
            ValidateRemoteSnapshot(pending);
            _previews.Invalidate(root.Definition,entry.Id);
            await CloudRemoteUpdate.ApplyAsync(root.Definition,entry,pending,root.Session!, (change,updated)=>
            {
                lock(_remoteJournalLock)
                {
                    var active=_repository.RemotePending(change.MountId,change.Scope).First(x=>x.Id==change.Id);
                    if(active.State=="Canceled"&&change.State=="Committing")throw new OperationCanceledException(token);
                    _repository.CommitRemotePlaceholder(change,updated);_repository.SaveOperation(RemoteTask(change,change.State=="Completed"?change.Entry!.Size:0));Notify();
                }
            }).ConfigureAwait(false);
            _previews.Queue(root.Definition,_repository.Entries(root.Definition.Id).Single(x=>x.Id==entry.Id));
            return _repository.RemotePending(root.Definition.Id,runtime.Engine.Scope).First(x=>x.Id==pending.Id);
        }
        finally{root.Gate.Release();}
    }
    /// <summary>已核验的上传回执消除同版本回显屏障；本地新代次继续保留原始内容基线，不等待下个轮询周期。</summary>
    private void CompleteOwnUploadEcho(Runtime root,SourceEntry receipt)
    {
        foreach(var pending in RemotePendingFor(root).Where(x=>x.Entry is not null&&x.Scope==root.RemoteSync?.Engine.Scope&&x.Path==receipt.Path&&x.Entry.IsDirectory==receipt.IsDirectory&&
            x.State is not("Completed" or "Superseded" or "Canceled" or "Committing" or "Hydrating")))
        {
            if(root.Source?.IsSameVersion(pending.Entry!.Version,receipt.Version)!=true)continue;
            SaveRemote(pending with{State="Completed",Error=null});DeleteRemoteSnapshot(pending.SnapshotPath);
        }
        root.RemoteSync?.Wake();
    }
    /// <summary>弱版本回显只在原基线摘要与新观测完整快照相同后等价提交；不改本地内容、同步标记或观察戳。</summary>
    private async Task<bool> CompleteUnchangedObservationAsync(Runtime root,RemoteSyncRuntime runtime,RemotePendingChange pending,CancellationToken token)
    {
        if(pending.Entry is not {IsDirectory:false} metadata||pending.State is "Committing" or "Hydrating"||runtime.Source is not IRemoteChangeSource changeSource)return false;
        MountEntry? original;string? baseline;
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);
            original=_repository.Entries(root.Definition.Id).FirstOrDefault(x=>x.SourcePath==pending.Path&&!x.IsDirectory&&!x.Deleted);
            if(original is null||runtime.Source.IsSameVersion(original.Version,metadata.Version)||!changeSource.IsSameVersionHint(original.Version,metadata.Version)||
                _repository.Uploads(root.Definition.Id).Any(x=>x.SourcePath==pending.Path&&UploadRules.InFlight(x)))return false;
            baseline=_repository.Baseline(root.Definition.Id,pending.Path,original.Version);if(baseline is null)return false;
        }
        finally{root.Gate.Release();}
        if(await HashRemoteAsync(runtime.Source,metadata,token).ConfigureAwait(false)!=baseline)return false;
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);
            MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
            var current=_repository.Entries(root.Definition.Id).FirstOrDefault(x=>x.Id==original.Id);
            var active=_repository.RemotePending(root.Definition.Id,runtime.Engine.Scope).First(x=>x.Id==pending.Id);
            if(current is null||current.Version!=original.Version||active.State is "Canceled" or "Superseded"||_repository.Uploads(root.Definition.Id).Any(x=>x.SourcePath==pending.Path&&UploadRules.InFlight(x)))return false;
            _repository.SaveBaseline(root.Definition.Id,new(metadata.Path,metadata.Version,baseline));
            _repository.SaveEntry(root.Definition.Id,current with{Version=metadata.Version});
            foreach(var upload in _repository.Uploads(root.Definition.Id).Where(x=>x.SourcePath==pending.Path&&x.ExpectedVersion==original.Version&&!UploadRules.InFlight(x)))
                ChangeUpload(upload.Id,x=>x with{ExpectedVersion=metadata.Version,BaselineSha256=x.BaselineSha256??baseline});
            SaveRemote(pending with{State="Completed",Error=null});DeleteRemoteSnapshot(pending.SnapshotPath);return true;
        }
        finally{root.Gate.Release();}
    }
    private async Task<RemotePendingChange> StageRemoteAsync(Runtime root,RemoteSyncRuntime runtime,RemotePendingChange pending,CancellationToken token)
    {
        var metadata=pending.Entry!;var directory=Path.Combine(_repository.DataDirectory,"RemoteStaging",root.Definition.Id.ToString("N"));Directory.CreateDirectory(directory);
        var path=Path.Combine(directory,pending.Id+".snapshot");
        // 先记路径，后落盘；中断的部分文件从零重建，不作为已验证快照使用。
        pending=SaveRemote(pending with{SnapshotPath=path,State="Downloading",Sha256=null});
        try
        {
            ValidateRemoteSnapshot(pending);DeleteRemoteSnapshot(path);
            var last=DateTimeOffset.MinValue;
            var digest=await RemoteStaging.DownloadAsync(runtime.Source,metadata,path,token,offset=>
            {if(DateTimeOffset.UtcNow-last>TimeSpan.FromMilliseconds(200)){_repository.SaveOperation(RemoteTask(pending,offset));Notify();last=DateTimeOffset.UtcNow;}}).ConfigureAwait(false);
            _repository.SaveBaseline(root.Definition.Id,new(metadata.Path,metadata.Version,digest));
            pending=SaveRemote(pending with{Sha256=digest,State="Prepared"});
            return pending;
        }
        catch{DeleteRemoteSnapshot(path);SaveRemote(pending with{SnapshotPath=null,Sha256=null});throw;}
    }
}
