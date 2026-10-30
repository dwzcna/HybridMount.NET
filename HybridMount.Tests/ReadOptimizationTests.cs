using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>共享下载和排队的行为验证；可控屏障代替网络速度假设，不触碰用户挂载。</summary>
public sealed class ReadOptimizationTests
{
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    [Fact(Timeout=15000)]
    public async Task FirstWaiterCancellationDoesNotCancelSharedDownloadAndVersionsAreIsolated()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.ReadTests",Guid.NewGuid().ToString("N"));
        try
        {
            await using var cache=new WholeFileCache(area);
            var entry=new SourceEntry("","app.exe",false,4,DateTimeOffset.UnixEpoch,"v1");
            var entered=Signal();var release=Signal();var downloads=0;
            async Task Download(Stream output,CancellationToken cancel)
            {Interlocked.Increment(ref downloads);entered.TrySetResult();await release.Task.WaitAsync(cancel);await output.WriteAsync(new byte[]{1,2,3,4},cancel);}
            using var canceled=new CancellationTokenSource();var firstBytes=new byte[2];var secondBytes=new byte[2];
            var first=cache.ReadAsync("scope",entry,0,firstBytes,Download,canceled.Token).AsTask();await entered.Task;
            var second=cache.ReadAsync("scope",entry,2,secondBytes,Download,default).AsTask();
            canceled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
            release.SetResult();Assert.Equal(2,await second);Assert.Equal(new byte[]{3,4},secondBytes);Assert.Equal(1,downloads);
            await cache.ReadAsync("scope",entry with{Version="v2"},0,firstBytes,Download,default);Assert.Equal(2,downloads);
        }
        finally{if(Directory.Exists(area))Directory.Delete(area,true);}
    }
    [Fact(Timeout=15000)]
    public async Task LastWaiterAndShutdownCancelUnderlyingWorkAndRetryCanSucceed()
    {
        await using var work=new SharedAsyncWork<string,int>();var entered=Signal();var stopped=Signal();
        using var cancel=new CancellationTokenSource();
        var task=work.RunAsync("same",async token=>{entered.SetResult();try{await Task.Delay(Timeout.Infinite,token);return 0;}finally{stopped.SetResult();}},cancel.Token);
        await entered.Task;cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);await stopped.Task;
        Assert.Equal(2,await work.RunAsync("same",_=>Task.FromResult(2),default));
        var enteredAgain=Signal();var pending=work.RunAsync("other",async token=>{enteredAgain.SetResult();await Task.Delay(Timeout.Infinite,token);return 0;},default);
        await enteredAgain.Task;await work.DisposeAsync();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        await Assert.ThrowsAsync<ObjectDisposedException>(()=>work.RunAsync("late",_=>Task.FromResult(0),default));
    }
    [Fact(Timeout=15000)]
    public async Task OverlappingMetadataUsesOneTaskAndCanceledWaiterDoesNotPoisonIt()
    {
        await using var work=new SharedAsyncWork<string,int>();var release=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        Task<int> Fetch(CancellationToken token){Interlocked.Increment(ref calls);return release.Task.WaitAsync(token);}
        using var cancel=new CancellationTokenSource();var first=work.RunAsync("file",Fetch,cancel.Token);var second=work.RunAsync("file",Fetch,default);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);release.SetResult(42);
        Assert.Equal(42,await second);Assert.Equal(1,calls);
    }
    [Fact(Timeout=15000)]
    public async Task PriorityQueueHonorsHintsExplicitHydrationCancellationAndFairness()
    {
        var queue=new PriorityReadQueue(1);var active=await queue.EnterAsync(0,false,default);
        var ordinary=queue.EnterAsync(0,false,default);var high=queue.EnterAsync(15,false,default);var explicitRead=queue.EnterAsync(0,true,default);
        using var cancel=new CancellationTokenSource();var canceled=queue.EnterAsync(15,true,cancel.Token);cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>canceled);
        active.Dispose();var lease=await explicitRead;Assert.False(high.IsCompleted);lease.Dispose();lease=await high;lease.Dispose();
        lease=await ordinary;var oldest=queue.EnterAsync(0,false,default);
        for(var index=0;index<4;index++){var urgent=queue.EnterAsync(15,true,default);lease.Dispose();lease=await urgent;}
        var lastUrgent=queue.EnterAsync(15,true,default);lease.Dispose();lease=await oldest.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(lastUrgent.IsCompleted);lease.Dispose();(await lastUrgent).Dispose();
    }
    [Fact]
    public void ProcessSnapshotSurvivesNativeMemoryReleaseWithoutKeepingCommandLine()
    {
        var text=Marshal.StringToHGlobalUni(@"C:\Windows\explorer.exe");var memory=Marshal.AllocHGlobal(32);
        HydrationProcess copy;
        try{for(var i=0;i<32;i++)Marshal.WriteByte(memory,i,0);Marshal.WriteInt32(memory,32);Marshal.WriteInt32(memory,4,1234);Marshal.WriteIntPtr(memory,8,text);copy=HydrationProcess.Copy(memory);}
        finally{Marshal.FreeHGlobal(text);Marshal.FreeHGlobal(memory);}
        Assert.Equal(1234u,copy.Id);Assert.Equal("explorer.exe",copy.Image);
    }
    [Fact(Timeout=20000)]
    public async Task SlowWeakWebDavDownloadsOnceWithIndependentCancellation()
    {
        await using var fixture=new DavFixture{WeakEtag=true};var handler=fixture.Server.Handler;var entered=Signal();var release=Signal();
        fixture.Server.Handler=async request=>{if(request.Method=="GET"){entered.TrySetResult();await release.Task;}return await handler(request);};
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");using var cancel=new CancellationTokenSource();
        var first=source.ReadRangeAsync(entry,0,new byte[10],cancel.Token).AsTask();await entered.Task;
        var bytes=new byte[7];var second=source.ReadRangeAsync(entry,9,bytes).AsTask();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);release.SetResult();await second;
        Assert.Equal(fixture.Bytes[9..16],bytes);Assert.Single(fixture.Server.Requests,request=>request.Method=="GET");
    }
    [Fact(Timeout=20000)]
    public async Task IgnoredRangeTransfersResponseOwnershipAndColdConcurrentReadersIssueOneGet()
    {
        await using var fixture=new DavFixture{IgnoreRange=true};var handler=fixture.Server.Handler;var headers=Signal();var release=Signal();
        fixture.Server.Handler=async request=>{var response=await handler(request);return request.Method=="GET"?response with{BeforeBody=release.Task,HeadersSent=headers}:response;};
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");using var cancel=new CancellationTokenSource();
        var first=source.ReadRangeAsync(entry,0,new byte[7],cancel.Token).AsTask();await headers.Task;
        var secondBytes=new byte[7];var second=source.ReadRangeAsync(entry,9,secondBytes).AsTask();
        // 两位等待者已在探测前加入同一共享工作；取消第一位不依赖线程池交接时序。
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);release.SetResult();await second;
        Assert.Equal(fixture.Bytes[9..16],secondBytes);Assert.Single(fixture.Server.Requests,request=>request.Method=="GET");
    }
    [Theory(Timeout=20000)]
    [InlineData("W/\"weak\"")][InlineData("")]
    public async Task UnreliableSnapshotRejectsVersionChangeBeforePublishing(string tag)
    {
        await using var fixture=new DavFixture();fixture.XmlTag=_=>tag;fixture.HttpTag=_=>null;
        var handler=fixture.Server.Handler;var headers=Signal();var release=Signal();
        fixture.Server.Handler=async request=>{var response=await handler(request);return request.Method=="GET"?response with{BeforeBody=release.Task,HeadersSent=headers}:response;};
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");
        var pending=source.ReadRangeAsync(entry,0,new byte[9]).AsTask();await headers.Task;fixture.Put("文件.bin",[1,2,3]);release.SetResult();
        await Assert.ThrowsAsync<SourceConflictException>(()=>pending);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Area,"cache"),"*.cache"));
    }
    [Theory]
    [InlineData("","missing_etag")][InlineData("W/\"weak\"","weak_etag")][InlineData("bare","nonstandard_etag")]
    public async Task WholeFileDiagnosticsReportReasonAndActualDownloadedBytes(string tag,string reason)
    {
        await using var fixture=new DavFixture();fixture.XmlTag=_=>tag;fixture.HttpTag=_=>null;await using var source=fixture.Source();
        var events=new System.Collections.Concurrent.ConcurrentQueue<SourceReadDiagnostic>();source.ReadDiagnostic+=events.Enqueue;
        var entry=await source.GetMetadataAsync("文件.bin");await source.ReadRangeAsync(entry,0,new byte[7]);
        Assert.Contains(events,item=>item.Strategy=="whole_file"&&item.FallbackReason==reason&&item.DownloadedBytes==fixture.Bytes.Length&&item.Completed);
    }
    [Theory]
    [InlineData(0,4096,0,4096,true)][InlineData(4096,4096,0,4096,false)]
    [InlineData(0,8192,4096,4096,false)][InlineData(4096,4096,0,-1,true)]
    [InlineData(4096,4096,4096,long.MaxValue,true)][InlineData(0,4096,0,0,false)]
    public void CancellationRespectsRangesEvenWhenRequestKeyIsZero(long offset,long length,long cancelOffset,long cancelLength,bool expected)
        =>Assert.Equal(expected,CloudMountSession.CancellationCovers(offset,length,cancelOffset,cancelLength));
}
