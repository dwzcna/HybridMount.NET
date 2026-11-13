namespace HybridMount.Models.Mounting.Windows;

public sealed partial class CloudMountSession
{
    private Task _nativeNotificationTail=Task.CompletedTask;
    private int _nativeNotificationCount;
    private readonly Dictionary<string,object> _pendingCloseNotifications=new(StringComparer.Ordinal);

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

    /// <summary>最多256项；同条目待执行关闭合并，关键事件封闭合并段，保持改名／删除因果。停止后拒绝新项并排空已接纳工作。</summary>
    private void QueueNativeNotification(Action action,string? closeKey=null)
    {
        lock(_fetchGate)
        {
            if(_clearing||_stop.IsCancellationRequested)return;
            if(closeKey is not null&&_pendingCloseNotifications.ContainsKey(closeKey))return;
            if(_nativeNotificationCount>=256)
            {
                _runtime?.AbortForSafety(new IOException("本地变更通知积压，保留记录等待核对。"));
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
                    lock(_fetchGate)
                        if(closeKey is not null&&_pendingCloseNotifications.TryGetValue(closeKey,out var pending)&&ReferenceEquals(pending,marker))
                            _pendingCloseNotifications.Remove(closeKey);
                    action();
                }
                catch(Exception error)
                {
                    _logger.QueueException("原生通知","Error","本地变更通知未完成",error);
                    _runtime?.AbortForSafety(error);
                }
                finally{lock(_fetchGate)_nativeNotificationCount--;}
            });
            _nativeNotificationTail=work;
            _notifications.Add(work);
            _=work.ContinueWith(done=>{lock(_fetchGate)_notifications.Remove(done);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
    }
}
