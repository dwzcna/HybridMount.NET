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
    private sealed class Work(MountDefinition mount,MountEntry entry,bool remote,CancellationToken stopping,bool browse=false)
    {
        public readonly MountDefinition Mount=mount;public readonly MountEntry Entry=entry;public readonly bool Remote=remote,Browse=browse;
        public readonly CancellationTokenSource Cancel=CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public readonly TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly object Sync=new();public bool Disposed,UserCanceled;
        public Task RequestCancel(){lock(Sync)return Disposed||Cancel.IsCancellationRequested?Task.CompletedTask:Cancel.CancelAsync();}
        public void Dispose(){lock(Sync){if(Disposed)return;Disposed=true;Cancel.Dispose();}}
    }
    private readonly ConcurrentDictionary<string,Work> _pending=new();
    private readonly ConcurrentDictionary<Guid,SemaphoreSlim> _mounts=new();
    private readonly ConcurrentDictionary<Guid,byte> _blocked=new();
    private sealed record Outcome(string Stage,DateTimeOffset Utc,int? HResult);
    private readonly ConcurrentDictionary<Guid,long> _publishedCount=new(),_unavailableCount=new(),_canceledCount=new();
    private readonly ConcurrentDictionary<Guid,Outcome> _lastOutcome=new(),_lastFailure=new();
    /// <summary>只保留固定阶段和数值代码；完整诊断读此内存快照，不启动工作或查询源。</summary>
    internal void RecordOutcome(Guid mount,string stage,int? code=null,bool failure=false)
    {var value=new Outcome(stage,DateTimeOffset.UtcNow,code);_lastOutcome[mount]=value;if(failure)_lastFailure[mount]=value;}
    private void ReportFailure(Guid mount,string stage,Exception error)
    {RecordOutcome(mount,stage,error.HResult,true);_diagnostic?.Invoke($"阶段={stage}；类型={error.GetType().Name}；HRESULT=0x{error.HResult:X8}");}
    private static string DescribeOutcome(Outcome value)=>$"{value.Stage}；UTC={value.Utc:O}"+(value.HResult is {} code?$"；HRESULT=0x{code:X8}":"");
    private readonly ConcurrentDictionary<string,(Guid Mount,DateTime Until)> _failed=new();
    private readonly SemaphoreSlim _slots=new(2,2);
    private readonly object _schedule=new();
    private readonly Dictionary<Guid,(MountDefinition Mount,Dictionary<string,MountEntry> Entries,bool Remote)> _browseDemand=new();
    private readonly Channel<Work> _queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(128){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _worker;
    private readonly Func<MountDefinition,MountEntry,bool,CancellationToken,Task<Dictionary<int,byte[]>?>>? _render;
    private Task? _dispose,_cancellation;
#if HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>隔离Shell集成测试可观察或抑制本实例的发布通知，不改变正式构建行为。</summary>
    internal Action<string>? ThumbnailNotificationForTests{get;set;}
#endif
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
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.ComponentModel.Win32Exception){ReportFailure(mount.Id,"缓存配置",error);return null;}
    }
    internal void Invalidate(MountDefinition mount,string entry)
    {try{if(_store.UsageBytes!=0)_store.Invalidate(mount.Id,entry);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){ReportFailure(mount.Id,"缓存失效",error);}}
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
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){ReportFailure(mount.Id,"元数据重绑",error);}
    }

    /// <summary>普通完成通知只处理完整本地文件；仅显式预览请求可读取远端，队列与挂载设置均有界。</summary>
    public void Queue(MountDefinition mount,MountEntry entry,bool allowRemote=false,bool fromBrowse=false)
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
            var work=new Work(mount,entry,allowRemote,_stop.Token,fromBrowse);if(!_pending.TryAdd(key,work)||!_queue.Writer.TryWrite(work)){_pending.TryRemove(key,out _);work.Dispose();}
        }
    }
    /// <summary>只取消离开当前视图的浏览任务；显式请求及已接受的本地内容通知保持各自所有权。</summary>
    internal void SetBrowseDemand(MountDefinition mount,IReadOnlyList<MountEntry> entries,bool remote)
    {
        Work[] canceled;lock(_schedule){var current=entries.ToDictionary(x=>x.Id,StringComparer.Ordinal);_browseDemand[mount.Id]=(mount,current,remote);canceled=_pending.Values.Where(x=>x.Mount.Id==mount.Id&&x.Browse&&(!current.TryGetValue(x.Entry.Id,out var latest)||latest.LocalPath!=x.Entry.LocalPath||latest.Version!=x.Entry.Version)).ToArray();}
        foreach(var work in canceled)_=work.RequestCancel();
        foreach(var entry in entries)Queue(mount,entry,remote,true);
    }
    /// <summary>任务页取消当前单文件预览，不关闭根开关；同一工作不立即自行重排，后续浏览可重建瞬时需求。</summary>
    internal bool CancelEntry(Guid mount,string entry)
    {
        var pending=_pending.Values.Where(x=>x.Mount.Id==mount&&x.Entry.Id==entry).ToArray();
        foreach(var work in pending){lock(work.Sync)work.UserCanceled=true;_=work.RequestCancel();}return pending.Length>0;
    }
    /// <summary>关闭／断开先阻止新任务，再取消并等待此挂载所有工作；不持有根锁等待其他挂载。</summary>
    public async Task CancelMountAsync(Guid mount)
    {
        Work[] pending;lock(_schedule){_blocked[mount]=0;_browseDemand.Remove(mount);pending=_pending.Values.Where(work=>work.Mount.Id==mount).ToArray();}
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
    internal string Describe(Guid mount)=>$"待处理={_pending.Values.Count(x=>x.Mount.Id==mount)}；暂缓={_failed.Values.Count(x=>x.Mount==mount&&x.Until>DateTime.UtcNow)}；停止接收={_blocked.ContainsKey(mount)}"
        +$"\n生成统计：已发布={_publishedCount.GetValueOrDefault(mount)}；不可用={_unavailableCount.GetValueOrDefault(mount)}；已取消={_canceledCount.GetValueOrDefault(mount)}"
        +(_lastOutcome.TryGetValue(mount,out var latest)?"\n最近生成／缓存结果："+DescribeOutcome(latest):"")
        +(_lastFailure.TryGetValue(mount,out var failure)?"\n最近失败："+DescribeOutcome(failure):"");
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
            if(!_available()||token.IsCancellationRequested)return;
            await gate.WaitAsync(token).ConfigureAwait(false);entered=true;await _slots.WaitAsync(token).ConfigureAwait(false);slot=true;token.ThrowIfCancellationRequested();
            RecordOutcome(mount.Id,"准备生成");var root=ConfigureMount(mount);var before=root is null?null:ThumbnailCacheReader.Capture(root,entry);if(before is null)return;
            if(_render is not null)
            {
                var frames=await _render(mount,entry,work.Remote,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                if(frames is null){_unavailableCount.AddOrUpdate(mount.Id,1,(_,count)=>count+1);_failed[key]=(mount.Id,DateTime.UtcNow.AddMinutes(5));if(!_lastOutcome.TryGetValue(mount.Id,out var outcome)||outcome.Stage=="准备生成")RecordOutcome(mount.Id,"未生成；格式、编解码器或读取条件不支持");return;}
                var after=ThumbnailCacheReader.Capture(root!,entry);
                if(after is null||before.ContentKey!=after.ContentKey||before.Length!=after.Length||before.LastWriteTime!=after.LastWriteTime||!before.Clean&&before.Stamp!=after.Stamp)return;
                if(!PublishCurrent(work,after,frames)){RecordOutcome(mount.Id,"发布前身份或设置已变化");return;}

            }
            else
            {
                using var lease=_enter is null?null:await _enter(mount,entry,token).ConfigureAwait(false);if(_enter is null||lease is not null)await ExtractAsync(work,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException error){if(!token.IsCancellationRequested){_failed[key]=(mount.Id,DateTime.UtcNow.AddMinutes(5));ReportFailure(mount.Id,"解码期限已到",error);}else{_canceledCount.AddOrUpdate(mount.Id,1,(_,count)=>count+1);RecordOutcome(mount.Id,"浏览或挂载工作已取消");}}
        catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or NotSupportedException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {_failed[key]=(mount.Id,DateTime.UtcNow.AddMinutes(5));ReportFailure(mount.Id,error.HResult==unchecked((int)0x80070718)?"读取预算耗尽":error.HResult==unchecked((int)0x80004001)?"严格范围读取不支持":"生成或缓存发布",error);}
        finally
        {
            if(slot)_slots.Release();if(entered)gate.Release();_pending.TryRemove(key,out _);
            (MountDefinition Mount,MountEntry Entry,bool Remote)? restart=null;
            lock(_schedule){if(work.Browse&&!work.UserCanceled&&token.IsCancellationRequested&&_browseDemand.TryGetValue(mount.Id,out var current)&&current.Entries.TryGetValue(entry.Id,out var latest))restart=(current.Mount,latest,current.Remote);}
            work.Completion.TrySetResult();work.Dispose();
            if(restart is {} next)Queue(next.Mount,next.Entry,next.Remote,true);
        }
    }
    /// <summary>浏览需求取消与发布共用短临界区；离开视图或设置关闭后不提交已取消任务的位图。</summary>
    private bool PublishCurrent(Work work,ThumbnailCacheBinding binding,IReadOnlyDictionary<int,byte[]> frames)
    {
        lock(_schedule){if(work.Cancel.IsCancellationRequested||_blocked.ContainsKey(work.Mount.Id)||_removed.ContainsKey(work.Mount.Id))return false;
            if(work.Browse&&(!_browseDemand.TryGetValue(work.Mount.Id,out var current)||!current.Entries.TryGetValue(work.Entry.Id,out var latest)||latest.LocalPath!=work.Entry.LocalPath||latest.Version!=work.Entry.Version))return false;
            if(!_store.Publish(binding,frames))return false;
            _publishedCount.AddOrUpdate(work.Mount.Id,1,(_,count)=>count+1);RecordOutcome(work.Mount.Id,"缓存已发布");
            // 与取消共用短临界区，取消收尾返回后不能再通知；不使用FLUSH，合并交给Shell异步分发。
#if HYBRIDMOUNT_HEADLESS_TEST
            if(ThumbnailNotificationForTests is {} notify)notify(binding.Path);else
#endif
            CloudFilesNative.NotifyThumbnailUpdated(binding.Path);return true;}
    }
    /// <summary>兼容独立缓存调用；解码由原生隔离进程执行，正文只通过原生保护租约读取。</summary>
    private async Task ExtractAsync(Work work,CancellationToken token)
    {
        var mount=work.Mount;var entry=work.Entry;var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        if(marker?.MountId!=mount.Id||marker.EntryId!=entry.Id||info!.Value.Info.ModifiedSize!=0||info.Value.Info.InSyncState!=1||!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length))return;
        Dictionary<int,byte[]>? frames;LocalFileStamp before;
        var lease=await NativeLocalReadLease.OpenAsync(mount,entry,token).ConfigureAwait(false);await using(lease)
        {
            before=lease.Stamp;var budget=PreviewRangeBudget.ForLocal(before.Length,lease.ReadAsync);frames=await new PreviewWorkerClient().DecodeAsync(Path.GetExtension(entry.LocalPath),before.Length,budget,token).ConfigureAwait(false);
        }
        before=lease.Stamp;token.ThrowIfCancellationRequested();if(frames is not null&&LocalFileState.Read(path).Key==before.Key&&ConfigureMount(mount) is {} root&&ThumbnailCacheReader.Capture(root,entry) is {} binding)PublishCurrent(work,binding,frames);
    }
    public long UsageBytes=>_store.UsageBytes;
    /// <summary>后台统一回收数据库BLOB，不散落图片文件或手工删除WAL。</summary>
    public long Trim(long needed){lock(_configurationGate){if(_store.UsageBytes==0)return 0;try{return _store.Trim(needed);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException){return 0;}}}
    /// <summary>只发信号；宿主同时取消其他工作之后再等待本队列结束。</summary>
    public Task RequestStop(){lock(_schedule){_queue.Writer.TryComplete();return _cancellation??=_stop.CancelAsync();}}
    public ValueTask DisposeAsync(){lock(_schedule)return new(_dispose??=StopAsync());}
    private async Task StopAsync(){await RequestStop().ConfigureAwait(false);await _worker.ConfigureAwait(false);_slots.Dispose();foreach(var gate in _mounts.Values)gate.Dispose();_stop.Dispose();}
}
