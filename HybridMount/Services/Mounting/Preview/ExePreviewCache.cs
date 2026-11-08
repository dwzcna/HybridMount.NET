using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>每挂载可选的有界预览队列与缓存；查询仅查缓存，解码委托在独立进程执行。</summary>
public sealed class ExePreviewCache : IAsyncDisposable
{
    public const int MaximumPngBytes=384*1024;
    private static readonly int[] Sizes=[32,64,128,256];
    private readonly ThumbnailCacheCatalog _store;
    private readonly ConcurrentDictionary<Guid,byte> _removed=new();
    private readonly ConcurrentDictionary<Guid,(ThumbnailCacheRoot Requested,ThumbnailCacheRoot Actual)> _configured=new();
    private readonly Func<bool> _available;
    private readonly Func<Guid,MountDefinition?>? _currentMount;
    private readonly object _configurationGate=new();
    private readonly Func<MountDefinition,MountEntry,CancellationToken,Task<IDisposable?>>? _enter;
    private readonly Action<string>? _diagnostic;
    private sealed class Work(MountDefinition mount,MountEntry entry,bool remote,CancellationToken stopping)
    {
        public readonly MountDefinition Mount=mount;public readonly MountEntry Entry=entry;public readonly bool Remote=remote;
        public readonly CancellationTokenSource Cancel=CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public readonly TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly object Sync=new();public bool Disposed;
        public Task RequestCancel(){lock(Sync)return Disposed?Task.CompletedTask:Cancel.CancelAsync();}
        public void Dispose(){lock(Sync){if(Disposed)return;Disposed=true;Cancel.Dispose();}}
    }
    private readonly ConcurrentDictionary<string,Work> _pending=new();
    private readonly ConcurrentDictionary<Guid,SemaphoreSlim> _mounts=new();
    private readonly ConcurrentDictionary<Guid,byte> _blocked=new();
    private readonly ConcurrentDictionary<string,(Guid Mount,DateTime Until)> _failed=new();
    private readonly SemaphoreSlim _slots=new(2,2);
    private readonly object _schedule=new();
    private readonly Channel<Work> _queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(128){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _worker;
    private readonly Func<MountDefinition,MountEntry,bool,CancellationToken,Task<Dictionary<int,byte[]>?>>? _render;
    private Task? _dispose,_cancellation;
    public ExePreviewCache(string directory,Func<MountDefinition,MountEntry,CancellationToken,Task<IDisposable?>>? enter=null,Action<string>? diagnostic=null,
        Func<MountDefinition,MountEntry,bool,CancellationToken,Task<Dictionary<int,byte[]>?>>? render=null)
        :this(directory,enter,diagnostic,render,()=>true){}
    /// <summary>应用层提供版本能力；暂停时在任何路径、缓存或内容查询之前退出。独立缓存实现保留可测试性。</summary>
    internal ExePreviewCache(string directory,Func<MountDefinition,MountEntry,CancellationToken,Task<IDisposable?>>? enter,Action<string>? diagnostic,
        Func<MountDefinition,MountEntry,bool,CancellationToken,Task<Dictionary<int,byte[]>?>>? render,Func<bool> available,Func<Guid,MountDefinition?>? currentMount=null)
    {_currentMount=currentMount;_available=available;_store=new(directory);_enter=enter;_diagnostic=diagnostic;_render=render;_worker=Task.Run(ProcessAsync);}
    /// <summary>按挂载、条目、原始内容版本和尺寸区分；不把远端地址或令牌明文写入文件名。</summary>
    public static string CacheKey(MountDefinition mount,MountEntry entry,int size)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]{mount.Id.ToString("N"),entry.Id,entry.Version,Bucket(size).ToString(System.Globalization.CultureInfo.InvariantCulture)})));
    private static int Bucket(int size)=>Sizes.FirstOrDefault(candidate=>candidate>=Math.Clamp(size,16,256),256);
    /// <summary>配置摘要变化才更新库和epoch；UI刷新不会反复创建写事务。</summary>
    internal ThumbnailCacheRoot? ConfigureMount(MountDefinition mount)
    {
        if(!_available()||_stop.IsCancellationRequested||_removed.ContainsKey(mount.Id))return null;
        try
        {
            lock(_configurationGate)
            {
                if(_removed.ContainsKey(mount.Id))return null;
                // 工作项携带旧快照；串行写入前重新读取协调器当前值，禁止关闭／移除后被迟到工作重新启用。
                if(_currentMount is not null)
                {
                    var current=_currentMount(mount.Id);
                    if(current is null){if(_configured.TryRemove(mount.Id,out var removed))_store.Configure(removed.Actual with{Enabled=false});return null;}
                    mount=current;
                }
                var requested=ThumbnailCacheReader.RootFor(mount);
                if(_configured.TryGetValue(mount.Id,out var previous)&&previous.Requested==requested)return previous.Actual;
                var root=_store.Configure(requested);_configured[mount.Id]=(requested,root);return root;
            }
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.ComponentModel.Win32Exception){_diagnostic?.Invoke("缩略图库配置失败："+error.GetType().Name);return null;}
    }
    internal void Invalidate(MountDefinition mount,string entry)
    {try{if(_store.UsageBytes!=0)_store.Invalidate(mount.Id,entry);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){_diagnostic?.Invoke("缩略图失效记录未完成："+error.GetType().Name);}}
    /// <summary>原生固定／脱水已完成后更新精确戳，当前索引版本和文件身份都必须仍匹配图片。</summary>
    internal void RebindMetadata(MountDefinition mount,MountEntry entry)
    {
        if(!_available()||!mount.ThumbnailsEnabled)return;
        try
        {
            var root=ConfigureMount(mount);
            if(root is null&&_configured.TryGetValue(mount.Id,out var saved)&&saved.Requested==ThumbnailCacheReader.RootFor(mount))root=saved.Actual; // 退出时仍收尾已接受的原生元数据变化，不重新排队。
            if(root is null)return;var current=ThumbnailCacheReader.Capture(root,entry);if(current is null||!current.Clean)return;
            var previous=_store.Binding(current.Path);if(previous is not null)_store.Rebind(previous,current);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){_diagnostic?.Invoke("缩略图元数据重绑未完成："+error.GetType().Name);}
    }

    /// <summary>普通完成通知只处理完整本地文件；仅显式预览请求可读取远端，队列与挂载设置均有界。</summary>
    public void Queue(MountDefinition mount,MountEntry entry,bool allowRemote=false)
    {
        if(!_available()||_stop.IsCancellationRequested||!mount.ThumbnailsEnabled||mount.RemovalPending||entry.Deleted||entry.RemotePresence==RemotePresence.Deleted||entry.IsDirectory||!entry.Materialized||!PreviewWorker.Supports(entry.LocalPath)||_blocked.ContainsKey(mount.Id))return;
        if(!allowRemote)
        {
            var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);
            try{if(!File.Exists(path)||!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length))return;}catch(IOException){return;}catch(UnauthorizedAccessException){return;}
        }
        if(ConfigureMount(mount) is null)return;
        var key=CacheKey(mount,entry,256);if(!allowRemote)_failed.TryRemove(key,out _);if(_failed.TryGetValue(key,out var failure)&&failure.Until>DateTime.UtcNow)return;
        lock(_schedule)
        {
            if(_stop.IsCancellationRequested||_pending.Count>=128||_pending.ContainsKey(key)||Sizes.All(size=>Get(mount,entry,size) is not null))return;
            if(_blocked.ContainsKey(mount.Id)||_removed.ContainsKey(mount.Id))return;
            var work=new Work(mount,entry,allowRemote,_stop.Token);if(!_pending.TryAdd(key,work)||!_queue.Writer.TryWrite(work)){_pending.TryRemove(key,out _);work.Dispose();}
        }
    }
    /// <summary>关闭／断开先阻止新任务，再取消并等待此挂载所有工作；不持有根锁等待其他挂载。</summary>
    public async Task CancelMountAsync(Guid mount)
    {
        Work[] pending;lock(_schedule){_blocked[mount]=0;pending=_pending.Values.Where(work=>work.Mount.Id==mount).ToArray();}
        await Task.WhenAll(pending.Select(work=>work.RequestCancel())).ConfigureAwait(false);
        await Task.WhenAll(pending.Select(work=>work.Completion.Task)).ConfigureAwait(false);
    }
    /// <summary>先封闭新工作并等待所有本挂载任务，再删除精确库；配置尚未遗忘，失败可重试。</summary>
    internal async Task RemoveMountAsync(MountDefinition mount)
    {
        await CancelMountAsync(mount.Id).ConfigureAwait(false);
        lock(_configurationGate){_removed[mount.Id]=0;_configured.TryRemove(mount.Id,out _);_store.Remove(mount);}
    }
    public void ResumeMount(Guid mount){_blocked.TryRemove(mount,out _);foreach(var row in _failed.Where(row=>row.Value.Mount==mount))_failed.TryRemove(row.Key,out _);}
    public string State(MountDefinition mount,MountEntry entry)=>!_available()||!mount.ThumbnailsEnabled?"disabled":_pending.ContainsKey(CacheKey(mount,entry,256))?"queued":_failed.TryGetValue(CacheKey(mount,entry,256),out var failed)&&failed.Until>DateTime.UtcNow?"unsupported":"notFound";
    /// <summary>只读队列和负缓存状态，诊断不排队、不读取远端。</summary>
    internal string Describe(Guid mount)=>$"待处理={_pending.Values.Count(x=>x.Mount.Id==mount)}；暂缓={_failed.Values.Count(x=>x.Mount==mount&&x.Until>DateTime.UtcNow)}；停止接收={_blocked.ContainsKey(mount)}";
    /// <summary>有界本机查询；缓存不存在、失效或被回收均返回空值，不启动提取。</summary>
    public byte[]? Get(MountDefinition mount,MountEntry entry,int size)
    {
        if(!_available()||!mount.ThumbnailsEnabled||entry.Deleted||entry.RemotePresence==RemotePresence.Deleted||entry.IsDirectory||!PreviewWorker.Supports(entry.LocalPath))return null;
        try
        {
            if(!_configured.TryGetValue(mount.Id,out var configured)||configured.Requested!=ThumbnailCacheReader.RootFor(mount))return null;
            var current=ThumbnailCacheReader.Capture(configured.Actual,entry);if(current is null)return null;
            return _store.Read(current.Path,size,binding=>binding.ContentKey==current.ContentKey&&ThumbnailCacheReader.Valid(binding));
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException){return null;}
    }
    private async Task ProcessAsync()
    {
        try{await foreach(var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))_ = ProcessWorkAsync(work);}
        finally{await Task.WhenAll(_pending.Values.Select(work=>work.Completion.Task)).ConfigureAwait(false);}
    }
    private async Task ProcessWorkAsync(Work work)
    {
        var mount=work.Mount;var entry=work.Entry;var key=CacheKey(mount,entry,256);var gate=_mounts.GetOrAdd(mount.Id,_=>new(1,1));var entered=false;var slot=false;var token=work.Cancel.Token;
        try
        {
            if(!_available())return;
            await gate.WaitAsync(token).ConfigureAwait(false);entered=true;await _slots.WaitAsync(token).ConfigureAwait(false);slot=true;token.ThrowIfCancellationRequested();
            var root=ConfigureMount(mount);var before=root is null?null:ThumbnailCacheReader.Capture(root,entry);if(before is null)return;
            if(_render is not null)
            {
                var frames=await _render(mount,entry,work.Remote,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                if(frames is null){_failed[key]=(mount.Id,DateTime.UtcNow.AddMinutes(5));return;}
                var after=ThumbnailCacheReader.Capture(root!,entry);
                if(after is null||before.ContentKey!=after.ContentKey||before.Length!=after.Length||before.LastWriteTime!=after.LastWriteTime||!before.Clean&&before.Stamp!=after.Stamp)return;
                if(!_store.Publish(after,frames))return;
                CloudFilesNative.NotifyItemUpdated(RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath));
            }
            else
            {
                using var lease=_enter is null?null:await _enter(mount,entry,token).ConfigureAwait(false);if(_enter is null||lease is not null)Extract(mount,entry);
            }
        }
        catch(OperationCanceledException){if(!token.IsCancellationRequested)_failed[key]=(mount.Id,DateTime.UtcNow.AddMinutes(5));}
        catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or NotSupportedException or System.Text.Json.JsonException)
        {_failed[key]=(mount.Id,DateTime.UtcNow.AddMinutes(5));_diagnostic?.Invoke("预览回退："+error.GetType().Name);}
        finally{if(slot)_slots.Release();if(entered)gate.Release();_pending.TryRemove(key,out _);work.Completion.TrySetResult();work.Dispose();}
    }
    private void Extract(MountDefinition mount,MountEntry entry)
    {
        var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);
        // 排他 oplock 阻止并发脱水；只有完整、未修改且身份一致的内容才允许加载资源。
        bool Valid()
        {
            var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            return marker?.MountId==mount.Id&&marker.EntryId==entry.Id&&info!.Value.Info.ModifiedSize==0&&info.Value.Info.InSyncState==1&&CloudFilesNative.HasFullContent(path,new FileInfo(path).Length);
        }
        // 冷占位必须在任何内容句柄打开前跳过，避免打开行为本身引发水合。
        if(!Valid())return;
        var changed=false;var frames=new Dictionary<int,byte[]>();
        using var handle=CloudFilesNative.OpenRead(path);
        if(!CloudFilesNative.CfReferenceProtectedHandle(handle)){_diagnostic?.Invoke("保护句柄不可引用。");return;}
        try
        {
        if(!Valid())return;
        // 使用同一受保护句柄读取 PE 资源，不再次打开原文件，不触发 oplock 自身失效。
        using var content=new ProtectedResourceStream(CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle),new FileInfo(path).Length);
        foreach(var size in Sizes)
        {
            _stop.Token.ThrowIfCancellationRequested();if(Get(mount,entry,size) is not null)continue;
            var png=ExeIconExtractor.Extract(content,size);if(png is null||png.Length>MaximumPngBytes){_diagnostic?.Invoke("未取得有界图标资源。");continue;}
            MountPathGuard.ValidateOperation(mount,path);if(!Valid())return;
            frames[size]=png;changed=true;
        }

        }
        finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
        handle.Dispose();
        if(changed&&ConfigureMount(mount) is {} root&&ThumbnailCacheReader.Capture(root,entry) is {} binding&&_store.Publish(binding,frames)){_diagnostic?.Invoke("图标缓存已发布。");CloudFilesNative.NotifyItemUpdated(path);}
    }
    public long UsageBytes=>_store.UsageBytes;
    /// <summary>后台统一回收数据库BLOB，不散落图片文件或手工删除WAL。</summary>
    public long Trim(long needed){lock(_configurationGate){if(_store.UsageBytes==0)return 0;try{return _store.Trim(needed);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){return 0;}}}
    /// <summary>只发信号；宿主同时取消其他工作之后再等待本队列结束。</summary>
    public Task RequestStop(){lock(_schedule){_queue.Writer.TryComplete();return _cancellation??=_stop.CancelAsync();}}
    public ValueTask DisposeAsync(){lock(_schedule)return new(_dispose??=StopAsync());}
    private async Task StopAsync(){await RequestStop().ConfigureAwait(false);await _worker.ConfigureAwait(false);_slots.Dispose();foreach(var gate in _mounts.Values)gate.Dispose();_stop.Dispose();}
}
