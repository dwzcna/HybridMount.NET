namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private int _runtimeReconnecting;
    /// <summary>只读当前故障标记，不启动原生进程或恢复用户已经断开的挂载。</summary>
    public bool HasRuntimeFailedMounts=>_roots.Values.Any(root=>{lock(root.ConnectionSync)return root.RuntimeFailed&&!root.RemovalRequested&&!root.Definition.RemovalPending;});
    /// <summary>应用拥有的串行批量重连；逐根等待旧收尾并重新核验，单根失败继续，重复提交合并。</summary>
    public Task ReconnectRuntimeFailedAsync(CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        if(Interlocked.Exchange(ref _runtimeReconnecting,1)!=0)return;
        try
        {
            foreach(var root in _roots.Values.ToArray())
            {
                cancel.ThrowIfCancellationRequested();Task cleanup;
                lock(root.ConnectionSync)
                {
                    if(!root.RuntimeFailed||root.RemovalRequested||root.Definition.RemovalPending||root.Connecting is not null)continue;
                    cleanup=root.RuntimeFailureCleanup;
                }
                try
                {
                    await cleanup.WaitAsync(cancel).ConfigureAwait(false);
                    lock(root.ConnectionSync)
                        if(!root.RuntimeFailed||root.RemovalRequested||root.Definition.RemovalPending||root.Connecting is not null||root.ConnectionStops>0||root.Session is not null)continue;
                    await ConnectAsync(root.Definition.Id,cancel,runtimeFailedOnly:true).ConfigureAwait(false);
                }
                catch(OperationCanceledException)when(cancel.IsCancellationRequested){throw;}
                catch(Exception error){_logger.QueueException("原生后台","Warning","重新连接挂载未完成",error,root.Definition.Id.ToString("N"));}
            }
        }
        finally{Interlocked.Exchange(ref _runtimeReconnecting,0);Notify();}
    },token);
}
