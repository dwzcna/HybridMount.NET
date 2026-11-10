using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Commons;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.Windows;

/// <summary>一个同步根的 CFAPI 连接；复制回调参数后在线程池读取，退出先取消／断开再释放委托。</summary>
public sealed partial class CloudMountSession : IAsyncDisposable
{
    private readonly MountDefinition _mount;
    private readonly Action<MountEntry>? _localContentReady;
    private readonly Action? _localChanged;
    private readonly IFileSource? _source;
    private readonly MountQueueBinding? _queueBinding;
    private readonly ConcurrentDictionary<string,MountingTask> _downloadTasks=new();
    private readonly object _fetchGate=new();
    private bool _clearing;
    private readonly MountRepository _repository;
    private readonly LoggerService _logger;
    private readonly NetworkFailureLog _networkFailures=new();
    private readonly CancellationTokenSource _stop = new();
    private readonly PriorityReadQueue _reads=new();
    private readonly SharedAsyncWork<string,SourceEntry> _metadata=new();
    private readonly bool _diagnostics=HydrationDiagnostics.Enabled;
    private NativeRuntimeClient? _runtime;
    private readonly string _sessionId=Guid.NewGuid().ToString("N");
    private sealed record NativeFetch(string FetchId,string EntryId,long Offset,long Length,byte Priority,bool Explicit,uint ProcessId,string Image);
    internal event Action<Exception>? RuntimeFailed;
    internal event Action<IReadOnlyList<string>>? PreviewDemandReceived;
    private string _previewObservation="尚未收到目录观察结果";
    /// <summary>最近一次观察的有界计数快照；不枚举Shell或读取源，供完整诊断直接查询。</summary>
    internal string PreviewObservationDescription=>Volatile.Read(ref _previewObservation);
    /// <summary>仅观察当前连接代次；没有图片或凭据经过此命令。</summary>
    internal void ObservePreviews(bool enabled){Runtime.Invoke("observePreviews",SessionArgs(new{enabled}));Volatile.Write(ref _previewObservation,enabled?"已启动，等待视口采样":"已关闭");}
    /// <summary>只保留采样策略、数量和数值错误码，失败样本明确显示而不触发重新观察或源读取。</summary>
    private static string DescribePreviewObservation(JsonElement value)
    {
        static int Count(JsonElement item,string name,int maximum)=>item.TryGetProperty(name,out var raw)&&raw.TryGetInt32(out var number)?Math.Clamp(number,0,maximum):0;
        var strategy=value.TryGetProperty("strategy",out var mode)&&mode.ValueKind==JsonValueKind.String&&mode.GetString()=="UIA.viewport"?"只读视口":"等待采样";
        var failure="";if(value.TryGetProperty("failures",out var failures)&&failures.ValueKind==JsonValueKind.Array)foreach(var item in failures.EnumerateArray().Take(16))if(item.TryGetProperty("hresult",out var raw)&&raw.TryGetInt32(out var code))failure=$"；最近采样HRESULT=0x{code:X8}";
        return $"策略={strategy}；窗口={Count(value,"windows",32)}；已匹配目录={Count(value,"accepted",16)}；候选={Count(value,"candidates",4096)}{failure}；UTC={DateTimeOffset.UtcNow:O}";
    }
    private sealed record FetchRequest(string EntryId,CancellationTokenSource Cancel,Task Work);
    private readonly ConcurrentDictionary<string,FetchRequest> _fetches=new();

    private Task? _dispose,_cancellation;
    private readonly HashSet<Task> _notifications=[];
    private readonly ConcurrentDictionary<string,int> _open = new(StringComparer.Ordinal);
    private bool _connected;
    private readonly ConcurrentDictionary<string,int> _explicitDownloads=new();
    private sealed class DownloadPriority(ConcurrentDictionary<string,int> counts,ConcurrentDictionary<string,MountingTask> tasks,string id) : IDisposable
    {
        private int _released;
        public void Dispose(){if(Interlocked.Exchange(ref _released,1)==0&&counts.AddOrUpdate(id,0,(_,value)=>Math.Max(0,value-1))==0)tasks.TryRemove(id,out _);}
    }
    /// <summary>为应用中明确提交的下载建立优先级范围；也覆盖系统在显式水合调用前产生的隐式打开请求。</summary>
    public IDisposable PrioritizeDownload(string id)
    {
        _explicitDownloads.AddOrUpdate(id,1,(_,value)=>value+1);
        if(_queueBinding is {} binding)_downloadTasks.TryAdd(id,TasksQueueService.CurrentTask is {} parent&&parent.MountId==_mount.Id?parent:new("pin:"+_mount.Id+":"+id,_mount.Id,binding.Generation,MountingPriority.User,DateTimeOffset.UtcNow,"keepOnDevice",EntryId:id));
        return new DownloadPriority(_explicitDownloads,_downloadTasks,id);
    }
    private readonly ConcurrentDictionary<string,int> _reading=new();
    private readonly ConcurrentDictionary<string,DateTimeOffset> _lastRead=new();
    public CloudMountSession(MountDefinition mount,IFileSource? source,MountRepository repository,LoggerService logger,bool blockHydration=false,Action<MountEntry>? localContentReady=null,Action? localChanged=null)
    { _localChanged=localChanged;_localContentReady=localContentReady;_mount=mount; _source=source;_queueBinding=(source as IMountNetworkSource)?.QueueBinding; _repository=repository; _logger=logger; _clearing=blockHydration;if(source is IReadDiagnosticsSource diagnostic)diagnostic.ReadDiagnostic+=OnReadDiagnostic; }
    internal event Action<LocalRenameNotice>? LocalRenameCompleted;
    internal event Action<string>? RenameRejected;
    private Dictionary<string,object?> SessionArgs(object? extra=null)
    {var args=NativeRuntimeArguments.Root(_mount,extra);args["sessionId"]=_sessionId;return args;}
    internal Dictionary<string,object?> EntryArgs(MountEntry entry,object? extra=null)
    {var args=NativeRuntimeArguments.Entry(_mount,entry,extra);args["sessionId"]=_sessionId;return args;}
    internal NativeRuntimeClient Runtime=>_runtime??throw new IOException("原生挂载尚未连接。");
    public void Connect()
    {
        _runtime=NativeRuntimeClient.Get();_runtime.EventReceived+=OnNativeEvent;_runtime.Failed+=OnRuntimeFailed;
        try{_runtime.Invoke("connectRoot",SessionArgs(new{blockHydration=_clearing}));_connected=true;}
        catch{_runtime.EventReceived-=OnNativeEvent;_runtime.Failed-=OnRuntimeFailed;throw;}
    }
#if HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>隔离回归通过真实事件入口注入采样状态；生产二进制不包含测试接口。</summary>
    internal void PreviewObservationForTests(IReadOnlyList<string> paths,bool complete)=>OnNativeEvent(new(_mount.Id.ToString("N"),_sessionId,"previewDemand",NativeRuntimeProtocol.Element(new{paths,complete})));
#endif
    private void OnReadDiagnostic(SourceReadDiagnostic value)
    {if(_diagnostics)_logger.Queue("水合诊断","Information",$"source={value.Strategy}; downloaded={value.DownloadedBytes}; fallback={value.FallbackReason??"none"}; completed={value.Completed}");}
    private void OnRuntimeFailed(Exception error)
    {
        _ = RequestStop();_logger.QueueException("原生后台","Error","CFAPI运行时已断开，保留恢复记录等待重新连接",error);RuntimeFailed?.Invoke(error);
    }
    private void OnNativeEvent(NativeRuntimeEvent message)
    {
        if(message.MountId!=_mount.Id.ToString("N")||message.SessionId!=_sessionId)return;
        var data=message.Data;
        if(message.Event=="previewDemand")
        {
            if(_clearing||_stop.IsCancellationRequested)return;
            try{if(data.TryGetProperty("observation",out var observation)&&observation.ValueKind==JsonValueKind.Object){Volatile.Write(ref _previewObservation,DescribePreviewObservation(observation));}if(data.TryGetProperty("complete",out var complete)&&complete.ValueKind==JsonValueKind.False)return;var paths=data.GetProperty("paths");if(paths.ValueKind!=JsonValueKind.Array||paths.GetArrayLength()>128)throw new IOException("预览提示数量无效。");var items=paths.EnumerateArray().Select(x=>x.GetString()??throw new IOException("预览提示为空。")).ToArray();if(items.Any(x=>x.Length>4096))throw new IOException("预览路径过长。");PreviewDemandReceived?.Invoke(items);}
            catch(Exception error){_logger.QueueException("缩略图","Warning","目录预览提示未处理",error,_mount.Id.ToString("N"));}
            return;
        }
        if(message.Event=="renameRejected")
        {
            if(_clearing||_stop.IsCancellationRequested)return;
            var warning=RenameRejectionMessage(data.GetProperty("sourcePath").GetString(),data.GetProperty("reason").GetString());
            _logger.Queue("重命名","Warning",warning);RenameRejected?.Invoke(warning);return;
        }
        if(message.Event=="fetch")
        {
            var fetch=NativeRuntimeProtocol.Read<NativeFetch>(data);
            lock(_fetchGate)
            {
                if(_clearing||_stop.IsCancellationRequested)return; // 原生clearRoot负责结束驱动请求，停止后不登记迟到工作。
                if(_metadataUpdating.Contains(fetch.EntryId)){var rejected=Task.Run(async()=>{try{await FinishFetchAsync(fetch,unchecked((int)0xc000cf16)).ConfigureAwait(false);}catch(Exception completion){if(Runtime.IsAvailable)_logger.QueueException("CFAPI","Warning","读取请求结束未确认",completion);}});_notifications.Add(rejected);_ = rejected.ContinueWith(done=>{lock(_fetchGate)_notifications.Remove(done);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);return;}
                var cancel=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var work=Task.Run(async()=>{await start.Task.ConfigureAwait(false);try{using var cancelOwner=_queueBinding?.Queue.RegisterCancellation("read:"+fetch.FetchId,()=>{if(!cancel.IsCancellationRequested)_=cancel.CancelAsync();return true;});await FetchAsync(fetch,new(fetch.ProcessId,fetch.Image),fetch.Explicit||_explicitDownloads.GetValueOrDefault(fetch.EntryId)>0,Stopwatch.GetTimestamp(),cancel.Token).ConfigureAwait(false);}finally{_fetches.TryRemove(fetch.FetchId,out _);cancel.Dispose();}});
                _fetches[fetch.FetchId]=new(fetch.EntryId,cancel,work);if(_clearing)cancel.Cancel();start.SetResult();
            }return;
        }
        if(message.Event=="cancelFetch")
        {if(_fetches.TryGetValue(data.GetProperty("fetchId").GetString()!,out var fetch))try{fetch.Cancel.Cancel();}catch(ObjectDisposedException){}return;}
        if(message.Event=="callbackFailure")
        {_logger.Queue("CFAPI","Error",$"原生回调未完成：type={data.GetProperty("type").GetInt32()}；HRESULT=0x{data.GetProperty("hresult").GetInt32():X8}");return;}
        var id=data.GetProperty("entryId").GetString()!;
        if(message.Event=="open"){_open.AddOrUpdate(id,1,(_,value)=>value+1);return;}
        if(message.Event=="close"){_open.AddOrUpdate(id,0,(_,value)=>Math.Max(0,value-1));if(!_clearing&&!_stop.IsCancellationRequested)_localChanged?.Invoke();NotifyPinWorkCompleted(id);QueueContentNotification(id);return;}
        var captured=data.Clone();QueueNativeNotification(()=>
        {
            var entries=_repository.Entries(_mount.Id);var entry=entries.FirstOrDefault(x=>x.Id==id);if(entry is null)return;
            if(message.Event=="deleted")foreach(var child in entries.Where(item=>RelativePaths.Contains(entry.LocalPath,item.LocalPath)&&item.RemotePresence!=RemotePresence.Deleted))_repository.SaveEntry(_mount.Id,child with{Deleted=true,Protected=true});
            else if(message.Event=="renamed")
            {
                var full=captured.GetProperty("path").GetString()!;var oldFull=captured.GetProperty("oldPath").GetString()!;if(!MountPathGuard.IsUnder(_mount.LocalPath,full)||!MountPathGuard.IsUnder(_mount.LocalPath,oldFull))return;
                var relative=Path.GetRelativePath(_mount.LocalPath,full).Replace('\\','/');var old=Path.GetRelativePath(_mount.LocalPath,oldFull).Replace('\\','/');
                foreach(var child in entries.Where(item=>RelativePaths.Contains(old,item.LocalPath)))_repository.SaveEntry(_mount.Id,child with{LocalPath=relative+child.LocalPath[old.Length..],Protected=true});
                if(!entry.IsDirectory)LocalRenameCompleted?.Invoke(new(old,relative,entry with{LocalPath=old}));
            }
        });
    }
    /// <summary>固定原因映射和有界文件名，不输出父路径或Native任意错误正文。</summary>
    internal static string RenameRejectionMessage(string? source,string? reason)
    {
        var name=Path.GetFileName(source?.TrimEnd('\\','/'));if(string.IsNullOrEmpty(name))name="未知文件";else{name=name.Replace('\r',' ').Replace('\n',' ');if(name.Length>160)name=name[..157]+"…";}
        return reason switch
        {
            "outOfRoot"=>$"不支持将文件移出同步根：{name}，请复制后再删除",
            "sessionStopping"=>$"挂载正在停止，无法重命名：{name}，请稍后重试",
            "differentMount"=>$"不支持跨挂载移动文件：{name}，请复制后再删除",
            "unsafeTarget"=>$"重命名目标目录不可安全访问：{name}，请检查目标目录后重试",
            _=>$"重命名未完成：{name}，请检查目标位置和挂载状态"
        };
    }
    private void QueueNativeNotification(Action action)
    {
        lock(_fetchGate){if(_stop.IsCancellationRequested)return;var work=Task.Run(()=>{try{action();}catch(Exception error){_logger.QueueException("原生通知","Error","本地变更通知未完成",error);}});_notifications.Add(work);_ = work.ContinueWith(done=>{lock(_fetchGate)_notifications.Remove(done);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);}
    }
    /// <summary>只有取消区间完整覆盖该回调时才结束其等待；部分重叠继续服务剩余需要的数据，避免误伤其他请求。</summary>
    internal static bool CancellationCovers(long offset,long length,long cancelOffset,long cancelLength)
    {
        if(offset<0||length<=0||cancelOffset<0||cancelOffset>offset||cancelLength==0||cancelLength< -1)return false;
        if(cancelLength==-1)return true;
        var delta=offset-cancelOffset;return delta<=cancelLength&&length<=cancelLength-delta;
    }
    /// <summary>完成回复丢失可幂等重试；穷尽后使未知运行时显式失效，不按静默时间解除有效读取保护。</summary>
    private async Task FinishFetchAsync(NativeFetch fetch,int status)
    {
        try{await FinishFetchWithRetryAsync(()=>Runtime.InvokeAsync("finishFetch",SessionArgs(new{fetchId=fetch.FetchId,status})),()=>Runtime.IsAvailable).ConfigureAwait(false);}
        catch(Exception error){if(Runtime.IsAvailable&&error is not OperationCanceledException&&error.HResult!=unchecked((int)0x80070490)&&!IsCanceledCompletion(error.HResult))Runtime.AbortForSafety(error);throw;}
    }
    /// <summary>最多三次确认，终态、停止及合作取消不重试；不重传任何正文。</summary>
    internal static async Task FinishFetchWithRetryAsync(Func<Task> complete,Func<bool> available)
    {
        for(var attempt=0;;attempt++)
            try{await complete().ConfigureAwait(false);return;}
            catch(Exception error)when(attempt<2&&error is not OperationCanceledException&&available()&&error.HResult!=unchecked((int)0x80070490)&&!IsCanceledCompletion(error.HResult))
            {await Task.Delay(attempt==0?100:500).ConfigureAwait(false);if(!available())throw;}
    }
    internal static bool IsCanceledCompletion(int result)=>result is unchecked((int)0x8007017c) or unchecked((int)0x80070189) or unchecked((int)0x800703e3);
    private async Task FetchAsync(NativeFetch info,HydrationProcess process,bool explicitHydration,long queued,CancellationToken token)
    {
        var id=info.EntryId;var offset=info.Offset;var length=info.Length;IDisposable? lease=null;long bytesTransferred=0;double queueMs=0,firstBlockMs=-1;var outcome="ok";_reading.AddOrUpdate(id,1,(_,value)=>value+1);_lastRead[id]=DateTimeOffset.UtcNow;
        try
        {
            if(_queueBinding is null)lease=await _reads.EnterAsync(info.Priority,explicitHydration,token).ConfigureAwait(false);queueMs=Stopwatch.GetElapsedTime(queued).TotalMilliseconds;
            var entry=_repository.Entries(_mount.Id).FirstOrDefault(item=>item.Id==id&&!item.Deleted&&item.RemotePresence!=RemotePresence.Deleted)??throw new FileNotFoundException("占位身份不存在。");
            var ownedHydration=info.Explicit||info.ProcessId==(uint)Runtime.ProcessId||info.ProcessId==(uint)Environment.ProcessId;
            var parent=ownedHydration?_downloadTasks.GetValueOrDefault(id):null; // 外部普通打开保持P0，不因同文件正在固定而降级。
            using var queueTask=_queueBinding?.Queue.Enter(_queueBinding,parent??new("read:"+info.FetchId,_mount.Id,_queueBinding.Generation,MountingPriority.Demand,DateTimeOffset.UtcNow,"download",entry.SourcePath,entry.Id,entry.Version));
            var source=_source??throw new IOException("清空期间不提供内容读取。");_remoteSnapshots.TryGetValue(id,out var staged);
            if(staged is null&&_repository.HasPendingRemote(_mount.Id,entry.SourcePath))throw new SourceConflictException("远端更新待提交，暂缓旧版本水合。");
            var metadata=staged?.Metadata??await _metadata.RunAsync(entry.SourcePath,cancel=>source.GetMetadataAsync(entry.SourcePath,cancel),token).ConfigureAwait(false);
            await using var snapshot=staged is null?null:new FileStream(staged.Path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,262144,FileOptions.Asynchronous|FileOptions.RandomAccess);
            if(!source.IsSameVersion(metadata.Version,entry.Version))throw new SourceConflictException("源版本已变化，拒绝混入旧缓存。");
            if(offset<0||length<=0||offset>metadata.Size||length>metadata.Size-offset)throw new IOException("系统请求范围越界。");
            var start=offset/4096*4096;var end=Math.Min(metadata.Size,checked((offset+length+4095)/4096*4096));var localStamp=HydrationStamp(entry,metadata);var fetchId=Guid.Parse(info.FetchId);
            async ValueTask Transfer(ReadOnlyMemory<byte> block,CancellationToken cancel)
            {cancel.ThrowIfCancellationRequested();await Runtime.TransferAsync(fetchId,start,block,cancel).ConfigureAwait(false);start+=block.Length;bytesTransferred+=block.Length;if(firstBlockMs<0)firstBlockMs=Stopwatch.GetElapsedTime(queued).TotalMilliseconds;}
            if(snapshot is null)
            {
                if(_queueBinding is null)await source.ReadRangeSequentialAsync(metadata,start,end-start,Transfer,NativeRuntimeProtocol.BlockBytes,token).ConfigureAwait(false);
                else while(start<end)
                {
                    // 缓冲名额只跨一个范围块；大固定下载不能在所有后续块之前封住按需读取。
                    using var buffer=await _reads.EnterStrictAsync(parent?.Priority??MountingPriority.Demand,token).ConfigureAwait(false);
                    var bytes=System.Buffers.ArrayPool<byte>.Shared.Rent(NativeRuntimeProtocol.BlockBytes);
                    try
                    {
                        var count=(int)Math.Min(NativeRuntimeProtocol.BlockBytes,end-start);var filled=0;
                        while(filled<count){var read=await source.ReadRangeAsync(metadata,start+filled,bytes.AsMemory(filled,count-filled),token).ConfigureAwait(false);if(read<=0||read>count-filled)throw new EndOfStreamException("远端区间提前结束。");filled+=read;}
                        await Transfer(bytes.AsMemory(0,count),token).ConfigureAwait(false);
                    }
                    finally{System.Buffers.ArrayPool<byte>.Shared.Return(bytes,clearArray:true);}
                }
            }
            else
            {
                while(start<end)
                {
                    using var buffer=_queueBinding is null?null:await _reads.EnterStrictAsync(parent?.Priority??MountingPriority.Demand,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                    var bytes=System.Buffers.ArrayPool<byte>.Shared.Rent(NativeRuntimeProtocol.BlockBytes);
                    try{var count=(int)Math.Min(NativeRuntimeProtocol.BlockBytes,end-start);snapshot.Position=start;await snapshot.ReadExactlyAsync(bytes.AsMemory(0,count),token).ConfigureAwait(false);await Transfer(bytes.AsMemory(0,count),token).ConfigureAwait(false);}
                    finally{System.Buffers.ArrayPool<byte>.Shared.Return(bytes,clearArray:true);}
                }
            }
            await FinishFetchAsync(info,0).ConfigureAwait(false);if(_networkFailures.Recovered(out var recovered))_logger.Queue("按需读取","Information",recovered);RecordHydrationWrite(entry,metadata,localStamp);_localContentReady?.Invoke(entry);
        }
        catch(Exception error)
        {
            var canceled=(error is OperationCanceledException||error is System.Runtime.InteropServices.COMException native&&IsCanceledCompletion(native.HResult))&&token.IsCancellationRequested;outcome=canceled?"canceled":"failed";
            try{await FinishFetchAsync(info,canceled?unchecked((int)0xc000cf16):unchecked((int)0xc000cf00)).ConfigureAwait(false);}catch(Exception completion){if(Runtime.IsAvailable)_logger.QueueException("CFAPI","Warning","读取请求结束未确认",completion);}
            if(!canceled){if(error is not SourceConnectionException connection)_logger.QueueException("按需读取","Error","操作未完成",error);else if(_networkFailures.ShouldWrite(connection.Diagnostic,out var suffix))_logger.QueueException("按需读取","Error","按需读取失败"+suffix,connection);}
        }
        finally
        {
            lease?.Dispose();_reading.AddOrUpdate(id,0,(_,value)=>Math.Max(0,value-1));NotifyPinWorkCompleted(id);if(_diagnostics)_logger.Queue("水合诊断","Information",$"entry={id}; pid={process.Id}; image={process.Image}; offset={offset}; length={length}; priority={info.Priority}; explicit={explicitHydration}; queueMs={queueMs:F0}; firstBlockMs={firstBlockMs:F0}; transferred={bytesTransferred}; elapsedMs={Stopwatch.GetElapsedTime(queued).TotalMilliseconds:F0}; result={outcome}");
        }
    }
    /// <summary>阻止新增水合和重命名，取消并等候在途读取；保留删除确认回调。</summary>
    public async Task BeginClearAsync()
    {
        FetchRequest[] pending;
        lock(_fetchGate) { _clearing=true;pending=_fetches.Values.ToArray(); }
        if(_connected&&_runtime?.IsAvailable==true)await _runtime.InvokeAsync("clearRoot",SessionArgs()).ConfigureAwait(false);
        foreach(var item in pending)try{item.Cancel.Cancel();}catch(ObjectDisposedException){}
        await Task.WhenAll(pending.Select(item=>item.Work)).ConfigureAwait(false);
    }
    public bool IsActive(string id) => (_open.TryGetValue(id,out var count) && count>0)||(_reading.TryGetValue(id,out var reads)&&reads>0)||_fetches.Values.Any(x=>x.EntryId==id);
    public DateTimeOffset LastRead(string id)=>_lastRead.GetValueOrDefault(id,DateTimeOffset.MinValue);
    /// <summary>在排他 oplock 下验证干净状态并以 CFAPI 更新/脱水；失败保留旧索引版本。</summary>
    public bool RefreshPlaceholder(MountEntry entry,SourceEntry metadata)
    {
        if(!BeginMetadataUpdate(entry.Id))return false;
        try{var result=Runtime.Invoke("refreshPlaceholder",EntryArgs(entry,new{size=metadata.Size,time=metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc()}));RecordLocalWrite(entry,NativeRuntimeArguments.Stamp(result));return true;}
        finally{EndMetadataUpdate(entry.Id);}
    }
    /// <summary>原生端创建真实占位并返回文件戳；回执到达后才提交索引。</summary>
    public void CreatePlaceholder(MountEntry entry,SourceEntry metadata)
    {
        var result=Runtime.Invoke("createPlaceholder",EntryArgs(entry,new{size=metadata.Size,time=metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc()}));_repository.SaveEntry(_mount.Id,entry with{Materialized=true});var usn=NativeRuntimeProtocol.Read<long>(result.GetProperty("createUsn"));RecordCreatedWrite(entry,metadata,usn);
    }
    private void QueueContentNotification(string id)
    {
        lock(_fetchGate)
        {
            if(_clearing||_stop.IsCancellationRequested)return;
            var work=Task.Run(()=>{try{var entry=_repository.Entries(_mount.Id).FirstOrDefault(item=>item.Id==id);if(entry is not null)_localContentReady?.Invoke(entry);}catch(Exception error){_logger.Queue("内容通知","Warning",error.GetType().Name);}});
            _notifications.Add(work);_ = work.ContinueWith(done=>{lock(_fetchGate)_notifications.Remove(done);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
    }
    /// <summary>同步禁止新水合，异步取消读取；不在根锁或回调内等待网络结束。</summary>
    public Task RequestStop(){lock(_fetchGate){_clearing=true;return _cancellation??=StopAdmissionAsync();}}
    private async Task StopAdmissionAsync(){await _stop.CancelAsync().ConfigureAwait(false);if(_connected&&_runtime?.IsAvailable==true)await _runtime.InvokeAsync("clearRoot",SessionArgs()).ConfigureAwait(false);}
    public ValueTask DisposeAsync(){lock(_fetchGate)return new(_dispose??=StopAsync());}
    private async Task StopAsync()
    {
        await RequestStop().ConfigureAwait(false);
        if(_connected&&_runtime?.IsAvailable==true)await _runtime.InvokeAsync("clearRoot",SessionArgs()).ConfigureAwait(false);
        await _pinnedHydration.DisposeAsync().ConfigureAwait(false);foreach(var gate in _pinGates.Values)gate.Dispose();
        Task[] pending;lock(_fetchGate)pending=_fetches.Values.Select(item=>item.Work).Concat(_notifications).ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);await _metadata.DisposeAsync().ConfigureAwait(false);
        // 回调已结束才断开 CFAPI；常规停止不注销持久同步根。
        if(_connected&&_runtime?.IsAvailable==true)await _runtime.InvokeAsync("disconnectRoot",SessionArgs()).ConfigureAwait(false);_connected=false;
        if(_runtime is not null){_runtime.EventReceived-=OnNativeEvent;_runtime.Failed-=OnRuntimeFailed;}
        if(_source is IReadDiagnosticsSource diagnostic)diagnostic.ReadDiagnostic-=OnReadDiagnostic;
        _stop.Dispose();
    }
}
