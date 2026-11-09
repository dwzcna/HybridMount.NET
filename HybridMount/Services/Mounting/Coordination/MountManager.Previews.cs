using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>设置只改变指定挂载；关闭先持久化再取消工作，开启只预热完整本地内容。</summary>
    public Task SetThumbnailsEnabledAsync(Guid id,bool enabled,CancellationToken token=default)=>OwnAsync(cancel=>SetThumbnailsCoreAsync(id,enabled,cancel),token);
    private async Task SetThumbnailsCoreAsync(Guid id,bool enabled,CancellationToken token)
    {
        if(!CustomPreviewsAvailable)throw new NotSupportedException(ShellExtensionPolicy.PauseReason);
        var root=Root(id);await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try{if(root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("移除过程中不能更改缩略图。");var updated=root.Definition with{ThumbnailsEnabled=enabled};_repository.SaveDefinition(updated);root.Definition=updated;}
        finally{root.Gate.Release();}
        _previews.ConfigureMount(root.Definition);
        if(enabled){_previews.ResumeMount(id);foreach(var entry in _repository.Entries(id))_previews.Queue(root.Definition,entry);}
        else await _previews.CancelMountAsync(id).ConfigureAwait(false);
        CloudFilesNative.NotifyItemUpdated(root.Definition.LocalPath);Notify();
    }
    private (Runtime Root,MountEntry Entry)? PreviewTarget(string path)
    {
        if(!CustomPreviewsAvailable||IsStopping)return null;var id=FindMount([path]);var root=Root(id);if(root.RemovalRequested||root.Definition.RemovalPending)return null;
        var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        var entry=_repository.Entries(id).FirstOrDefault(entry=>entry.Id==marker?.EntryId&&entry.Materialized&&!entry.Deleted&&entry.RemotePresence!=RemotePresence.Deleted);
        if(marker?.MountId!=id||entry is null||entry.IsDirectory||!PreviewWorker.Supports(entry.LocalPath))return null;
        return(root,entry);
    }
    /// <summary>只读的缓存状态，不排队、不访问远端；旧 IPC 调用仍保持 getPreview 的无副作用语义。</summary>
    public string PreviewState(string path)=>!CustomPreviewsAvailable?"disabled":PreviewTarget(path) is {} target?_previews.State(target.Root.Definition,target.Entry):"unsupported";
    /// <summary>扩展显式请求单个文件预览；立即返回状态，网络和解码均在后台，不自动启动应用。</summary>
    public string RequestPreview(string path,int size)
    {
        if(!CustomPreviewsAvailable)return "disabled";
        if(size is <16 or >256)return "unsupported";var target=PreviewTarget(path);if(target is null)return "unsupported";
        _previews.Queue(target.Value.Root.Definition,target.Value.Entry,LegacyRemotePreviewsAllowed);return _previews.State(target.Value.Root.Definition,target.Value.Entry);
    }
    /// <summary>新Shell宿主按需生成单个媒体；根内已启用项目才能排队，网络和解码不在回调内执行。</summary>
    public string RequestThumbnail(string path,int size)
    {
        if(!CustomPreviewsAvailable)return "disabled";if(size is <16 or >256)return "unsupported";
        var target=PreviewTarget(path);if(target is null)return "unsupported";
        _previews.Queue(target.Value.Root.Definition,target.Value.Entry,RemoteThumbnailsAllowed);return _previews.State(target.Value.Root.Definition,target.Value.Entry);
    }
    /// <summary>新宿主只请求已完整的本地内容，不复用旧进程可能联网的requestPreview语义。</summary>
    public string RequestLocalPreview(string path,int size)
    {
        if(!CustomPreviewsAvailable)return "disabled";if(size is <16 or >256)return "unsupported";
        var target=PreviewTarget(path);if(target is null)return "unsupported";
        _previews.Queue(target.Value.Root.Definition,target.Value.Entry,false);return _previews.State(target.Value.Root.Definition,target.Value.Entry);
    }
    /// <summary>根锁内只核对并取得本地保护句柄；远端读取及隔离解码不持有根锁，发布前再校验身份和设置。</summary>
    private async Task<Dictionary<int,byte[]>?> RenderPreviewAsync(MountDefinition mount,MountEntry entry,bool allowRemote,CancellationToken token)
    {
        if(!CustomPreviewsAvailable||!_roots.TryGetValue(mount.Id,out var root))return null;
        allowRemote=allowRemote&&(LegacyRemotePreviewsAllowed||RemoteThumbnailsAllowed); // 仅新按需请求允许有界远端预览。
        NativeLocalReadLease? local=null;LocalFileStamp? stamp=null;Common.IFileSource? source=null;long length=0;
        async ValueTask ReleaseLocal(){if(local is {} lease){local=null;await lease.DisposeAsync().ConfigureAwait(false);}}
        try
        {
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(!root.Definition.ThumbnailsEnabled||root.RemovalRequested||root.Session is null||root.Source is null)return null;
                var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(root.Definition,path);
                var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                if(marker?.MountId!=mount.Id||marker.EntryId!=entry.Id)return null;
                length=new FileInfo(path).Length;source=root.Source;
                if(CloudFilesNative.HasFullContent(path,length))
                {
                    local=await NativeLocalReadLease.OpenAsync(root.Definition,entry,token).ConfigureAwait(false);stamp=local.Stamp;
                }
                else if(!allowRemote||entry.Protected||info!.Value.Info.InSyncState!=1||info.Value.Info.ModifiedSize!=0)return null;
            }
            finally{root.Gate.Release();}
            PreviewRangeBudget budget;
            if(local is not null)
            {
                budget=new PreviewRangeBudget(length,local.ReadAsync);
            }
            else budget=new PreviewRangeBudget(source!,new SourceEntry(entry.Id,entry.SourcePath,false,length,DateTimeOffset.MinValue,entry.Version));
            var frames=await new PreviewWorkerClient().DecodeAsync(Path.GetExtension(entry.LocalPath),length,budget.ReadAsync,token).ConfigureAwait(false);
            // 解码结束先归还oplock；上传／清空可能已持有根锁，不能带着正文保护引用重新等待该锁。
            await ReleaseLocal().ConfigureAwait(false);
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(!root.Definition.ThumbnailsEnabled||root.RemovalRequested||root.Session is null||!ReferenceEquals(root.Source,source)||_stop.IsCancellationRequested)return null;
                var current=_repository.Entries(mount.Id).FirstOrDefault(item=>item.Id==entry.Id);if(current is null||current.Version!=entry.Version||current.LocalPath!=entry.LocalPath||current.Deleted||current.RemotePresence==RemotePresence.Deleted)return null;
                var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(root.Definition,path);var info=CloudFilesNative.Inspect(path);
                if(info is null||stamp is not null&&LocalFileState.Read(path).Key!=stamp.Key)return null;
                return frames;
            }
            finally{root.Gate.Release();}
        }
        finally{await ReleaseLocal().ConfigureAwait(false);}
    }
}
