using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>仅在根锁内证明已映射目录自身没有变更意图；子文件的修改不属于目录元数据冲突。身份不明、删除及不可逆阶段均拒绝。</summary>
    private bool DirectoryObservationSafe(Runtime root,RemotePendingChange pending,MountEntry? entry,IEnumerable<LocalUpload> uploads)
    {
        if(pending.Entry is not {IsDirectory:true}||pending.SnapshotPath is not null||pending.OriginalStamp is not null||pending.State is "Committing" or "Hydrating"||
            entry is not {IsDirectory:true,Materialized:true,Deleted:false,Protected:false}||entry.RemotePresence==RemotePresence.Deleted||
            !entry.SourcePath.Equals(pending.Path,StringComparison.Ordinal)||RenameBlocksSource(root.Definition.Id,pending.Path)||
            uploads.Any(x=>UploadRules.Unfinished(x)&&x.SourcePath.Equals(pending.Path,StringComparison.OrdinalIgnoreCase)))return false;
        var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(root.Definition,path);
        var state=LocalFileMetadata.TryRead(path);if(state is null||(state.Value.Attributes&FileAttributes.Directory)==0)return false;
        var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        return marker?.MountId==root.Definition.Id&&marker.EntryId==entry.Id&&info!.Value.Info.InSyncState==1&&info.Value.Info.ModifiedSize==0;
    }
    /// <summary>正常目录属性观测只提交映射版本；不扫描或更改子文件，不将子文件上传标为冲突。调用者持根锁。</summary>
    private bool TryCompleteDirectoryObservation(Runtime root,RemoteSyncRuntime runtime,RemotePendingChange pending,MountEntry? entry,IEnumerable<LocalUpload> uploads)
    {
        if(pending.State=="Canceled"||!DirectoryObservationSafe(root,pending,entry,uploads))return false;
        var completed=pending with{State="Completed",Error=null,MetadataOnly=true};
        _repository.CommitRemotePlaceholder(completed,entry! with{Version=pending.Entry!.Version});
        _repository.SetRemotePresence(root.Definition.Id,entry! with{Version=pending.Entry!.Version},RemotePresence.Present);
        _repository.SaveOperation(RemoteTask(completed));WakeRemoteDependents(root);Notify();return true;
    }
    /// <summary>对取消的目录记录只读查询实际远端，再核验本地映射；保留取消及原版本，不执行原操作或修改正文，仅解除已证明无害的依赖。</summary>
    private async Task<bool> RecheckCanceledDirectoryAsync(Runtime root,RemoteSyncRuntime runtime,RemotePendingChange pending,CancellationToken token)
    {
        if(pending.State!="Canceled"||pending.MetadataOnly||pending.Entry is not {IsDirectory:true})return false;
        var remote=await runtime.Source.TryGetMetadataAsync(pending.Path,token).ConfigureAwait(false);
        if(remote is not {IsDirectory:true}||remote.Path!=pending.Path)return false;
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))return false;
            var current=_repository.AllRemotePending(root.Definition.Id).SingleOrDefault(x=>x.Id==pending.Id);
            if(current!=pending)return false;
            var entry=_repository.EntryByExactSource(root.Definition.Id,pending.Path);
            if(!DirectoryObservationSafe(root,pending,entry,_repository.Uploads(root.Definition.Id)))return false;
            SaveRemote(pending with{MetadataOnly=true});WakeRemoteDependents(root);return true;
        }
        finally{root.Gate.Release();}
    }
    /// <summary>依赖提交后通知已有消费者；唤醒不联网、不重启取消任务，固定维护重新核验实际系统意图。</summary>
    private static void WakeRemoteDependents(Runtime root){root.Uploads?.Wake();root.Pins?.RequestFull();}
}
