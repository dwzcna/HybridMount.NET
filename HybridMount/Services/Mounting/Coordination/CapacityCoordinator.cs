using System.Collections.Concurrent;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>同远端写范围的统计与上传共用串行租约；跨挂载不能重复消费同一空间，网络期间不持根锁。</summary>
internal sealed class CapacityCoordinator : IDisposable
{
    internal sealed class Scope
    {
        internal readonly SemaphoreSlim Gate=new(1,1);
        internal long Revision,CachedRevision;
        internal readonly Dictionary<string,(RemoteCapacity Value,DateTimeOffset VerifiedUtc)> Values=[];
        internal readonly Dictionary<string,DateTimeOffset> Failures=[];
    }
    private readonly ConcurrentDictionary<string,Scope> _scopes=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _queries=new(2,2);
    private readonly Func<DateTimeOffset> _now;
    public CapacityCoordinator(Func<DateTimeOffset>? now=null)=>_now=now??(()=>DateTimeOffset.UtcNow);
    public async Task<Lease> EnterAsync(string scope,CancellationToken token)
    {
        var state=_scopes.GetOrAdd(scope,_=>new());await state.Gate.WaitAsync(token).ConfigureAwait(false);return new(this,state);
    }
    /// <summary>协议拒绝通知只递增代次；字典仍由持有租约者清理，不在回调中等待网络租约。</summary>
    public void Invalidate(string scope){if(_scopes.TryGetValue(scope,out var state))Interlocked.Increment(ref state.Revision);}
    /// <summary>租约内的预留保持到请求回执或未知阶段落库；重复释放只归还一次。</summary>
    internal sealed class Lease(CapacityCoordinator owner,Scope state) : IDisposable
    {
        private Scope? _state=state;
        private Scope Current()
        {
            var current=_state??throw new ObjectDisposedException(nameof(Lease));var revision=Volatile.Read(ref current.Revision);
            if(current.CachedRevision!=revision){current.Values.Clear();current.Failures.Clear();current.CachedRevision=revision;}return current;
        }
        public async Task<RemoteCapacity> ReadAsync(string key,Func<CancellationToken,Task<RemoteCapacity>> query,bool force,CancellationToken token)
        {
            var current=Current();
            if(!force&&current.Values.TryGetValue(key,out var cached)&&owner._now()-cached.VerifiedUtc<TimeSpan.FromMinutes(5))return cached.Value;
            if(!force&&current.Failures.TryGetValue(key,out var failed)&&owner._now()-failed<TimeSpan.FromMinutes(1))throw new IOException("容量尚待重新核实，将在一分钟后重试。");
            var revision=Volatile.Read(ref current.Revision);
            await owner._queries.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var value=await query(token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                if(revision==Volatile.Read(ref current.Revision)){current.Values[key]=(value,owner._now());current.Failures.Remove(key);}return value;
            }
            catch{current.Values.Remove(key);current.Failures[key]=owner._now();throw;}
            finally{owner._queries.Release();}
        }
        /// <summary>回执到达只调整当前完整统计，其他配置范围失效；核实时间不因上传延长。</summary>
        public void Committed(string? key,long delta)
        {
            var current=Current();
            var cached=key is not null?current.Values.GetValueOrDefault(key):default;current.Values.Clear();current.Failures.Clear();
            if(key is null||cached.Value is null)return;
            try
            {
                var value=cached.Value;long? used=value.UsedBytes is {} prior?Math.Max(0,checked(prior+delta)):null;
                long? free=value.ConfiguredTotalBytes is {} total&&used is {} amount?Math.Max(0,total-amount):value.AvailableBytes is {} oldFree?Math.Max(0,checked(oldFree-delta)):null;
                current.Values[key]=(value with{UsedBytes=used,AvailableBytes=free},cached.VerifiedUtc);
            }
            catch(OverflowException){ }
        }
        public RemoteCapacitySnapshot? Snapshot(string? key)=>key is not null&&Current().Values.TryGetValue(key,out var cached)?new(cached.Value,cached.VerifiedUtc,cached.VerifiedUtc):null;
        public void Invalidate(){var current=Current();current.CachedRevision=Interlocked.Increment(ref current.Revision);current.Values.Clear();current.Failures.Clear();}
        public void Dispose()=>Interlocked.Exchange(ref _state,null)?.Gate.Release();
    }
    public void Dispose(){foreach(var scope in _scopes.Values)scope.Gate.Dispose();_queries.Dispose();}
}
