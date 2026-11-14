using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly object _previewDemandGate=new();
    private readonly Dictionary<Guid,(string Signature,HashSet<string> Entries)> _previewDemand=new();
#if HYBRIDMOUNT_HEADLESS_TEST
    internal bool BrowsePreviewsForTests{get;set;}
    internal bool ObserveExplorerForTests{get;set;}
    internal Action<string>? ThumbnailNotificationForTests{set=>_previews.ThumbnailNotificationForTests=value;}
    internal TimeSpan ThumbnailNotificationDebounceForTests{set=>_previews.NotificationDebounce=value;}
    internal void PreviewObservationForTests(Guid id,IReadOnlyList<string> paths,bool complete)=>Root(id).Session!.PreviewObservationForTests(paths,complete);
    private bool PreviewObservationEnabled=>ObserveExplorerForTests;
    private bool BrowseDrivenPreviews=>BrowsePreviewsForTests;
    internal void ApplyPreviewDemandForTests(Guid id,IReadOnlyList<string> paths){var root=Root(id);ApplyPreviewDemand(root,root.Session!,paths);}
#else
    private bool PreviewObservationEnabled=>true;
#endif
    /// <summary>浏览需求只查索引并进入有界队列；网络、解码和原生操作均不在事件读线程或UI执行。</summary>
    private void ApplyPreviewDemand(Runtime root,CloudMountSession session,IReadOnlyList<string> paths)=>ApplyPreviewDemand(root,session,new PreviewBrowseDemand(paths,paths.ToHashSet(StringComparer.OrdinalIgnoreCase)));
    private void ApplyPreviewDemand(Runtime root,CloudMountSession session,PreviewBrowseDemand demand)
    {
        var paths=demand.Paths;
#if HYBRIDMOUNT_HEADLESS_TEST
        if (!BrowseDrivenPreviews) return;
#endif
        if(!CustomPreviewsAvailable||IsStopping||root.RemovalRequested||!root.Definition.ThumbnailsEnabled||!ReferenceEquals(root.Session,session))return;
        var selected=paths.Where(path=>Path.IsPathFullyQualified(path)&&MountPathGuard.IsUnder(root.Definition.LocalPath,path)).Select(path=>Path.GetRelativePath(root.Definition.LocalPath,path).Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pathSignature=string.Join("|",selected.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))+"#"+string.Join("|",demand.ForegroundPaths.OrderBy(path=>path,StringComparer.OrdinalIgnoreCase))+"#"+demand.Window+":"+demand.View;
        lock(_previewDemandGate){if(_previewDemand.TryGetValue(root.Definition.Id,out var previous)&&previous.Signature==pathSignature)return;}
        var entries=_repository.EntriesAtPaths(root.Definition.Id,selected).Where(entry=>!entry.Deleted&&!entry.IsDirectory&&entry.Materialized&&entry.RemotePresence!=RemotePresence.Deleted).OrderBy(x=>x.Id,StringComparer.Ordinal).Take(512).ToArray();
        var signature=pathSignature;
        lock(_previewDemandGate){if(_previewDemand.TryGetValue(root.Definition.Id,out var previous)&&previous.Signature==signature)return;_previewDemand[root.Definition.Id]=(signature,entries.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal));}
        _previews.SetBrowseDemand(root.Definition,entries,RemoteThumbnailsAllowed,entries.Where(entry=>demand.ForegroundPaths.Contains(RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath))).Select(entry=>entry.Id).ToHashSet(StringComparer.Ordinal),demand.Window,demand.View);
    }
    /// <summary>水合完成仅优先当前浏览项目；测试兼容路径仍可显式验证历史队列。</summary>
    private void QueueLocalPreview(Runtime root,MountEntry entry)
    {
#if HYBRIDMOUNT_HEADLESS_TEST
        if (!BrowseDrivenPreviews)
        {
            _previews.Queue(root.Definition, entry);
            return;
        }
#endif
        lock (_previewDemandGate)
        {
            if (!_previewDemand.TryGetValue(root.Definition.Id, out var current) || !current.Entries.Contains(entry.Id)) return;
        }
        _previews.UpdateBrowseEntry(root.Definition, entry, RemoteThumbnailsAllowed);
    }
    private void SetPreviewObservation(Runtime root,bool enabled)
    {
#if HYBRIDMOUNT_HEADLESS_TEST
        if (!BrowseDrivenPreviews) return;
#endif
        if(!PreviewObservationEnabled||!CustomPreviewsAvailable||root.Session is null)return;
        try{root.Session.ObservePreviews(enabled&&root.Source is not null&&!root.RemovalRequested);}
        catch(Exception error){_logger.QueueException("缩略图","Warning","目录观察未启用，挂载继续运行",error,root.Definition.Id.ToString("N"));}
        if(!enabled)lock(_previewDemandGate)_previewDemand.Remove(root.Definition.Id);
    }

    /// <summary>设置只改变指定挂载；关闭先持久化再取消工作，开启只预热完整本地内容。</summary>
    public Task SetThumbnailsEnabledAsync(Guid id,bool enabled,CancellationToken token=default)=>OwnAsync(cancel=>SetThumbnailsCoreAsync(id,enabled,cancel),token);
    private async Task SetThumbnailsCoreAsync(Guid id,bool enabled,CancellationToken token)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if(!CustomPreviewsAvailable)throw new NotSupportedException(ShellExtensionPolicy.PauseReason);
        var root=Root(id);await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try{if(root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("移除过程中不能更改缩略图。");var updated=root.Definition with{ThumbnailsEnabled=enabled};_repository.SaveDefinition(updated);root.Definition=updated;}
        finally{root.Gate.Release();}
        _previews.ConfigureMount(root.Definition);
        if (enabled)
        {
            _previews.ResumeMount(id);
#if HYBRIDMOUNT_HEADLESS_TEST
            if (!BrowseDrivenPreviews)
                foreach (var entry in _repository.Entries(id)) _previews.Queue(root.Definition, entry);
#endif
        }
        else await _previews.CancelMountAsync(id).ConfigureAwait(false);
        SetPreviewObservation(root,enabled);Notify();
    }
    private (Runtime Root,MountEntry Entry)? PreviewTarget(string path)
    {
        if(!CustomPreviewsAvailable||IsStopping)return null;var id=FindMount([path]);var root=Root(id);if(root.RemovalRequested||root.Definition.RemovalPending)return null;
        var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        var entry=marker?.MountId==id?_repository.EntryById(id,marker.EntryId):null;
        if(entry is not {Materialized:true,Deleted:false}||entry.RemotePresence==RemotePresence.Deleted)return null;
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
#if HYBRIDMOUNT_HEADLESS_TEST
        _previews.Queue(target.Value.Root.Definition, target.Value.Entry, LegacyRemotePreviewsAllowed);
#else
        _previews.Queue(target.Value.Root.Definition, target.Value.Entry, allowRemote: false);
#endif
        return _previews.State(target.Value.Root.Definition, target.Value.Entry);
    }
    /// <summary>新Shell宿主按需生成单个媒体；根内已启用项目才能排队，网络和解码不在回调内执行。</summary>
    public string RequestThumbnail(string path,int size)
    {
        if(!CustomPreviewsAvailable)return "disabled";if(size is <16 or >256)return "unsupported";
        var target=PreviewTarget(path);if(target is null)return "unsupported";
        _previews.Queue(target.Value.Root.Definition,target.Value.Entry,RemoteThumbnailsAllowed,urgent:false);return _previews.State(target.Value.Root.Definition,target.Value.Entry);
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
        using var queueTask=TasksQueue.EnterTask(mount.Id,"preview:"+mount.Id+":"+entry.Id,MountingPriority.Thumbnail,"thumbnail",entry.SourcePath,entry.Id,entry.Version);
        using var cancelOwner=TasksQueue.RegisterCancellation("preview:"+mount.Id+":"+entry.Id,()=>_previews.CancelEntry(mount.Id,entry.Id));
#if HYBRIDMOUNT_HEADLESS_TEST
        allowRemote = allowRemote && (LegacyRemotePreviewsAllowed || RemoteThumbnailsAllowed);
#else
        allowRemote = allowRemote && RemoteThumbnailsAllowed;
#endif
        NativeLocalReadLease? local=null;PreviewDecodeLease? decode=null;LocalFileStamp? stamp=null;Commons.IFileSource? source=null;CloudMountSession? session=null;long length=0;var nativeLocal=false;
        var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);
#if HYBRIDMOUNT_HEADLESS_TEST
        // 旧预算专项显式保留原经纪路径；生产和新的本地原生集成回归使用单一Stock生成者。
        var nativeCompleteLocal=!LegacyPreviewsForTests;
#else
        var nativeCompleteLocal=true;
#endif
        async ValueTask ReleaseLocal(){if(local is {} lease){local=null;await lease.DisposeAsync().ConfigureAwait(false);stamp=lease.Stamp;}}
        try
        {
            if(!nativeCompleteLocal)decode=await PreviewDecodeLease.AcquireAsync(path,token).ConfigureAwait(false);
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(!root.Definition.ThumbnailsEnabled||root.RemovalRequested||root.Session is null||root.Source is null)return null;
                MountPathGuard.ValidateOperation(root.Definition,path);var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                if(marker?.MountId!=mount.Id||marker.EntryId!=entry.Id)return null;
                length=new FileInfo(path).Length;source=root.Source;session=root.Session;
                if(CloudFilesNative.HasFullContent(path,length))
                {
                    if(nativeCompleteLocal)nativeLocal=true;
                    else{local=await NativeLocalReadLease.OpenAsync(root.Definition,entry,token).ConfigureAwait(false);stamp=local.Stamp;}
                }
                else if(!allowRemote||entry.Protected||info!.Value.Info.InSyncState!=1||info.Value.Info.ModifiedSize!=0)return null;
            }
            finally{root.Gate.Release();}
            if(!nativeLocal&&decode is null)
            {
                decode=await PreviewDecodeLease.AcquireAsync(path,token).ConfigureAwait(false);
                // 排队期间可能已经水合完成，先归还托管互斥量，再由原生端取得自己的互斥量及保护租约。
                if(nativeCompleteLocal&&CloudFilesNative.HasFullContent(path,length)){await decode.DisposeAsync().ConfigureAwait(false);decode=null;nativeLocal=true;}
            }
            using var remoteRead=!nativeLocal&&local is null?await TasksQueue.AcquireCurrentWorkflowAsync(token).ConfigureAwait(false):null;
            Dictionary<int,byte[]>? frames;
            if(nativeLocal)
            {
                var result=await session!.Runtime.InvokeAsync("renderLocalThumbnail",session.EntryArgs(entry),token:token).ConfigureAwait(false);
                frames=System.Text.Json.JsonSerializer.Deserialize<Dictionary<int,byte[]>>(result.GetProperty("frames"));stamp=NativeRuntimeArguments.Stamp(result);
                if(frames is null||frames.Count!=4||PreviewWorker.Sizes.Any(size=>!frames.TryGetValue(size,out var png)||png.Length is <24 or >ExePreviewCache.MaximumPngBytes))return null;
                _previews.RecordTiming(mount.Id,"原生启动",result.GetProperty("startupMs").GetInt64());_previews.RecordTiming(mount.Id,"原生解码",result.GetProperty("decodeMs").GetInt64());
            }
            else
            {
                PreviewRangeBudget budget;
                if(local is not null)budget=PreviewRangeBudget.ForLocal(length,local.ReadAsync);
                else if(source!.Capabilities.RangeRead==CapabilitySupport.Unsupported){_previews.RecordOutcome(mount.Id,"远端严格预览不支持");return null;}
                else budget=new PreviewRangeBudget(source!,new SourceEntry(entry.Id,entry.SourcePath,false,length,DateTimeOffset.MinValue,entry.Version));
                frames=await new PreviewWorkerClient(timing:(stage,ms)=>_previews.RecordTiming(mount.Id,stage,ms),diagnostic:code=>{_previews.RecordOutcome(mount.Id,"媒体解码",code,true);_logger.Queue("缩略图","Warning",$"媒体解码失败；HRESULT=0x{code:X8}");}).DecodeAsync(Path.GetExtension(entry.LocalPath),length,budget,token).ConfigureAwait(false);
                if(frames is null&&budget.UnavailableReason is {} reason)_previews.RecordOutcome(mount.Id,"预览不可用："+reason);
            }
            // 归还本地保护引用后才重新等待根锁；不能让上传或清空与预览的锁次序相反。
            await ReleaseLocal().ConfigureAwait(false);await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(!root.Definition.ThumbnailsEnabled||root.RemovalRequested||root.Session is null||!ReferenceEquals(root.Session,session)||!ReferenceEquals(root.Source,source)||_stop.IsCancellationRequested){_previews.RecordOutcome(mount.Id,"生成完成后连接或设置已变化");return null;}
                var current=_repository.EntriesAtPaths(mount.Id,[entry.LocalPath]).FirstOrDefault(item=>item.Id==entry.Id);if(current is null||current.Version!=entry.Version||current.LocalPath!=entry.LocalPath||current.Deleted||current.RemotePresence==RemotePresence.Deleted){_previews.RecordOutcome(mount.Id,"生成完成后索引版本已变化");return null;}
                MountPathGuard.ValidateOperation(root.Definition,path);var info=CloudFilesNative.Inspect(path);if(info is null){_previews.RecordOutcome(mount.Id,"生成完成后占位身份不可用");return null;}
                if(stamp is not null){var actual=LocalFileState.Read(path);if(actual.Key!=stamp.Key){_previews.RecordOutcome(mount.Id,$"生成完成后本地状态戳已变化（USN {stamp.Usn}→{actual.Usn}）");return null;}}
                return frames is null?null:new VerifiedPreviewFrames(frames,LocalFileState.Read(path));
            }
            finally{root.Gate.Release();}
        }
        finally{await ReleaseLocal().ConfigureAwait(false);if(decode is not null)await decode.DisposeAsync().ConfigureAwait(false);}
    }
}
