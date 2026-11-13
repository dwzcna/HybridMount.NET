using System.Threading.Channels;
namespace HybridMount.Services.FileTasks;

/// <summary>.NET文件任务唯一执行入口；持久化意图、分发适配器、步骤核验及原生结果回应互相分离。</summary>
internal sealed class TaskDispatcher(MountRepository repository,LoggerService logger):IAsyncDisposable
{
    /// <summary>所有挂载共用待接纳内存上限，防止断库时重发复制为每挂载4096项。</summary>
    internal sealed class InboxBudget
    {
        private readonly object _gate=new();private int _count;private long _bytes;
        internal bool Take(long bytes){lock(_gate){if(_count>=4096||_bytes+bytes>8*1024*1024)return false;_count++;_bytes+=bytes;return true;}}
        internal void Release(long bytes){lock(_gate){_count--;_bytes-=bytes;}}
    }
    private readonly InboxBudget _inboxBudget=new();
    private readonly TaskQueue _queue=new(repository);private readonly TaskRunner _observations=new();
    /// <summary>不需要文件步骤的重复观测只确认已有持久证据，不生成新的逐文件任务。</summary>
    internal Task<T> AcknowledgeObservedAsync<T>(Func<CancellationToken,Task<T>> verify,CancellationToken token)=>_observations.RunAsync(verify,token);
    private readonly object _gate=new();private readonly Dictionary<Guid,RootLease> _roots=[];
    /// <summary>只在依赖就绪后交给Runner；Verifier确认后保存终态，取消不表示已发出的修改回滚。</summary>
    internal Task ExecuteAsync(FileTaskRecord task,Func<CancellationToken,Task> execute,Func<CancellationToken,Task<TaskVerification>> verify,CancellationToken token)
        =>_queue.RunAsync(task,async cancel=>{await execute(cancel).ConfigureAwait(false);return true;},verify,token);
    internal Task<T> ExecuteAsync<T>(FileTaskRecord task,Func<CancellationToken,Task<T>> execute,Func<CancellationToken,Task<TaskVerification>> verify,CancellationToken token)
        =>_queue.RunAsync(task,execute,verify,token);
    internal void ContentChanged(string taskId,long generation)=>_queue.ReplaceContent(taskId,generation);
    /// <summary>持久业务记录变化后补充任务图；在途任务的终态必须由Verifier发布，进度字节不重复写图。</summary>
    internal void Observe(FileTaskRecord task)
    {
        var current=repository.ReadFileTask(task.Id);
        if(current is null||current.Generation<task.Generation)repository.PrepareFileTask(task,[task.Operation]);
        if(!_queue.IsRunning(task.Id)&&current?.State!=task.State)repository.SetFileTaskState(task,task.State,"business");
    }
    internal RootLease Register(Guid mount,string session,NativeRuntimeClient runtime,Func<NativeTaskIntent,CancellationToken,Task<IReadOnlyCollection<string>?>> route,Func<Task<long>>? stopCapture=null,Action<NativeTaskIntent>? accepted=null,Func<IReadOnlyList<NativeTaskIntent>,CancellationToken,Task<IReadOnlyDictionary<string,IReadOnlyCollection<string>>?>>? routeBatch=null)
    {
        lock(_gate)
        {
            if(_roots.ContainsKey(mount))throw new IOException("原生监听所有者尚未退出。");
            var owner=new RootLease(repository,logger,mount,session,runtime,route,stopCapture,accepted,routeBatch,_inboxBudget);_roots.Add(mount,owner);return owner;
        }
    }
    internal async Task StopCaptureAsync(Guid mount)
    {
        RootLease? owner;lock(_gate)_roots.TryGetValue(mount,out owner);if(owner is not null)await owner.StopCaptureAsync().ConfigureAwait(false);
    }
    internal async Task RemoveAsync(Guid mount)
    {
        RootLease? owner;lock(_gate){_roots.Remove(mount,out owner);}if(owner is not null)await owner.DisposeAsync().ConfigureAwait(false);
    }
    internal void StopAdmission()=>_queue.StopAdmission();
    internal async Task StopCapturesAsync()
    {
        RootLease[] owners;lock(_gate)owners=_roots.Values.ToArray();await Task.WhenAll(owners.Select(x=>x.StopCaptureAsync())).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        _queue.StopAdmission();RootLease[] owners;lock(_gate){owners=_roots.Values.ToArray();_roots.Clear();}
        await _queue.DrainAsync().ConfigureAwait(false);foreach(var owner in owners)await owner.DisposeAsync().ConfigureAwait(false);
    }
    /// <summary>每挂载独立接纳、路由与结果发送；传输线程仅投递内存，慢处理不会阻塞其他挂载。</summary>
    internal sealed class RootLease:IAsyncDisposable
    {
        private readonly MountRepository _repository;private readonly LoggerService _logger;private readonly Guid _mount;private readonly string _session;
        private readonly NativeRuntimeClient _runtime;private readonly Func<NativeTaskIntent,CancellationToken,Task<IReadOnlyCollection<string>?>> _route;private readonly Func<Task<long>>? _captureStop;private readonly Action<NativeTaskIntent>? _accepted;private readonly InboxBudget _budget;
        private readonly Func<IReadOnlyList<NativeTaskIntent>,CancellationToken,Task<IReadOnlyDictionary<string,IReadOnlyCollection<string>>?>>? _routeBatch;
        private readonly Channel<object> _inbox=Channel.CreateBounded<object>(new BoundedChannelOptions(4096){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
        private readonly CancellationTokenSource _stop=new();private readonly IDisposable _sink;
        private readonly Channel<bool> _routeWake=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest});
        private readonly Channel<bool> _resultWake=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest});
        private readonly Task _accepting,_routing,_results;private readonly object _postGate=new(),_stopGate=new();private long _bytes;private volatile bool _paused;private int _needsRescan;private Task? _stopCapture,_dispose;
        internal RootLease(MountRepository repository,LoggerService logger,Guid mount,string session,NativeRuntimeClient runtime,Func<NativeTaskIntent,CancellationToken,Task<IReadOnlyCollection<string>?>> route,Func<Task<long>>? stopCapture,Action<NativeTaskIntent>? accepted,Func<IReadOnlyList<NativeTaskIntent>,CancellationToken,Task<IReadOnlyDictionary<string,IReadOnlyCollection<string>>?>>? routeBatch,InboxBudget budget)
        {
            _repository=repository;_logger=logger;_mount=mount;_session=session;_runtime=runtime;_route=route;_captureStop=stopCapture;_accepted=accepted;_routeBatch=routeBatch;_budget=budget;
            _sink=runtime.RegisterTaskSink(mount,session,Post,id=>Post(id));
            _repository.TaskJournalChanged+=ResultsChanged;_routeWake.Writer.TryWrite(true);_resultWake.Writer.TryWrite(true);
            _accepting=Task.Run(AcceptAsync);_routing=Task.Run(RouteAsync);_results=Task.Run(SendResultsAsync);
        }
        private static long Size(object item)=>item is NativeTaskIntent intent?256L+2L*(intent.Path.Length+(intent.OldPath?.Length??0)):128L;
        private bool Post(object item)
        {
            lock(_postGate)
            {
                var size=Size(item);if(_stop.IsCancellationRequested||!_budget.Take(size))return false;
                if(!_inbox.Writer.TryWrite(item)){_budget.Release(size);return false;}_bytes+=size;return true;
            }
        }
        private async Task AcceptAsync()
        {
            try{await foreach(var item in _inbox.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                lock(_postGate){_bytes-=Size(item);_budget.Release(Size(item));}
                try
                {
                    if(item is string result){_repository.AcknowledgeTaskResult(_mount,result);continue;}
                    var intent=(NativeTaskIntent)item;var acceptance=_repository.AcceptTaskIntent(intent);if(acceptance.Created)_accepted?.Invoke(intent);
                    _runtime.SendTaskAcknowledgment(intent,"accepted");_routeWake.Writer.TryWrite(true);
                    if(acceptance.Gap){Interlocked.Exchange(ref _needsRescan,1);_repository.DeferTaskIntent(intent,"sequence-gap");}
                }
                catch(InvalidDataException error){_runtime.AbortForSafety(error);}
                catch(Exception error)
                {
                    if(item is NativeTaskIntent intent)_runtime.SendTaskAcknowledgment(intent,"deferred");
                    _logger.QueueException("原生任务","Warning","意图尚未持久接纳，等待同ID重发",error,_mount.ToString("N"));
                }
            }}catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
        }
        private void ResultsChanged(Guid mount){if(mount==_mount)_resultWake.Writer.TryWrite(true);}
        /// <summary>没有意图时不轮询数据库；暂缓项最多两秒补查，新事件可提前唤醒。</summary>
        private async Task RouteAsync()
        {
            var token=_stop.Token;var retry=false;
            try{while(!token.IsCancellationRequested)
            {
                await WaitForWakeAsync(_routeWake,retry?TimeSpan.FromSeconds(2):Timeout.InfiniteTimeSpan,token).ConfigureAwait(false);retry=false;
                if(_paused)continue;
                try
                {
                    var intents=_repository.UnroutedTaskIntents(_mount);
                    if(_routeBatch is not null&&intents.Count>0)
                    {
                        foreach(var stale in intents.Where(item=>item.SessionId!=_session))_repository.FinishInterruptedTaskIntent(stale);
                        var current=intents.Where(item=>item.SessionId==_session).ToArray();
                        if(current.Length>0)
                        {
                            var rescan=Interlocked.Exchange(ref _needsRescan,0)!=0;
                            if(rescan)current[0]=current[0] with{RescanRequired=true};
                            try
                            {
                                var mapping=await _routeBatch(current,token).ConfigureAwait(false);
                                if(mapping is null){retry=true;if(rescan)Interlocked.Exchange(ref _needsRescan,1);}
                                else foreach(var intent in current)
                                {
                                    if(!mapping.TryGetValue(intent.TaskId,out var tasks)){retry=true;continue;}
                                    _repository.BindTaskIntent(intent,tasks);
                                    foreach(var id in tasks)if(_repository.ReadFileTask(id) is {} task)_repository.SetFileTaskState(task,task.State,"dispatch");
                                }
                            }
                            catch(Exception)when(!token.IsCancellationRequested){Interlocked.Exchange(ref _needsRescan,1);retry=true;throw;}
                        }
                        if(intents.Count==256&&!retry)_routeWake.Writer.TryWrite(true);
                        continue;
                    }
                    foreach(var intent in intents)
                    {
                        if(_paused)break;
                        try
                        {
                            if(intent.SessionId!=_session){_repository.FinishInterruptedTaskIntent(intent);continue;}
                            var rescan=Interlocked.Exchange(ref _needsRescan,0)!=0||intent.RescanRequired;
                            var routed=rescan?intent with{EventKind="rescan",Path="",OldPath=null}:intent;
                            var tasks=await _route(routed,token).ConfigureAwait(false);
                            if(tasks is null){retry=true;if(rescan)Interlocked.Exchange(ref _needsRescan,1);continue;}
                            _repository.BindTaskIntent(intent,tasks);
                            foreach(var id in tasks)if(_repository.ReadFileTask(id) is {} task)_repository.SetFileTaskState(task,task.State,"dispatch");
                        }
                        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
                        catch(Exception error)
                        {
                            retry=true;Interlocked.Exchange(ref _needsRescan,1);
                            try{_repository.DeferTaskIntent(intent,"route");}catch(Exception){/* 保留已接纳意图，下一轮重新核对。 */}
                            _logger.QueueException("原生任务","Warning","本地意图处理暂缓，保留重试",error,intent.TaskId);
                        }
                    }
                    if(intents.Count==256)retry=true;
                }
                catch(Exception error)when(error is not OperationCanceledException)
                {retry=true;_logger.QueueException("原生任务","Warning","持久意图暂时无法读取，稍后重新核对",error,_mount.ToString("N"));}
            }}catch(OperationCanceledException)when(token.IsCancellationRequested){}
        }
        /// <summary>仅有待发结果时按一秒重送；空闲完全依赖事务内存唤醒，不放大根查询。</summary>
        private async Task SendResultsAsync()
        {
            var token=_stop.Token;var retry=false;
            try{while(!token.IsCancellationRequested)
            {
                await WaitForWakeAsync(_resultWake,retry?TimeSpan.FromSeconds(1):Timeout.InfiniteTimeSpan,token).ConfigureAwait(false);
                try{var results=_repository.PendingTaskResults(_mount);retry=results.Count>0;foreach(var result in results)_runtime.SendTaskResult(result);}
                catch(Exception error){retry=true;_logger.QueueException("原生任务","Warning","任务结果仍保留，稍后重送",error,_mount.ToString("N"));}
            }}catch(OperationCanceledException)when(token.IsCancellationRequested){}
        }
        private static async Task WaitForWakeAsync(Channel<bool> wake,TimeSpan timeout,CancellationToken token)
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);if(timeout!=Timeout.InfiniteTimeSpan)deadline.CancelAfter(timeout);
            try{await wake.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);}catch(OperationCanceledException)when(!token.IsCancellationRequested){}
            while(wake.Reader.TryRead(out _)){}
        }
        internal Task StopCaptureAsync(){lock(_stopGate)return _stopCapture??=StopCaptureCoreAsync();}
        private async Task StopCaptureCoreAsync()
        {
            _paused=true;if(!_runtime.IsAvailable)return;
            long sequence;
            if(_captureStop is not null)sequence=await _captureStop().ConfigureAwait(false);
            else
            {
                var response=await _runtime.InvokeAsync("stopTaskWatch",new{mountId=_mount,sessionId=_session}).ConfigureAwait(false);
                sequence=NativeRuntimeProtocol.Read<long>(response.GetProperty("sequence"));
            }
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(_repository.AcceptedTaskSequence(_mount,_session)<sequence)await Task.Delay(20,deadline.Token).ConfigureAwait(false);
            // 意图已全部持久拥有，原生端可释放该停止会话的内存；结果仍按ResultID单独确认。
            while(!_runtime.SendTaskFrame(new{kind="sessionRetired",taskId=Guid.NewGuid().ToString("N"),mountId=_mount,sessionId=_session}))
            {
                if(!_runtime.IsAvailable)break;await Task.Delay(20,deadline.Token).ConfigureAwait(false);
            }
        }
        public ValueTask DisposeAsync(){lock(_stopGate)return new(_dispose??=DisposeCoreAsync());}
        private async Task DisposeCoreAsync()
        {
            try{await StopCaptureAsync().ConfigureAwait(false);}finally
            {
                // 新意图停止后排空已经接纳的内存项；未决业务与结果保留到下次连接核对。
                _repository.TaskJournalChanged-=ResultsChanged;_sink.Dispose();_inbox.Writer.TryComplete();await _accepting.ConfigureAwait(false);
                await _stop.CancelAsync().ConfigureAwait(false);await Task.WhenAll(_routing,_results).ConfigureAwait(false);_stop.Dispose();
            }
        }
    }
}
