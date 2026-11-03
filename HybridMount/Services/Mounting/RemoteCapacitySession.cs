using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>一个连接代次内的只读配额缓存；查询合并，等待者取消不影响共享请求，断开先取消再等候。</summary>
internal sealed class RemoteCapacitySession : IAsyncDisposable
{
    private readonly object _sync=new();
    private readonly IFileSource _source;
    private readonly Action<RemoteCapacitySnapshot> _publish;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _stop;
    private RemoteCapacitySnapshot _snapshot;
    private Task _work=Task.CompletedTask,_cancellation=Task.CompletedTask;
    private Task? _dispose;
    private bool _stopping;
    public RemoteCapacitySession(IFileSource source,Action<RemoteCapacitySnapshot> publish,CancellationToken token,
        RemoteCapacitySnapshot? previous=null,Func<DateTimeOffset>? now=null)
    {_source=source;_publish=publish;_stop=CancellationTokenSource.CreateLinkedTokenSource(token);_snapshot=previous??new(RemoteCapacity.Unknown);_now=now??(()=>DateTimeOffset.UtcNow);}
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
                work=_work=Task.Run(RefreshCoreAsync);
            }
        }
        return waiter.CanBeCanceled?work.WaitAsync(waiter):work;
    }
    private async Task RefreshCoreAsync()
    {
        try
        {
            RemoteCapacitySnapshot current;lock(_sync){if(_stopping)return;current=_snapshot;}
            _publish(current);
            var value=await _source.GetRemoteCapacityAsync(_stop.Token).ConfigureAwait(false);
            lock(_sync){if(_stopping||_stop.IsCancellationRequested)return;current=_snapshot=new(value,_now(),_snapshot.LastAttemptUtc);}
            _publish(current);
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){ }
        catch(Exception error)
        {
            RemoteCapacitySnapshot current;
            lock(_sync)
            {
                if(_stopping||_stop.IsCancellationRequested)return;
                var message=error is SourceRequestRejectedException rejected?$"远端配额查询被拒绝（HTTP {rejected.HttpStatus}），保留上次值。":
                    $"远端配额查询失败（{error.GetType().Name}），保留上次值。";
                current=_snapshot=_snapshot with{IsRefreshing=false,Error=message};
            }
            _publish(current);
        }
    }
    /// <summary>不持根锁发停止信号；重复停止与释放只取消一次。</summary>
    public void RequestStop(){lock(_sync){if(_stopping)return;_stopping=true;_cancellation=_stop.CancelAsync();}}
    public ValueTask DisposeAsync(){lock(_sync){RequestStop();return new(_dispose??=StopCoreAsync());}}
    private async Task StopCoreAsync(){await Task.WhenAll(_cancellation,_work).ConfigureAwait(false);_stop.Dispose();}
}
