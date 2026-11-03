using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly object _lifetime=new();
    private readonly HashSet<Task> _foreground=[];
    private int _stopping;
    private Task _cancellation=Task.CompletedTask;
    /// <summary>停止一经开始不再接纳连接、文件任务或预览；可在令牌释放后安全查询。</summary>
    public bool IsStopping=>Volatile.Read(ref _stopping)!=0;
    internal Action<string,Exception?>? StopObserver {get;set;}
    /// <summary>仅在连接所有者的令牌已取消时产生，HTTP 自身超时不能成为此类型。</summary>
    internal sealed class ConnectionStoppedException(Exception error,CancellationToken token):OperationCanceledException("挂载连接已停止。",error,token);
    private void ThrowIfStopping(){if(IsStopping)throw new OperationCanceledException("程序正在停止。",new CancellationToken(true));}
    /// <summary>先登记再执行，finally 归还所有权；停止快照包含手动操作，不依赖页面继续存在。</summary>
    private Task<T> OwnAsync<T>(Func<CancellationToken,Task<T>> action,CancellationToken token)
    {
        CancellationTokenSource cancel;TaskCompletionSource completed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(_lifetime){ThrowIfStopping();token.ThrowIfCancellationRequested();cancel=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);_foreground.Add(completed.Task);}
        return RunAsync();
        async Task<T> RunAsync()
        {
            try{cancel.Token.ThrowIfCancellationRequested();return await action(cancel.Token).ConfigureAwait(false);}
            finally{cancel.Dispose();lock(_lifetime){_foreground.Remove(completed.Task);completed.TrySetResult();}}
        }
    }
    /// <summary>表单只读探测同样由应用拥有；退出取消并等待，不保存配置或执行远端写探测。</summary>
    public Task<ConnectionTestResult> TestConnectionAsync(FileSourceConfiguration settings,SourceCredentials credentials,CancellationToken token=default,TimeSpan? timeout=null)
        =>OwnAsync(cancel=>(_factory as FileSourceFactory??new FileSourceFactory()).TestConnectionAsync(settings,credentials,cancel,timeout),token);
    private Task OwnAsync(Func<CancellationToken,Task> action,CancellationToken token)
        =>OwnAsync(async cancel=>{await action(cancel).ConfigureAwait(false);return true;},token);
    /// <summary>先向所有生产者发停止信号，不持根锁等待；重复请求不重复取消已释放的资源。</summary>
    public void RequestStop()
    {
        lock(_lifetime)
        {
            if(IsStopping)return;Volatile.Write(ref _stopping,1);
            var signals=new List<Task>{_stop.CancelAsync(),_previews.RequestStop()};
            foreach(var root in _roots.Values)
            {
                CancelConnection(root,reason:"Shutdown");RequestRemoteStop(root);RequestUploadStop(root);
                lock(root.ConnectionSync){signals.Add(root.ConnectionSignal);if(root.Uploads is {} uploads)signals.Add(uploads.Cancellation);}
                if(root.Session is {} session)signals.Add(session.RequestStop());
            }
            _cancellation=Task.WhenAll(signals);ObserveStop("StopSignaled",null);
        }
    }
    private void ObserveStop(string phase,Exception? error)
    {
        try{StopObserver?.Invoke(phase,error);}catch(Exception diagnostic){_logger.Queue("退出诊断","Warning",diagnostic.GetType().Name);}
    }
    /// <summary>并发及重复释放共用一次收尾；真实清理失败逐阶段记录，其他资源仍继续释放。</summary>
    public ValueTask DisposeAsync(){lock(_lifetime){RequestStop();return new(_dispose??=StopCoreAsync());}}
    private async Task StopCoreAsync()
    {
        var errors=new List<Exception>();
        async Task Step(string phase,Func<Task> work)
        {
            ObserveStop(phase+"Starting",null);
            try{await work().ConfigureAwait(false);ObserveStop(phase+"Completed",null);}
            catch(Exception error){errors.Add(error);ObserveStop(phase+"Failed",error);_logger.Queue("退出","Error",phase+"："+error.GetType().Name);}
        }
        await Step("Cancellation",()=>_cancellation).ConfigureAwait(false);
        if(_startup is not null)await Step("Startup",()=>_startup).ConfigureAwait(false);
        Task[] foreground;lock(_lifetime)foreground=_foreground.ToArray();
        await Step("Foreground",()=>Task.WhenAll(foreground)).ConfigureAwait(false);
        await Step("Jobs",()=>Task.WhenAll(_jobs.Values.Select(job=>job.Work))).ConfigureAwait(false);
        if(_maintenance is not null)await Step("Maintenance",()=>_maintenance).ConfigureAwait(false);
        await Step("RemoteSync",()=>Task.WhenAll(_roots.Values.Select(StopRemoteAsync))).ConfigureAwait(false);
        await Step("Uploads",()=>Task.WhenAll(_roots.Values.Select(StopUploadsAsync))).ConfigureAwait(false);
        await Step("Previews",()=>_previews.DisposeAsync().AsTask()).ConfigureAwait(false);
        foreach(var root in _roots.Values)
        {
            await Step("Disconnect",async()=>
            {
                await root.Gate.WaitAsync().ConfigureAwait(false);
                try{await DisconnectCoreAsync(root).ConfigureAwait(false);Status(root,"未连接");}
                finally{root.Gate.Release();}
            }).ConfigureAwait(false);
        }
        foreach(var job in _jobs.Values)job.Cancel.Dispose();_remoteSlots.Dispose();_uploadSlots.Dispose();_configurationGate.Dispose();_stop.Dispose();
        ObserveStop(errors.Count==0?"Stopped":"StoppedWithErrors",null);
        if(errors.Count!=0)throw new AggregateException("挂载停止时有资源未正常释放。",errors);
    }
}
