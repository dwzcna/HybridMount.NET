using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>一个连接代次内的只读配额缓存；查询合并，等待者取消不影响共享请求，断开先取消再等候。</summary>
internal sealed class RemoteCapacitySession : IAsyncDisposable
{
    private readonly object _sync=new();
    private readonly Func<bool,CancellationToken,Task<RemoteCapacity>> _query;
    private readonly Action<RemoteCapacitySnapshot> _publish;
    private readonly Action<Exception>? _failed;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _stop;
    private RemoteCapacitySnapshot _snapshot;
    private Task _work=Task.CompletedTask,_cancellation=Task.CompletedTask;
    private Task? _dispose;
    private bool _stopping;
    private long _revision;
    public RemoteCapacitySession(IFileSource source,Action<RemoteCapacitySnapshot> publish,CancellationToken token,
        RemoteCapacitySnapshot? previous=null,Func<DateTimeOffset>? now=null,Func<bool,CancellationToken,Task<RemoteCapacity>>? query=null,Action<Exception>? failed=null)
    {_failed=failed;_query=query??((_,cancel)=>source.GetRemoteCapacityAsync(cancel));_publish=publish;_stop=CancellationTokenSource.CreateLinkedTokenSource(token);_snapshot=previous??new(RemoteCapacity.Unknown);_now=now??(()=>DateTimeOffset.UtcNow);}
    public bool IsStopping {get{lock(_sync)return _stopping;}}
    /// <summary>普通读取缓存五分钟；手动刷新和重连可强制查询，同时请求仍共用在途工作。</summary>
    public Task RefreshAsync(bool force=false,CancellationToken waiter=default)
    {
        waiter.ThrowIfCancellationRequested();Task work;
        lock(_sync)
        {
            if(_stopping||_stop.IsCancellationRequested)return Task.CompletedTask;
            if(!_work.IsCompleted)work=_work;
            else if(!force&&_snapshot.LastAttemptUtc is {} previous&&_now()-previous<TimeSpan.FromMinutes(5))return Task.CompletedTask;
            else
            {
                _snapshot=_snapshot with{LastAttemptUtc=_now(),IsRefreshing=true};
                var revision=_revision;work=_work=Task.Run(()=>RefreshCoreAsync(force,revision));
            }
        }
        return waiter.CanBeCanceled?work.WaitAsync(waiter):work;
    }
    /// <summary>已知存储拒绝使缓存过期，保留上次值供诊断；不在通知线程联网。</summary>
    public void Invalidate()
    {
        RemoteCapacitySnapshot current;lock(_sync){if(_stopping)return;_revision++;current=_snapshot=_snapshot with{LastAttemptUtc=null,IsRefreshing=false,Error="服务器报告存储不足，容量需要重新核实。"};}
        _publish(current);
    }
    private async Task RefreshCoreAsync(bool force,long revision)
    {
        try
        {
            RemoteCapacitySnapshot current;lock(_sync){if(_stopping)return;current=_snapshot;}
            _publish(current);
            var value=await _query(force,_stop.Token).ConfigureAwait(false);
            lock(_sync){if(_stopping||_stop.IsCancellationRequested||revision!=_revision)return;current=_snapshot=new(value,_now(),_snapshot.LastAttemptUtc);}
            _publish(current);
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){ }
        catch(Exception error)
        {
            RemoteCapacitySnapshot current;
            lock(_sync)
            {
                if(_stopping||_stop.IsCancellationRequested||revision!=_revision)return;
                var message=error is SourceConnectionException connection?connection.Message+" 已保留上次值。":error is SourceRequestRejectedException rejected?$"远端配额查询被拒绝（HTTP {rejected.HttpStatus}），保留上次值。":
                    $"远端配额查询失败（{error.GetType().Name}），保留上次值。";
                current=_snapshot=_snapshot with{IsRefreshing=false,Error=message};
            }
            try{_failed?.Invoke(error);}catch{ /* 诊断不得改变容量状态。 */ }
            _publish(current);
        }
    }
    /// <summary>不持根锁发停止信号；重复停止与释放只取消一次。</summary>
    public void RequestStop(){lock(_sync){if(_stopping)return;_stopping=true;_cancellation=_stop.CancelAsync();}}
    public ValueTask DisposeAsync(){lock(_sync){RequestStop();return new(_dispose??=StopCoreAsync());}}
    private async Task StopCoreAsync(){await Task.WhenAll(_cancellation,_work).ConfigureAwait(false);_stop.Dispose();}
}
