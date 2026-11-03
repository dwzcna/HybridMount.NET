using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>准确停在退避阶段，验证无异常唤醒、所有者取消与期限分类；不联网或接触用户挂载。</summary>
[Collection("Process exception diagnostics")]
public sealed class WebDavRetryCancellationTests
{
    private sealed class ResetHandler(bool unrelatedTimeout=false):HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            Exception error=unrelatedTimeout?new TaskCanceledException("internal HTTP timeout"):
                new HttpRequestException(HttpRequestError.SecureConnectionError,"test reset",new IOException("test",new SocketException((int)SocketError.ConnectionReset)));
            return Task.FromException<HttpResponseMessage>(error);
        }
    }
    [Fact]
    public async Task Retry_wait_stops_without_throwing_a_first_chance_cancellation()
    {
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),new WholeFileCache("unused"),new ResetHandler());
        using var stop=new CancellationTokenSource();var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.ReadRetryWaiting=()=>waiting.TrySetResult();var exceptions=0;
        EventHandler<FirstChanceExceptionEventArgs> observer=(_,args)=>{if(args.Exception is OperationCanceledException)Interlocked.Increment(ref exceptions);};
        AppDomain.CurrentDomain.FirstChanceException+=observer;
        try
        {
            var result=source.WaitReadRetryAsync(TimeSpan.FromSeconds(2),stop.Token).AsTask();await waiting.Task;
            await stop.CancelAsync();Assert.False(await result);Assert.True(result.IsCompletedSuccessfully);Assert.Equal(0,exceptions);
            Assert.False(await source.WaitReadRetryAsync(TimeSpan.FromSeconds(2),stop.Token));
        }
        finally{AppDomain.CurrentDomain.FirstChanceException-=observer;}
    }
    [Theory(Timeout=15000)]
    [InlineData("metadata",true)][InlineData("metadata",false)]
    [InlineData("discovery",true)][InlineData("discovery",false)]
    [InlineData("report",true)][InlineData("report",false)]
    [InlineData("capacity",true)][InlineData("capacity",false)]
    public async Task Backoff_caller_cancellation_and_owner_deadline_remain_distinct(string operation,bool caller)
    {
        var handler=new ResetHandler();var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),new WholeFileCache("unused"),handler)
        {ReadRetryDelay=TimeSpan.FromSeconds(2),MetadataDeadline=TimeSpan.FromMilliseconds(caller?5000:200),ReadRetryWaiting=()=>waiting.TrySetResult()};
        using var stop=new CancellationTokenSource();var watch=Stopwatch.StartNew();
        Task work=operation switch
        {
            "metadata"=>source.GetMetadataAsync("file.bin",stop.Token),
            "discovery"=>source.SupportsCollectionSyncAsync("",stop.Token),
            "report"=>source.GetCollectionChangesAsync("","urn:old",stop.Token),
            "capacity"=>source.GetRemoteCapacityAsync(stop.Token),
            _=>throw new InvalidOperationException()
        };
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if(caller)
        {
            await stop.CancelAsync();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);Assert.True(work.IsCanceled);
        }
        else
        {
            var error=await Assert.ThrowsAsync<SourceConnectionException>(()=>work);
            Assert.Equal(ConnectionFailureKind.Timeout,error.Diagnostic.Kind);Assert.False(stop.IsCancellationRequested);Assert.True(work.IsFaulted);
        }
        Assert.Equal(1,handler.Requests);Assert.True(watch.Elapsed<TimeSpan.FromSeconds(2));
    }
    [Fact]
    public async Task Unrelated_http_task_cancellation_is_still_a_bounded_timeout_failure()
    {
        var handler=new ResetHandler(unrelatedTimeout:true);
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),new WholeFileCache("unused"),handler){ReadRetryDelay=TimeSpan.Zero};
        var error=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync());
        Assert.Equal(ConnectionFailureKind.Timeout,error.Diagnostic.Kind);Assert.Equal(3,error.Diagnostic.Attempts);Assert.Equal(3,handler.Requests);
    }
}
