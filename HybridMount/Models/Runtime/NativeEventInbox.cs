using System.Runtime.CompilerServices;
namespace HybridMount.Models.Runtime;

/// <summary>内存有界事件邮箱：活动快照按代次和条目合并，关键事件独立保留容量并阻断跨事件合并。</summary>
internal sealed class NativeEventInbox
{
    private sealed record Item(NativeRuntimeEvent Event,string Key,int Bytes,bool State);
    private readonly object _gate=new();private readonly LinkedList<Item> _items=[];
    private readonly Dictionary<string,LinkedListNode<Item>> _states=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ready=new(0);private int _stateBytes,_criticalBytes,_critical,_stateCount;private bool _closed;
    private long _accepted,_merged,_rejected,_delivered;
    private static long Sequence(NativeRuntimeEvent value)=>NativeRuntimeProtocol.Read<long>(value.Data.GetProperty("sequence"));
    /// <summary>读循环只做内存操作，不等待消费者；满或关闭返回false，由运行时显式失效。</summary>
    internal bool TryWrite(NativeRuntimeEvent value)
    {
        var key=value.SessionId+":"+(value.Data.TryGetProperty("entryId",out var entry)?entry.GetString():"");var size=value.Data.GetRawText().Length*2+key.Length*2+256;var state=value.Event=="activity";
        lock(_gate)
        {
            if(_closed){_rejected++;return false;}
            if(state&&_states.TryGetValue(key,out var prior))
            {
                if(Sequence(value)<Sequence(prior.Value.Event)){_merged++;return true;}
                if(_stateBytes-prior.Value.Bytes+size>1024*1024){_rejected++;return false;}
                _stateBytes=_stateBytes-prior.Value.Bytes+size;prior.Value=new(value,key,size,true);_merged++;return true;
            }
            if(state?(_stateCount>=128||_stateBytes+size>1024*1024):(_critical>=256||_criticalBytes+size>4*1024*1024)){_rejected++;return false;}
            if(!state){if(key.EndsWith(':'))_states.Clear();else _states.Remove(key);}
            var node=_items.AddLast(new Item(value,key,size,state));if(state){_states[key]=node;_stateCount++;}else _critical++;
            if(state)_stateBytes+=size;else _criticalBytes+=size;_accepted++;_ready.Release();return true;
        }
    }
    internal async IAsyncEnumerable<NativeRuntimeEvent> ReadAllAsync([EnumeratorCancellation]CancellationToken token)
    {
        while(true)
        {
            await _ready.WaitAsync(token).ConfigureAwait(false);NativeRuntimeEvent? value=null;
            lock(_gate)
            {
                if(_items.First is {} node){var item=node.Value;_items.RemoveFirst();if(item.State)_stateBytes-=item.Bytes;else _criticalBytes-=item.Bytes;if(item.State){_stateCount--;if(_states.TryGetValue(item.Key,out var prior)&&ReferenceEquals(prior,node))_states.Remove(item.Key);}else _critical--;_delivered++;value=item.Event;}
                else if(_closed)yield break;
            }
            if(value is not null)yield return value;
        }
    }
    internal void Complete(){lock(_gate){if(_closed)return;_closed=true;_ready.Release();}}
    /// <summary>只读固定计数，无业务操作、数据库或远端访问。</summary>
    internal string Statistics{get{lock(_gate)return $"待处理={_items.Count}；关键={_critical}；状态={_stateCount}；接收={_accepted}；合并={_merged}；拒绝={_rejected}；交付={_delivered}";}}
}
