namespace HybridMount.Helpers.Threading;

/// <summary>仅合并重叠中的同键工作；每位等待者独立取消，最后一位离开或宿主停止时取消底层工作。</summary>
/// <remarks>不缓存已完成的结果。工作使用自己的令牌，不能捕获首位调用者的令牌或借用其资源。</remarks>
public sealed class SharedAsyncWork<TKey,TValue> : IAsyncDisposable where TKey : notnull
{
    private sealed class Job
    {
        public readonly CancellationTokenSource Cancel=new();
        public Task<TValue> Work=null!;
        public int Waiters;
    }
    private readonly object _gate=new();
    private readonly Dictionary<TKey,Job> _jobs=new();
    private readonly HashSet<Job> _running=[];
    private Task? _shutdown;
    private bool _stopping;

    /// <summary>领取共享结果；resource 的所有权总是转入此方法，合并时立即释放，否则在工作结束后释放。</summary>
    public async Task<TValue> RunAsync(TKey key,Func<CancellationToken,Task<TValue>> factory,CancellationToken token,IDisposable? resource=null)
    {
        Job job;
        try
        {
            lock(_gate)
            {
                token.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(_stopping,this);
                if(!_jobs.TryGetValue(key,out job!))
                {
                    job=new();_jobs.Add(key,job);_running.Add(job);
                    var owned=resource;resource=null;
                    var captured=job;
                    job.Work=Task.Run(async()=>
                    {
                        try { return await factory(captured.Cancel.Token).ConfigureAwait(false); }
                        finally
                        {
                            owned?.Dispose();
                            lock(_gate)
                            {
                                if(_jobs.TryGetValue(key,out var current)&&ReferenceEquals(current,captured))_jobs.Remove(key);
                                _running.Remove(captured);
                            }
                        }
                    });
                    // 没有剩余等待者时也观察底层失败，避免未观察的任务异常。
                    _=job.Work.ContinueWith(work=>{_ = work.Exception;captured.Cancel.Dispose();},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                }
                job.Waiters++;
            }
        }
        finally { resource?.Dispose(); }
        try { return await job.Work.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            var cancel=false;
            lock(_gate)
            {
                if(--job.Waiters==0 && !job.Work.IsCompleted)
                {
                    if(_jobs.TryGetValue(key,out var current)&&ReferenceEquals(current,job))_jobs.Remove(key);
                    cancel=true;
                }
            }
            if(cancel)try{await job.Cancel.CancelAsync().ConfigureAwait(false);}catch(ObjectDisposedException){}
        }
    }
    /// <summary>先禁止加入、取消所有工作，再等待资源归还；重复释放共享一次完成任务。</summary>
    public ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            if(_shutdown is not null)return new(_shutdown);
            _stopping=true;
            return new(_shutdown=StopAsync(_running.ToArray()));
        }
    }
    private static async Task StopAsync(Job[] jobs)
    {
        // 先对全部工作发取消，再统一等候，避免首个取消处理阻挡其余请求收到停止。
        var cancellations=jobs.Select(async job=>{try{await job.Cancel.CancelAsync().ConfigureAwait(false);}catch(ObjectDisposedException){}}).ToArray();
        await Task.WhenAll(cancellations).ConfigureAwait(false);
        try{await Task.WhenAll(jobs.Select(job=>job.Work)).ConfigureAwait(false);}catch(Exception){/* 失败由调用者报告，停止只负责资源归还。 */}
    }
}
