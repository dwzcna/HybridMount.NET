namespace HybridMount.Services.Mounting;

/// <summary>有界读取并发；显式水合优先，其次采用 CFAPI 提示，每八次调度让最早请求先行，避免普通读取饥饿。</summary>
public sealed class PriorityReadQueue(int capacity=4)
{
    private sealed class Waiter(int priority,bool aging)
    {
        public readonly int Priority=priority;
        public readonly bool Aging=aging;
        public readonly TaskCompletionSource<IDisposable> Ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Lease(PriorityReadQueue owner) : IDisposable
    {
        private PriorityReadQueue? _owner=owner;
        public void Dispose()=>Interlocked.Exchange(ref _owner,null)?.Release();
    }
    private readonly object _gate=new();
    private readonly List<Waiter> _waiting=[];
    private int _active,_grants;
    /// <summary>只影响尚未执行的工作，不抢占已在传输的请求；取消等待不占用并发槽。</summary>
    public Task<IDisposable> EnterAsync(byte priority,bool explicitHydration,CancellationToken token)=>EnterCoreAsync(Math.Min(15,(int)priority)+(explicitHydration?16:0),true,token);
    /// <summary>统一队列使用的分块缓冲名额；严格优先级且无老化，只约束内存，不占网络执行位。</summary>
    internal Task<IDisposable> EnterStrictAsync(Models.Mounting.MountingPriority priority,CancellationToken token)=>EnterCoreAsync((int)priority,false,token);
    private async Task<IDisposable> EnterCoreAsync(int priority,bool aging,CancellationToken token)
    {
        Waiter waiter;
        lock(_gate)
        {
            token.ThrowIfCancellationRequested();
            if(_active<capacity){_active++;return new Lease(this);}
            waiter=new(priority,aging);_waiting.Add(waiter);
        }
        using var registration=token.Register(()=>
        {
            lock(_gate)if(_waiting.Remove(waiter))waiter.Ready.TrySetCanceled(token);
        });
        return await waiter.Ready.Task.ConfigureAwait(false);
    }
    private void Release()
    {
        lock(_gate)
        {
            if(_waiting.Count==0){_active--;return;}
            var next=_waiting.All(x=>x.Aging)&&++_grants%8==0?_waiting[0]:_waiting.MaxBy(item=>item.Priority)!;
            _waiting.Remove(next);next.Ready.SetResult(new Lease(this));
        }
    }
}
