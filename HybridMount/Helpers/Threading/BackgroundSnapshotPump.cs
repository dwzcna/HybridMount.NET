namespace HybridMount.Helpers.Threading;

/// <summary>单一后台快照读取者；250ms合并状态变化，UI Dispatcher只发布已整理结果，释放或更换查询后丢弃迟到展示。</summary>
internal sealed class BackgroundSnapshotPump<TQuery,TValue>:IDisposable
{
    private readonly object _gate=new();
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<TQuery,TValue> _read;
    private readonly Action<TValue> _publish;
    private readonly Action<Exception> _failure;
    private readonly CancellationTokenSource _stop=new();
    private TQuery _query=default!;
    private long _version,_delivered,_lastStarted;
    private bool _running,_closed;
    private Task _work=Task.CompletedTask;
    internal BackgroundSnapshotPump(IUiDispatcher dispatcher,Func<TQuery,TValue> read,Action<TValue> publish,Action<Exception> failure)
    {_dispatcher=dispatcher;_read=read;_publish=publish;_failure=failure;}
    /// <summary>调用方提交不可变查询快照；后台变化可从任意线程请求，同一查询期间不会因持续进度而饿死UI发布。</summary>
    internal void Request(TQuery query)
    {lock(_gate){if(_closed)return;_query=query;++_version;if(_running)return;_running=true;_work=Task.Run(RunAsync);}}
    internal Task WhenIdle {get{lock(_gate)return _work;}}
    private async Task RunAsync()
    {
        while(true)
        {
            TQuery query;long version;
            try
            {
                var elapsed=System.Diagnostics.Stopwatch.GetElapsedTime(_lastStarted).TotalMilliseconds;
                if(_lastStarted!=0&&elapsed<250)await Task.Delay(TimeSpan.FromMilliseconds(250-elapsed),_stop.Token).ConfigureAwait(false);
                lock(_gate){if(_closed){_running=false;return;}query=_query;version=_version;_lastStarted=System.Diagnostics.Stopwatch.GetTimestamp();}
                TValue value=default!;Exception? failure=null;
                try{value=_read(query);}catch(Exception error){failure=error;}
                _dispatcher.TryEnqueue(()=>
                {
                    lock(_gate){if(_closed||version<_delivered||!EqualityComparer<TQuery>.Default.Equals(query,_query))return;_delivered=version;}
                    if(failure is not null)_failure(failure);else _publish(value);
                });
                lock(_gate){if(_closed||version==_version){_running=false;return;}}
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){lock(_gate)_running=false;return;}
            catch(Exception error){lock(_gate)_running=false;System.Diagnostics.Trace.TraceError("Snapshot dispatch: 0x{0:X8}",error.HResult);return;}
        }
    }
    public void Dispose(){lock(_gate){if(_closed)return;_closed=true;}_stop.Cancel();}
}
