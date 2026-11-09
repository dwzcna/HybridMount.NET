using System.Runtime.InteropServices;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>独立子进程验证内部协议的认证、64位精度、并发响应及进程退出；不接触产品挂载。</summary>
[Collection("Native IPC")]
public sealed class NativeRuntimeTransportTests
{
    [Fact(Timeout=30000)]
    public async Task QueriesPreserveExactFileStampAndConcurrentResponsesStayCorrelated()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.NativeRuntime."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);var path=Path.Combine(area,"sample.txt");await File.WriteAllTextAsync(path,"native query");
        try
        {
            await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var expected=LocalFileState.Read(path);
            var reads=await Task.WhenAll(Enumerable.Range(0,32).Select(_=>runtime.InspectAsync(path)));
            Assert.All(reads,item=>{Assert.Equal(expected,item.Stamp);Assert.Null(item.Placeholder);Assert.False(item.Full);});
            var ping=await runtime.InvokeAsync("ping",new{});Assert.True(ping.GetProperty("ready").GetBoolean());Assert.Equal(ApplicationPaths.ProductScope,ping.GetProperty("scope").GetString());
            var invalid=await Assert.ThrowsAsync<COMException>(()=>runtime.InspectAsync(path,expectedIdentity:"not-this-file"));Assert.Equal(unchecked((int)0x800703ee),invalid.HResult);
            var unsupported=await Assert.ThrowsAsync<COMException>(()=>runtime.InvokeAsync("arbitraryRegistryDelete",new{path}));Assert.Equal(unchecked((int)0x80004001),unsupported.HResult);
            Assert.Equal("native query",await File.ReadAllTextAsync(path));
        }
        finally{File.Delete(path);Directory.Delete(area);}
    }
    /// <summary>突发查询可由有界队列拒绝单次请求，不能关闭其他挂载共用的传输。</summary>
    [Fact(Timeout=60000)]
    public async Task BurstQueriesKeepControlTransportUsableWithBoundedBusyResponses()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.NativeBurst."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);var path=Path.Combine(area,"metadata.txt");await File.WriteAllTextAsync(path,"unchanged");
        try
        {
            await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var expected=LocalFileState.Read(path);
            await Task.WhenAll(Enumerable.Range(0,1024).Select(async _=>{try{Assert.Equal(expected,(await runtime.InspectAsync(path)).Stamp);}catch(COMException error)when(error.HResult==unchecked((int)0x800700aa)){} }));
            Assert.True(runtime.IsAvailable);Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());Assert.Equal("unchanged",await File.ReadAllTextAsync(path));
        }
        finally{File.Delete(path);Directory.Delete(area);}
    }
    [Fact(Timeout=60000)]
    public async Task CheckpointCanWaitBeyondThirtySecondsForItsAcknowledgement()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();
        var result=await runtime.InvokeAsync("testCheckpoint",new{},async(stage,_)=>{Assert.Equal("test",stage);await Task.Delay(TimeSpan.FromSeconds(31));return new{acknowledged=true};});
        Assert.True(result.GetProperty("acknowledged").GetBoolean());Assert.True(runtime.IsAvailable);
    }
    [Theory(Timeout=15000)]
    [InlineData(false)][InlineData(true)]
    public async Task BlockedCheckpointEndsOnCancellationOrRuntimeFailure(bool terminate)
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var cancel=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request=runtime.InvokeAsync("testCheckpoint",new{},async(_,_)=>{entered.TrySetResult();await release.Task;return null;},cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(terminate){using var process=System.Diagnostics.Process.GetProcessById(runtime.ProcessId);process.Kill();await process.WaitForExitAsync();await Assert.ThrowsAnyAsync<IOException>(()=>request.WaitAsync(TimeSpan.FromSeconds(5)));}
            else{cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request.WaitAsync(TimeSpan.FromSeconds(5)));}
        }
        finally{release.TrySetResult();}
    }
    /// <summary>事件消费者停滞时显式报告通道故障，不能静默丢事件后继续宣称运行正常；停止仍能排空。</summary>
    [Fact(Timeout=30000)]
    public async Task SaturatedStateEventsFailExplicitlyAndWakeWaitingCommand()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.EventReceived+=value=>{if(value.Event=="testState"){entered.TrySetResult();release.Wait(TimeSpan.FromSeconds(10));}};runtime.Failed+=_=>failed.TrySetResult();
        try{var work=runtime.InvokeAsync("testStateEvents",new{mountId=Guid.NewGuid(),sessionId=Guid.NewGuid(),count=2048});await Task.WhenAny(entered.Task,work).WaitAsync(TimeSpan.FromSeconds(5));if(work.IsFaulted)await work;await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(runtime.IsAvailable);await Assert.ThrowsAnyAsync<Exception>(()=>work.WaitAsync(TimeSpan.FromSeconds(5)));}
        finally{release.Set();}
    }
    [Fact(Timeout=30000)]
    public async Task TerminatedOwnedProcessWakesPendingCallsAndDisposalDoesNotUnregisterAnything()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var process=System.Diagnostics.Process.GetProcessById(runtime.ProcessId);process.Kill();await process.WaitForExitAsync();
        await Assert.ThrowsAnyAsync<Exception>(()=>runtime.InvokeAsync("ping",new{}).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
