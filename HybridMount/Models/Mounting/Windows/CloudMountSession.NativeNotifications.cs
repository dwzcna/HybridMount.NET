using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Mounting.Windows;

public sealed partial class CloudMountSession
{
    private Task _nativeNotificationTail=Task.CompletedTask;
    private int _nativeNotificationCount,_nativeNotificationFaulted;
    private long _nativeNotificationRetries,_nativeNotificationRescans;
    private readonly Dictionary<string,object> _pendingCloseNotifications=new(StringComparer.Ordinal);
    private string NativeNotificationStatistics
    {
        get{lock(_fetchGate)return $"挂载通知待办={_nativeNotificationCount}；重试={Interlocked.Read(ref _nativeNotificationRetries)}；隔离={_nativeNotificationFaulted}；补扫请求={Interlocked.Read(ref _nativeNotificationRescans)}";}
    }

    /// <summary>共享事件线程只更新打开计数和入队；关闭业务、根核验和SQLite均由本挂载串行链执行。</summary>
    private void QueueClosedEntryNotification(string id)
    {
        QueueNativeNotification(()=>
        {
            if(_clearing||_stop.IsCancellationRequested)return;
            _localChanged?.Invoke();
            var entry=_repository.EntryById(_mount.Id,id);
            if(entry is null)return;
            NotifyPinWorkCompleted(entry);
            _localContentReady?.Invoke(entry);
        },id);
    }

    /// <summary>最多256项；关闭合并但不跨关键事件。溢出停止本挂载，不能丢弃后继续执行或熔断其他挂载。</summary>
    private void QueueNativeNotification(Action action,string? closeKey=null)
    {
        lock(_fetchGate)
        {
            if(_clearing||_stop.IsCancellationRequested)return;
            if(closeKey is not null&&_pendingCloseNotifications.ContainsKey(closeKey))return;
            if(_nativeNotificationCount>=256)
            {
                IsolateNativeNotifications(new IOException("本地变更通知积压，保留记录等待核对。"));
                return;
            }
            object? marker=null;
            if(closeKey is null)_pendingCloseNotifications.Clear();
            else _pendingCloseNotifications[closeKey]=marker=new object();
            var prior=_nativeNotificationTail;
            _nativeNotificationCount++;
            var work=Task.Run(async()=>
            {
                try
                {
                    await prior.ConfigureAwait(false);
                    if(Volatile.Read(ref _nativeNotificationFaulted)!=0)return;
                    lock(_fetchGate)
                        if(closeKey is not null&&_pendingCloseNotifications.TryGetValue(closeKey,out var pending)&&ReferenceEquals(pending,marker))
                            _pendingCloseNotifications.Remove(closeKey);
                    await RunNativeNotificationAsync(action).ConfigureAwait(false);
                }
                finally{lock(_fetchGate)_nativeNotificationCount--;}
            });
            _nativeNotificationTail=work;
            TrackNativeNotification(work);
        }
    }

    /// <summary>合法业务通知最多四次执行；退避不持根锁／全局云位，SQLite子树更新为事务，取消只结束重试不宣称成功。</summary>
    private async Task RunNativeNotificationAsync(Action action)
    {
        int[] delays=[100,500,2000];
        for(var attempt=0;;attempt++)
        {
            try{action();return;}
            catch(Exception error)
            {
                if(_stop.IsCancellationRequested)
                {
                    _logger.QueueException("原生通知","Warning","本地变更通知未完成",error,_mount.Id.ToString("N"));
                    return;
                }
                var transient=error is SqliteException{SqliteErrorCode:5 or 6 or 10 or 14}||error is IOException and not SourceConflictException;
                if(attempt>=delays.Length||!transient){IsolateNativeNotifications(error);return;}
                Interlocked.Increment(ref _nativeNotificationRetries);
                if(attempt==0)_logger.QueueException("原生通知","Warning","本地变更通知未完成",error,_mount.Id.ToString("N"));
                try{await Task.Delay(delays[attempt],_stop.Token).ConfigureAwait(false);}
                catch(OperationCanceledException)when(_stop.IsCancellationRequested){return;}
            }
        }
    }

    /// <summary>先同步封闭本会话接纳，再由后台停止CFAPI并通知管理器排空；共享管道与其他根保持。重连仍执行补扫与未决核验。</summary>
    private void IsolateNativeNotifications(Exception error)
    {
        lock(_fetchGate)
        {
            if(_nativeNotificationFaulted!=0)return;
            Volatile.Write(ref _nativeNotificationFaulted,1);
            _clearing=true;
            _pendingCloseNotifications.Clear();
            var work=Task.Run(async()=>
            {
                _logger.QueueException("原生通知","Error","本地变更通知未完成",error,_mount.Id.ToString("N"));
                var stopping=RequestStop();
                try{RuntimeFailed?.Invoke(error);}
                catch(Exception callback){_logger.QueueException("原生通知","Error","本地变更通知未完成",callback,_mount.Id.ToString("N"));}
                try{await stopping.ConfigureAwait(false);}
                catch(Exception cleanup){_logger.QueueException("原生通知","Error","本地变更通知未完成",cleanup,_mount.Id.ToString("N"));}
            });
            TrackNativeNotification(work);
        }
    }

    /// <summary>调用者持_fetchGate；退出统一等待所有已登记的执行及失败收尾。</summary>
    private void TrackNativeNotification(Task work)
    {
        _notifications.Add(work);
        _=work.ContinueWith(done=>{lock(_fetchGate)_notifications.Remove(done);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
}
