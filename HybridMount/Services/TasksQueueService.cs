using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Services;

/// <summary>应用拥有的异步协调服务；四种每挂载工作流与全局实际网络额度分离，等待不占固定系统线程。</summary>
public sealed partial class TasksQueueService : IAsyncDisposable
{
    private sealed class Pending(MountingTask task,long sequence,SemaphoreSlim backlog,bool workflow)
    {
        public readonly MountingTask Task=task;
        public readonly bool Workflow=workflow;
        public readonly long Submitted=System.Diagnostics.Stopwatch.GetTimestamp();
        public readonly SemaphoreSlim Backlog=backlog;
        public readonly long Sequence=sequence;
        public readonly TaskCompletionSource<NetworkLease> Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _sync=new();
    private readonly List<Pending> _pending=[];
    private readonly Dictionary<long,NetworkLease> _active=[];
    private readonly Dictionary<(MountingPriority,Guid),long> _turns=[];
    private long _turn,_granted,_waitTicks,_maximumWaitTicks;
    private readonly Dictionary<Guid,long> _generations=[];
    private readonly Dictionary<Guid,CancellationTokenSource> _mountStops=[];
    private readonly HashSet<(Guid,string)> _completed=[];
    private readonly Dictionary<(Guid,string),MountingTask> _workflows=[];
    private readonly List<ScopeState> _demandScopes=[];
    private readonly List<CancelRegistration> _cancelActions=[];
    private readonly List<SharedResource> _sharedResources=[];
    private readonly IReadOnlyDictionary<MountingPriority,SemaphoreSlim> _backlogs;
    private readonly IReadOnlyDictionary<MountingPriority,SemaphoreSlim> _workflowBacklogs,_ownedBacklogs;
    private int _parallelism;
    private long _sequence;
    private bool _stopping,_disposed;
    private readonly CancellationTokenSource _observerStop=new();
    private readonly CancellationTokenSource _admissionStop=new();
    private readonly HashSet<Task> _observers=[];
    private TaskCompletionSource _drained=CompletedSignal();
    private Task? _dispose;
    private static readonly AsyncLocal<ScopeState?> Ambient=new();
    private sealed record ScopeState(MountQueueBinding Binding,MountingTask? Task);
    private sealed class Scope(TasksQueueService owner,ScopeState current,ScopeState? before):IDisposable
    {
        private int _disposed;
        public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;Ambient.Value=before;lock(owner._sync){var index=owner._demandScopes.FindIndex(value=>ReferenceEquals(value,current));if(index>=0)owner._demandScopes.RemoveAt(index);owner.Pump();}owner.RaiseChanged();}
    }
    private sealed class SharedResource(TasksQueueService owner,MountingTask task,string resource,NetworkLease? retained):IDisposable
    {
        internal MountingTask Task {get;}=task;internal string Resource {get;}=resource;private int _disposed;
        public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;lock(owner._sync){owner._sharedResources.Remove(this);owner.Pump();}retained?.Dispose();owner.RaiseChanged();}
    }
    /// <summary>跨挂载共享配额等必要资源继承等待者优先级；仅内存调度关系，不记录账户或授权远端写入。</summary>
    internal IDisposable ShareResource(MountingTask task,string resource)
    {
        SharedResource value;
        lock(_sync)
        {
            // 在共享Task.Run接纳前同步保留流程；首个等待者取消，不能让后续同通道流程反向堵住共享生产者。
            var retained=_active.Values.FirstOrDefault(x=>x.IsWorkflow&&SameFlow(x.Task,task))?.Borrow();
            value=new(this,task,resource,retained);_sharedResources.Add(value);Pump();
        }
        RaiseChanged();return value;
    }
    private sealed class CancelRegistration(TasksQueueService owner,string id,Func<bool> cancel):IDisposable
    {
        internal string Id {get;}=id;internal Func<bool> Cancel {get;}=cancel;
        public void Dispose(){lock(owner._sync)owner._cancelActions.Remove(this);}
    }
    /// <summary>业务所有者登记自身取消源；服务不伪造回滚，也不取消同挂载的其他文件。</summary>
    internal IDisposable RegisterCancellation(string id,Func<bool> cancel)
    {var value=new CancelRegistration(this,id,cancel);lock(_sync)_cancelActions.Add(value);return value;}
    /// <summary>全局默认四路、最高128；每优先级最多1024个已接纳等待，低优先级不能耗尽高优先级入口。</summary>
    public TasksQueueService(int parallelism=4,int capacity=1024)
    {if(parallelism is <1 or >128)throw new ArgumentOutOfRangeException(nameof(parallelism));ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);_parallelism=parallelism;_backlogs=Backlogs();_workflowBacklogs=Backlogs();_ownedBacklogs=Backlogs();
        IReadOnlyDictionary<MountingPriority,SemaphoreSlim> Backlogs()=>Enum.GetValues<MountingPriority>().ToDictionary(value=>value,_=>new SemaphoreSlim(capacity,capacity));}
    public event Action? Changed;
    /// <summary>动态上限只改变下一次授予；降低时不取消已有正文所有者。</summary>
    internal void SetConcurrency(int value)
    {
        if(value is <1 or >128)throw new ArgumentOutOfRangeException(nameof(value));
        lock(_sync){ObjectDisposedException.ThrowIf(_disposed,this);_parallelism=value;Pump();}RaiseChanged();
    }
    internal int Concurrency {get{lock(_sync)return _parallelism;}}
    /// <summary>只读内存诊断，不访问源或启动生成；累计等待同时包含通道及路径等待。</summary>
    internal (int Limit,int ActiveRequests,int ActiveWorkflows,int Queued,long Granted,double WaitMs,double MaximumWaitMs) SchedulingDiagnostics
    {get{lock(_sync)return(_parallelism,_active.Values.Count(x=>!x.IsWorkflow),_active.Values.Count(x=>x.IsWorkflow),_pending.Count,_granted,
        (double)_waitTicks/System.Diagnostics.Stopwatch.Frequency*1000,(double)_maximumWaitTicks/System.Diagnostics.Stopwatch.Frequency*1000);}}
    internal static MountQueueBinding? CurrentBinding=>Ambient.Value?.Binding;
    internal static MountingTask? CurrentTask=>Ambient.Value?.Task;
    private static TaskCompletionSource CompletedSignal(){var value=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);value.SetResult();return value;}
    /// <summary>建立新的连接代次；旧代次必须已排空，不能以同根重连绕过正在读取的正文。</summary>
    internal MountQueueBinding StartMount(Guid mount)
    {
        lock(_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_stopping)throw new OperationCanceledException("任务服务正在停止。",new CancellationToken(true));
            if(_mountStops.ContainsKey(mount)||_active.Values.Any(x=>x.Task.MountId==mount)||_pending.Any(x=>x.Task.MountId==mount))throw new InvalidOperationException("上一连接的网络步骤尚未结束。");
            _completed.RemoveWhere(x=>x.Item1==mount);var generation=_generations.GetValueOrDefault(mount)+1;_generations[mount]=generation;_mountStops[mount]=new();return new(this,mount,generation);
        }
    }
    /// <summary>只读当前连接的停止令牌；旧绑定恒返回已取消令牌，不访问已释放的取消源。</summary>
    internal CancellationToken GenerationToken(MountQueueBinding binding)
    {lock(_sync)return _disposed||_generations.GetValueOrDefault(binding.MountId)!=binding.Generation?new CancellationToken(true):_mountStops.TryGetValue(binding.MountId,out var stop)?stop.Token:CancellationToken.None;}
    internal bool IsCurrent(MountQueueBinding binding){lock(_sync)return !_disposed&&_generations.GetValueOrDefault(binding.MountId)==binding.Generation;}
    internal IDisposable Enter(MountQueueBinding binding,MountingTask? task=null)
    {
        if(task is not null&&task.FlowId==Guid.Empty)task=task with{FlowId=Guid.NewGuid(),Channel=ChannelFor(task.Operation)};
        var previous=Ambient.Value;var current=new ScopeState(binding,task);Ambient.Value=current;
        if(task is not null)lock(_sync)
        {
            if(!IsCurrent(binding)||_stopping&&!_active.Values.Any(x=>x.IsWorkflow&&SameFlow(x.Task,task)))
            {Ambient.Value=previous;throw new OperationCanceledException("挂载代次已退役或任务服务停止接纳。",new CancellationToken(true));}
            _demandScopes.Add(current);Pump();
        }
        return new Scope(this,current,previous);
    }
    /// <summary>业务作用域只传身份及优先级，不占执行位；CFAPI父水合等待其子读取时不会形成名额互等。</summary>
    internal IDisposable EnterTask(Guid mount,string id,MountingPriority priority,string operation,string? path=null,string? entry=null,string? version=null,DateTimeOffset? submitTime=null)
    {
        MountQueueBinding binding;lock(_sync)binding=new(this,mount,_generations.GetValueOrDefault(mount));
        var parent=CurrentTask;var submitted=submitTime??(parent?.MountId==mount&&parent.Id==id?parent.SubmitTime:DateTimeOffset.UtcNow);
        var task=new MountingTask(id,mount,binding.Generation,priority,submitted.ToUniversalTime(),operation,path,entry,version);
        if(parent?.MountId==mount&&parent.Id==id)task=task with{FlowId=parent.FlowId,Channel=parent.Channel,ExclusivePath=parent.ExclusivePath,Subtree=parent.Subtree};
        return Enter(binding,task);
    }
    /// <summary>按优先级、UTC提交时间、稳定序号取得一个网络步骤；取消仅撤出尚未开始的等待，不推断写入回滚。</summary>
    internal Task<NetworkLease> AcquireAsync(MountQueueBinding binding,MountingTask task,CancellationToken token)=>AcquireCoreAsync(binding,task,false,token);
    /// <summary>完整文件工作流租约；不占全局网络额度。调用者必须在根锁外等待，并在进入作用域后调用。</summary>
    internal Task<NetworkLease> AcquireWorkflowAsync(MountQueueBinding binding,MountingTask task,CancellationToken token)=>AcquireCoreAsync(binding,task,true,token);
    internal Task<NetworkLease> AcquireCurrentWorkflowAsync(CancellationToken token,bool exclusive=false,bool subtree=false,string? destination=null)
    {
        var scope=Ambient.Value??throw new InvalidOperationException("缺少业务执行上下文。");
        var task=scope.Task??throw new InvalidOperationException("缺少业务身份。");
        return AcquireWorkflowAsync(scope.Binding,task with{ExclusivePath=exclusive||task.Channel==CloudOperationChannel.Upload,Subtree=subtree,DestinationPath=destination},token);
    }
    private async Task<NetworkLease> AcquireCoreAsync(MountQueueBinding binding,MountingTask task,bool workflow,CancellationToken token)
    {
        if(task.MountId!=binding.MountId||task.Generation!=binding.Generation||task.SubmitTime.Offset!=TimeSpan.Zero||!Enum.IsDefined(task.Priority))throw new ArgumentException("任务身份、UTC提交时间或优先级无效。");
        if(!IsCurrent(binding))throw new OperationCanceledException("挂载连接代次已经失效。",new CancellationToken(true));
        token.ThrowIfCancellationRequested();
        if(workflow)lock(_sync)
        {
            var parent=_active.Values.FirstOrDefault(x=>x.IsWorkflow&&SameFlow(x.Task,task));
            if(parent is not null)return parent.Borrow();
        }
        using var admission=CancellationTokenSource.CreateLinkedTokenSource(token,_admissionStop.Token,GenerationToken(binding));
        token=admission.Token;bool owned;
        lock(_sync)
        {
            owned=_active.Values.Any(x=>x.IsWorkflow&&SameFlow(x.Task,task));
            if(_stopping&&!owned)throw new OperationCanceledException("任务服务已停止接纳新工作流。",new CancellationToken(true));
        }
        // 已就绪工作流的请求有独立有界入口；不能被等待同通道的未来工作流填满队列后反向堵死。
        var backlog=(workflow?_workflowBacklogs:owned?_ownedBacklogs:_backlogs)[task.Priority];await backlog.WaitAsync(token).ConfigureAwait(false);
        Pending pending;
        lock(_sync)
        {
            if(!IsCurrent(binding)){backlog.Release();throw new OperationCanceledException("挂载连接代次已经失效。",new CancellationToken(true));}
            if(workflow&&_active.Values.FirstOrDefault(x=>x.IsWorkflow&&SameFlow(x.Task,task)) is {} parent)
            {backlog.Release();return parent.Borrow();}
            pending=new(task,++_sequence,backlog,workflow);_pending.Add(pending);Pump();
        }
        RaiseChanged();
        try{return await pending.Ready.Task.WaitAsync(token).ConfigureAwait(false);}
        catch
        {
            NetworkLease? granted=null;
            lock(_sync)
            {
                if(_pending.Remove(pending)){pending.Backlog.Release();Pump();}
                else if(pending.Ready.Task.IsCompletedSuccessfully)granted=pending.Ready.Task.Result;
            }
            granted?.Dispose();RaiseChanged();throw;
        }
    }
    private MountingPriority Priority(MountingTask task,HashSet<string>? seen=null)
    {
        seen??=[];if(!seen.Add(task.Id))return task.Priority;
        var result=task.Priority;
        // 共享元数据／首块由首个所有者执行；后到的同文件高优先级读取提升必要步骤，不复制请求或放宽版本。
        foreach(var scope in _demandScopes)
            if(scope.Task is {} demand&&demand.MountId==task.MountId&&demand.Generation==task.Generation&&demand.Priority>result&&
                (demand.EntryId is not null&&demand.EntryId==task.EntryId||demand.Path is not null&&demand.Path==task.Path)&&
                (demand.Version is null||task.Version is null||demand.Version==task.Version))result=demand.Priority;
        var resources=_sharedResources.Where(x=>x.Task.MountId==task.MountId&&x.Task.Id==task.Id&&x.Task.Generation==task.Generation).Select(x=>x.Resource).ToHashSet(StringComparer.Ordinal);
        foreach(var waiter in _sharedResources.Where(x=>resources.Contains(x.Resource)))if(waiter.Task.Priority>result)result=waiter.Task.Priority;
        foreach(var parent in _workflows.Values.Concat(_pending.Select(x=>x.Task)).Where(x=>x.MountId==task.MountId&&x.Dependencies.Contains(task.Id)))
            if(Priority(parent,seen) is var priority&&priority>result)result=priority;
        return result;
    }
    /// <summary>仅内存查询共享依赖提升后的优先级，供有界整文件队列调度；不读库或启动网络。</summary>
    internal MountingPriority EffectivePriority(MountingTask task){lock(_sync)return Priority(task);}
    private bool DependenciesReady(MountingTask task)=>task.Dependencies.All(id=>_completed.Contains((task.MountId,id)));
    /// <summary>只能在短调度锁内调用；名额仅交给可运行任务，冲突、依赖和退避不会占用全局执行位。</summary>
    private void Pump()
    {
        while(!_disposed)
        {
            var requests=_active.Values.Count(x=>!x.IsWorkflow);
            var ready=_pending.Where(x=>(x.Workflow||requests<_parallelism)&&DependenciesReady(x.Task)&&CanRun(x.Task,x.Workflow)).ToArray();
            if(ready.Length==0)break;
            var priority=ready.Max(x=>Priority(x.Task));
            var selected=ready.Where(x=>Priority(x.Task)==priority).GroupBy(x=>x.Task.MountId)
                .OrderBy(group=>_turns.GetValueOrDefault((priority,group.Key)))
                .ThenBy(group=>group.Min(x=>x.Sequence))
                .First().OrderBy(x=>x.Task.SubmitTime).ThenBy(x=>x.Sequence).First();
            _turns[(priority,selected.Task.MountId)]=++_turn;
            _pending.Remove(selected);selected.Backlog.Release();
            if(_active.Count==0)_drained=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var waited=System.Diagnostics.Stopwatch.GetTimestamp()-selected.Submitted;_granted++;_waitTicks+=waited;_maximumWaitTicks=Math.Max(_maximumWaitTicks,waited);
            var workflowOwner=selected.Workflow?null:_active.Values.FirstOrDefault(x=>x.IsWorkflow&&SameFlow(x.Task,selected.Task));
            var lease=new NetworkLease(this,selected.Task,selected.Sequence,selected.Workflow,workflowLease:workflowOwner?.Borrow());_active.Add(selected.Sequence,lease);selected.Ready.SetResult(lease);
        }
        if(_active.Count==0)_drained.TrySetResult();
    }
    private void Release(NetworkLease lease)
    {
        lock(_sync)
        {
            if(!_active.TryGetValue(lease.Sequence,out var active)||!ReferenceEquals(active,lease))return;
            _active.Remove(lease.Sequence);Pump();
        }
        RaiseChanged();
    }
    /// <summary>执行一个可恢复业务任务；每个步骤完成即归还名额，false重新排队让路，异常由原业务执行器保留证据。</summary>
    public async Task RunAsync(IMountingTask description,IMountingTaskStep executor,CancellationToken token=default)
    {
        var task=(description as MountingTask??new MountingTask(description.Id,description.MountId,description.Generation,description.Priority,description.SubmitTime,description.Operation,description.Path,description.EntryId,description.Version)) with{Dependencies=description.Dependencies.ToArray()};
        if(task.FlowId==Guid.Empty)task=task with{FlowId=Guid.NewGuid(),Channel=ChannelFor(task.Operation)};
        using var request=CancellationTokenSource.CreateLinkedTokenSource(token);token=request.Token;
        using var cancelOwner=RegisterCancellation(task.Id,()=>{if(!request.IsCancellationRequested)_=request.CancelAsync();return true;});
        MountQueueBinding binding;lock(_sync)
        {
            if(_stopping)throw new OperationCanceledException("任务服务正在停止。",new CancellationToken(true));
            if(task.Dependencies.Contains(task.Id)||WouldCycle(task,task.Id,[]))throw new ArgumentException("任务依赖不能形成环。");
            if(!_workflows.TryAdd((task.MountId,task.Id),task))throw new InvalidOperationException("任务已经提交。");
            _completed.Remove((task.MountId,task.Id));binding=new(this,task.MountId,task.Generation);
        }
        try
        {
            while(true)
            {
                token.ThrowIfCancellationRequested();var lease=await AcquireWorkflowAsync(binding,task,token).ConfigureAwait(false);var complete=false;
                using var scope=Enter(binding,task);
                try{complete=await executor.ExecuteStepAsync(token).ConfigureAwait(false);if(complete)lock(_sync)_completed.Add((task.MountId,task.Id));}
                finally{lease.Dispose();await lease.Finished.Task.ConfigureAwait(false);}
                if(complete)break;
            }
        }
        finally{lock(_sync)_workflows.Remove((task.MountId,task.Id));RaiseChanged();}
    }
    private bool WouldCycle(MountingTask task,string target,HashSet<string> visited)
    {
        foreach(var dependency in task.Dependencies)
            if(dependency==target||visited.Add(dependency)&&_workflows.TryGetValue((task.MountId,dependency),out var next)&&WouldCycle(next,target,visited))return true;
        return false;
    }
    /// <summary>观察器由服务登记并取消；观察本身不占网络位，闭包中产生的存储请求仍走同一传输入口。</summary>
    internal Task RunObserverAsync(IMountingObserver observer,CancellationToken token)
    {
        CancellationTokenSource stop;TaskCompletionSource finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(_sync){ObjectDisposedException.ThrowIf(_disposed,this);stop=CancellationTokenSource.CreateLinkedTokenSource(token,_observerStop.Token);_observers.Add(finished.Task);}
        return Task.Run(async()=>
        {
            try{Ambient.Value=null;await observer.ObserveAsync(stop.Token).ConfigureAwait(false);}
            finally{stop.Dispose();lock(_sync)_observers.Remove(finished.Task);finished.TrySetResult();}
        });
    }
    public IReadOnlyList<MountingQueueSnapshot> QueueSnapshots
    {
        get
        {
            lock(_sync)
            {
                var network=_active.Values.Select(x=>new MountingQueueSnapshot(x.Task,Priority(x.Task),x.IsWorkflow?"文件工作流":"网络步骤",null,null,x.Sequence){Workflow=x.IsWorkflow})
                    .Concat(_pending.Select(x=>
                    {
                        var blocker=x.Task.Dependencies.FirstOrDefault(id=>!_completed.Contains((x.Task.MountId,id)));
                        var current=_active.Values.FirstOrDefault(lease=>Blocks(lease,x.Task,x.Workflow));
                        return new MountingQueueSnapshot(x.Task,Priority(x.Task),"等待执行位",blocker is not null?"等待必要依赖："+blocker:current is not null?"等待通道或路径："+current.Task.Id:"等待全局网络执行位",blocker??current?.Task.Id,x.Sequence){Workflow=x.Workflow};
                    })).ToArray();
                var represented=network.Select(x=>x.Task.Id).ToHashSet(StringComparer.Ordinal);
                return network.Concat(_demandScopes.Select(x=>x.Task).OfType<MountingTask>().Where(x=>!represented.Contains(x.Id)).DistinctBy(x=>x.Id)
                    .Select(x=>new MountingQueueSnapshot(x,Priority(x),"业务处理／安全等待",null,null,0))).ToArray();
            }
        }
    }
    /// <summary>停止接纳新业务和观察器；已接纳的事务仍可完成证明请求，实际传输由各业务停止令牌控制。</summary>
    public void RequestStop(){lock(_sync){if(_stopping)return;_stopping=true;}_= _observerStop.CancelAsync();RaiseChanged();}
    internal async Task RetireMountAsync(MountQueueBinding binding)
    {
        Task[] active;CancellationTokenSource? stop=null;
        lock(_sync)
        {
            if(_generations.GetValueOrDefault(binding.MountId)==binding.Generation)
            {
                _generations[binding.MountId]=binding.Generation+1;_mountStops.Remove(binding.MountId,out stop);
                foreach(var pending in _pending.Where(x=>x.Task.MountId==binding.MountId&&x.Task.Generation==binding.Generation).ToArray())
                {_pending.Remove(pending);pending.Backlog.Release();pending.Ready.TrySetCanceled(new CancellationToken(true));}
            }
            active=_active.Values.Where(x=>x.Task.MountId==binding.MountId&&x.Task.Generation==binding.Generation).Select(x=>x.Finished.Task).ToArray();Pump();
        }
        try{if(stop is not null)await stop.CancelAsync().ConfigureAwait(false);await Task.WhenAll(active).ConfigureAwait(false);}
        finally{stop?.Dispose();RaiseChanged();}
    }
    private void RaiseChanged(){if(Changed is {} handlers)foreach(Action handler in handlers.GetInvocationList())try{handler();}catch{/* UI诊断由订阅者负责，不能占用或破坏传输名额。 */}}
    public ValueTask DisposeAsync(){lock(_sync)return new(_dispose??=DisposeCoreAsync());}
    private async Task DisposeCoreAsync()
    {
        RequestStop();await _admissionStop.CancelAsync().ConfigureAwait(false);Task[] observers;Task active;CancellationTokenSource[] mounts;
        lock(_sync)
        {
            _disposed=true;foreach(var pending in _pending){pending.Backlog.Release();pending.Ready.TrySetCanceled(new CancellationToken(true));}_pending.Clear();
            observers=_observers.ToArray();active=_drained.Task;mounts=_mountStops.Values.ToArray();_mountStops.Clear();
        }
        await Task.WhenAll(mounts.Select(source=>source.CancelAsync())).ConfigureAwait(false);
        await Task.WhenAll(observers).ConfigureAwait(false);await active.ConfigureAwait(false);foreach(var source in mounts)source.Dispose();
        if(_backend is {} backend)backend.Changed-=BackendChanged;_observerStop.Dispose();
        // 不释放可能仍有取消续体归还的backlog；它不创建WaitHandle且随服务一起回收。
    }
    /// <summary>文件工作流或实际响应正文的所有权；借用仅针对同工作流，不借用全局网络位。</summary>
    internal sealed class NetworkLease:IDisposable
    {
        internal TasksQueueService Owner {get;}
        internal MountingTask Task {get;}
        internal long Sequence {get;}
        internal bool IsWorkflow {get;}
        internal CancellationToken StopToken {get;}
        internal TaskCompletionSource Finished {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly NetworkLease? _parent,_workflowLease;
        private readonly object _gate=new();
        private int _references=1,_released;
        internal NetworkLease(TasksQueueService owner,MountingTask task,long sequence,bool workflow,NetworkLease? parent=null,NetworkLease? workflowLease=null)
        {Owner=owner;Task=task;Sequence=sequence;IsWorkflow=workflow;_parent=parent;_workflowLease=workflowLease;StopToken=parent?.StopToken??owner.GenerationToken(new(owner,task.MountId,task.Generation));}
        /// <summary>返回及交付正文前重新核验连接代次；旧请求的字节不得发布到新连接。</summary>
        internal void Validate()
        {if(!Owner.IsCurrent(new(Owner,Task.MountId,Task.Generation)))throw new OperationCanceledException("挂载连接代次已退役。",new CancellationToken(true));}
        internal NetworkLease Borrow()
        {
            lock(_gate)
            {
                if(!IsWorkflow||_references==0)throw new OperationCanceledException("业务工作流已经结束。",new CancellationToken(true));
                _references++;return new(Owner,Task,Sequence,true,this);
            }
        }
        private void ChildFinished(){bool finished;lock(_gate)finished=--_references==0;if(finished)Finish();}
        private void Finish(){Owner.Release(this);_workflowLease?.Dispose();Finished.TrySetResult();}
        public void Dispose()
        {
            bool finished;lock(_gate){if(_released!=0)return;_released=1;finished=--_references==0;}
            if(_parent is not null){_parent.ChildFinished();Finished.TrySetResult();}
            else if(finished)Finish();
        }
    }
}
