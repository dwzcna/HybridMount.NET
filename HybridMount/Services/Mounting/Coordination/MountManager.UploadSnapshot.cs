using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>实例级确定性测试屏障；生产默认关闭，不经 UI/IPC 配置。提交阶段的回调不得获取根锁。</summary>
    internal Func<string,LocalUpload,Task>? UploadCheckpointForTests { get; set; }
    private Task UploadCheckpointAsync(string stage,LocalUpload item)=>UploadCheckpointForTests?.Invoke(stage,item)??Task.CompletedTask;
    /// <summary>网络发送前短暂打开只读共享的文件，复制到挂载根外并核对身份、USN 和摘要。</summary>
    private async Task<LocalUpload?> PrepareUploadAsync(Runtime root,LocalUpload queued,CancellationToken token)
    {
        if(queued.IsCopy)
        {
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(root.RemovalRequested||root.Definition.RemovalPending||root.Session is null||root.Source is null)throw new OperationCanceledException(token);
                var current=_repository.Upload(queued.Id);
                if(current is not null&&FileExclusionRules.Matches(root.Definition,current)){ChangeUpload(current.Id,FileExclusionRules.Pause);return null;}
                if(current is null||!current.IsCopy||current.CancelRequested||current.State is not("Queued" or "Waiting" or "Prepared" or "WaitingCapacity"))return null;
                if(current.SnapshotPath is not null)
                {
                    var frozen=UploadCopySnapshot.Prepare(current,ValidateUploadSnapshot);
                    var result=ChangeUpload(current.Id,x=>x==current?frozen:x);return result==frozen?result:null;
                }
            }
            finally{root.Gate.Release();}
        }
        LocalUpload? item=null;LocalFileStamp? stamp=null;MountEntry? mapping=null;string? path=null,created=null,digest=null;
        var definition=root.Definition;var source=root.Source;var session=root.Session;var hydrate=false;var reuse=false;var published=false;
        void Validate()
        {
            if(root.RemovalRequested||root.Definition.RemovalPending||root.Session is null||root.Source is null)throw new OperationCanceledException(token);
            if(root.Definition!=definition||root.Source!=source||root.Session!=session)throw new UploadPreparationChangedException();
        }
        return await UploadPreparation.RunAsync(root.Gate,()=>
        {
            definition=root.Definition;source=root.Source;session=root.Session;Validate();
            var current=_repository.Upload(queued.Id);if(current is null||current.CancelRequested)return false;
            if(FileExclusionRules.Matches(definition,current)){ChangeUpload(current.Id,FileExclusionRules.Pause);return false;}
            if(current.State is not("Queued" or "Waiting" or "Prepared" or "WaitingCapacity")||current.Error==UploadRules.TypeConflictMessage)return false;
            path=RelativePaths.ToLocal(definition.LocalPath,current.LocalPath);MountPathGuard.ValidateOperation(definition,path);
            stamp=LocalFileState.Read(path);UploadRules.ValidateTypes(current,stamp);
            mapping=_repository.Entries(current.MountId).FirstOrDefault(x=>x.Id==current.EntryId);
            reuse=current.SnapshotPath is not null&&File.Exists(current.SnapshotPath)&&current.SnapshotStamp==stamp.Key&&current.SnapshotGeneration==current.Generation;
            hydrate=!stamp.IsDirectory&&!reuse&&CloudFilesNative.Inspect(path) is not null&&!CloudFilesNative.HasFullContent(path,stamp.Length);
            if(hydrate&&mapping is null)throw new SourceConflictException("部分占位缺少原始映射，无法安全补齐。");
            var attempt=Guid.NewGuid().ToString("N");
            var reserved=ChangeUpload(current.Id,x=>x==current?x with{SnapshotAttemptId=attempt}:x);
            if(reserved?.SnapshotAttemptId!=attempt)return false;
            item=reserved;return true;
        },async()=>
        {
            if(reuse)return;
            MountPathGuard.ValidateOperation(definition,path!);
            if(hydrate)
            {
                var metadata=await source!.GetMetadataAsync(mapping!.SourcePath,token).ConfigureAwait(false);
                if(!source.IsSameVersion(metadata.Version,item!.ExpectedVersion??mapping.Version))throw new SourceConflictException("远端版本变化，无法补齐本地修改过的部分占位；保留文件。");
                using(var priority=session!.PrioritizeDownload(mapping.Id))
                using(var handle=CloudFilesNative.Open(path!))await CloudFilesNative.HydrateAsync(handle,token).ConfigureAwait(false);
                if(!CloudFilesNative.HasFullContent(path!,new FileInfo(path!).Length))throw new SourceConflictException("修改过的占位尚未完整补齐，未开始上传。");
                return;
            }
            if(!stamp!.IsDirectory)
            {
                var directory=Path.Combine(_repository.DataDirectory,"UploadStaging",item!.MountId.ToString("N"));Directory.CreateDirectory(directory);
                created=Path.Combine(directory,item.SnapshotAttemptId+".snapshot");
                digest=await UploadPreparation.CopyAsync(path!,created,stamp,token,()=>UploadCheckpointAsync("SnapshotCopy",item)).ConfigureAwait(false);
            }
            await UploadCheckpointAsync("SnapshotCopied",item!).ConfigureAwait(false);
        },()=>
        {
            Validate();MountPathGuard.ValidateOperation(definition,path!);
            if(hydrate)return null;
            if(LocalFileState.Read(path!).Key!=stamp!.Key||_repository.Entries(item!.MountId).FirstOrDefault(x=>x.Id==item.EntryId)!=mapping)return null;
            if(_repository.HasPendingRemote(item.MountId,item.SourcePath)||UploadRules.DeletedSourceAncestor(_repository.Entries(item.MountId),item.SourcePath) is not null)return null;
            var prepared=UploadPreparation.PublishAttempt(item,update=>ChangeUpload(item.Id,update),x=>x with
            {State="Prepared",SnapshotAttemptId=null,SnapshotPath=reuse?x.SnapshotPath:created,SnapshotSha256=reuse?x.SnapshotSha256:digest,SnapshotStamp=stamp.Key,SnapshotIdentity=stamp.Identity,SnapshotGeneration=item.Generation,Total=stamp.IsDirectory?0:stamp.Length,Bytes=0,Error=null,LastCheckedUtc=null,AutoRetryCount=reuse?x.AutoRetryCount:0,NextAttemptUtc=reuse?x.NextAttemptUtc:null,TransientFailureCount=reuse?x.TransientFailureCount:0,
                BaselineSha256=x.BaselineSha256??(x.ExpectedVersion is null?null:_repository.Baseline(x.MountId,x.SourcePath,x.ExpectedVersion))});
            published=prepared is not null;return prepared;
        },()=>
        {
            if(published){if(!reuse)DeleteUploadSnapshot(item!.SnapshotPath);}
            else DeleteUploadSnapshot(created);
            if(!published&&item is not null)_repository.UpdateUpload(item.Id,x=>x.SnapshotAttemptId==item.SnapshotAttemptId?x with{SnapshotAttemptId=null}:x);
        },token).ConfigureAwait(false);
    }
    /// <summary>实例级通知屏障，用于验证数据库、保护句柄和根锁的提交顺序。</summary>
    internal Action<string>? ShellNotificationForTests {get;set;}
    /// <summary>远端确认后短锁捕获，恢复摘要在锁外执行；重新核对会话、代次和文件后再原生提交及持久化。</summary>
    private async Task CommitUploadAsync(Runtime root,LocalUpload prepared,CancellationToken token)
    {
        var receipt=prepared.Receipt??throw new IOException("上传回执缺失。");string? notifyPath=null;
        var definition=root.Definition;var session=root.Session;var source=root.Source;
        var localPath=RelativePaths.ToLocal(definition.LocalPath,prepared.LocalPath);MountEntry? capturedEntry=null;
        MountEntry Entry()
        {
            var entries=_repository.Entries(prepared.MountId);
            var mapped=entries.FirstOrDefault(x=>x.Id==prepared.EntryId&&x.SourcePath==prepared.SourcePath&&x.LocalPath==prepared.LocalPath)
                ??entries.FirstOrDefault(x=>!x.Deleted&&x.SourcePath==prepared.SourcePath&&x.LocalPath==prepared.LocalPath);
            return new(mapped?.Id??prepared.Id,prepared.SourcePath,prepared.LocalPath,receipt.Version,receipt.IsDirectory,Materialized:true);
        }
        bool Current(UploadCommitCapture capture)=>!root.RemovalRequested&&!root.Definition.RemovalPending&&root.Definition==definition&&ReferenceEquals(root.Session,session)&&ReferenceEquals(root.Source,source)&&
            _repository.Upload(prepared.Id)==capture.Row&&Entry()==capturedEntry&&(!capture.NeedsHash||UploadCommitRecovery.SameStamp(localPath,capture.Stamp!));
        var recovery=await UploadCommitRecovery.VerifyAsync(root.Gate,()=>
        {
            var row=_repository.Upload(prepared.Id);if(row!=prepared||row.State!="RemoteCommitted"||root.RemovalRequested||root.Definition.RemovalPending)return null;
            MountPathGuard.ValidateOperation(definition,prepared.IsCopy?definition.LocalPath:localPath);capturedEntry=Entry();
            var stamp=prepared.IsCopy||LocalMissing(localPath)?null:LocalFileState.Read(localPath);
            var needsHash=stamp is not null&&row.Generation==prepared.SnapshotGeneration&&_uploadCommitter is IRecoveryUploadCommitter committer&&committer.NeedsRecovery(definition,capturedEntry,prepared,stamp);
            return new(row,stamp,needsHash);
        },Current,capture=>UploadCommitRecovery.HashAsync(localPath,capture.Stamp!,token,()=>UploadCheckpointAsync("RecoveryHash",prepared)),token).ConfigureAwait(false);
        if(recovery is null)return;
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(!Current(recovery))return;
            if(root.RemovalRequested||root.Definition.RemovalPending)throw new OperationCanceledException(token);
            MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
            if(UploadRules.DeletedSourceAncestor(_repository.Entries(root.Definition.Id),prepared.SourcePath) is not null)
            {ChangeUpload(prepared.Id,current=>current with{State="Conflict",Error=UploadRules.RemoteDeletedMessage,LastCheckedUtc=null});return;}
            var path=RelativePaths.ToLocal(root.Definition.LocalPath,prepared.LocalPath);var exists=!LocalMissing(path);LocalFileStamp? committedStamp=null;
            UploadRules.ValidateTypes(prepared,remoteDirectory:receipt.IsDirectory);
            if(_repository.Upload(prepared.Id)?.Error==UploadRules.TypeConflictMessage)throw new SourceConflictException(UploadRules.TypeConflictMessage);
            // 远端 StableId 可为空且不保证可用；占位身份始终由本产品维护。
            var entries=_repository.Entries(prepared.MountId);
            // 本地同名替换沿用原索引身份；提交事务成功时才解除删除／修改保护，避免产生同路径的旧墓碑。
            var mapped=entries.FirstOrDefault(x=>x.Id==prepared.EntryId&&x.SourcePath==prepared.SourcePath&&x.LocalPath==prepared.LocalPath)
                ??entries.FirstOrDefault(x=>!x.Deleted&&x.SourcePath==prepared.SourcePath&&x.LocalPath==prepared.LocalPath);
            var entry=new MountEntry(mapped?.Id??prepared.Id,prepared.SourcePath,prepared.LocalPath,receipt.Version,receipt.IsDirectory,Materialized:true);
            if(exists&&!prepared.IsCopy)
            {
                MountPathGuard.ValidateOperation(root.Definition,path);
                UploadRules.ValidateTypes(prepared,LocalFileState.Read(path),receipt.IsDirectory);
                var current=_repository.Upload(prepared.Id);
                _previews.Invalidate(root.Definition,entry.Id);
                if(current?.Generation==prepared.SnapshotGeneration)committedStamp=_uploadCommitter is IRecoveryUploadCommitter committer?
                    committer.Commit(root.Definition,entry,prepared,recovery.VerifiedStamp):_uploadCommitter.Commit(root.Definition,entry,prepared);
            }
            if(committedStamp is not null)await UploadCheckpointAsync("CommitVerified",prepared).ConfigureAwait(false);
            var completed=_repository.TryCommitUploadedEntry(recovery.Row,prepared,entry,receipt,committedStamp,!exists);
            if(completed is null)return;
            if(committedStamp is not null&&completed.State=="Completed")notifyPath=path;
            CompleteOwnUploadEcho(root,receipt);
            await UploadCheckpointAsync("Committed",completed).ConfigureAwait(false);
            if(committedStamp is not null&&exists)_previews.Queue(root.Definition,entry);
            PublishUpload(completed);DeleteUploadSnapshot(prepared.SnapshotPath);root.Uploads?.Wake();
        }
        finally
        {
            root.Gate.Release();
            if(notifyPath is not null)
                try{if(ShellNotificationForTests is {} notify)notify(notifyPath);else CloudFilesNative.NotifyItemUpdated(notifyPath);}
                catch(Exception error){_logger.Queue("Shell状态","Warning","上传已提交，Shell通知失败："+error.GetType().Name);}
        }
    }
    /// <summary>恢复发送只接受本挂载的暂存文件，不沿快照目录中的联接或符号链接读取。</summary>
    private void ValidateUploadSnapshot(LocalUpload item)
    {
        var staging=Path.Combine(_repository.DataDirectory,"UploadStaging");var own=Path.Combine(staging,item.MountId.ToString("N"));
        var path=item.SnapshotPath;
        if(path is null||!Path.IsPathFullyQualified(path)||!MountPathGuard.IsUnder(own,path)||!Path.GetExtension(path).Equals(".snapshot",StringComparison.OrdinalIgnoreCase))
            throw new SourceConflictException("上传快照路径不属于本挂载，已拒绝读取。");
        for(var cursor=path;;cursor=Path.GetDirectoryName(cursor)!)
        {
            if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new SourceConflictException("上传快照路径包含外部链接，已拒绝读取。");
            if(cursor.Equals(staging,StringComparison.OrdinalIgnoreCase))break;
        }
    }
    /// <summary>只清理本程序根外快照目录中的文件；未知结果仍引用的快照由持久记录保留。</summary>
    private void DeleteUploadSnapshot(string? path,bool forget=false)
    {
        if(path is null||!MountPathGuard.IsUnder(Path.Combine(_repository.DataDirectory,"UploadStaging"),path))return;
        try
        {
            var staging=Path.Combine(_repository.DataDirectory,"UploadStaging");
            for(var cursor=Path.GetFullPath(path);;cursor=Path.GetDirectoryName(cursor)!)
            {
                if((File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)return;
                if(cursor.Equals(staging,StringComparison.OrdinalIgnoreCase))break;
            }
            _repository.DeleteUnreferencedUploadSnapshot(path,()=>File.Delete(path),forget);
        }
        catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
