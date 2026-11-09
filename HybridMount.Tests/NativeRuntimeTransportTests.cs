using System.Runtime.InteropServices;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>独立子进程验证内部协议的认证、64位精度、并发响应及进程退出；不接触产品挂载。</summary>
[Collection("Native IPC")]
public sealed class NativeRuntimeTransportTests
{
    [Theory]
    [InlineData("json:302","json:302")]
    [InlineData("json:403","json:403")]
    [InlineData("std-exception","std-exception")]
    [InlineData("json:302:untrusted","operation")]
    public void NativeFailureStageKeepsOnlySafeProtocolStages(string value,string expected)
    {
        var data=NativeRuntimeProtocol.Element(new{failureStage=value});Assert.Equal(expected,NativeRuntimeClient.FailureStage(data));
        Assert.Equal("operation",NativeRuntimeClient.FailureStage(NativeRuntimeProtocol.Element(new{})));
    }
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
    [Fact]
    public void MissingNativeExecutableHasAnActionableMessage()
    {
        NativeRuntimeClient.VerifyExecutable(typeof(NativeRuntimeClient).Assembly.Location);
        var error=Assert.Throws<IOException>(()=>NativeRuntimeClient.VerifyExecutable(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"HybridMount.Native.exe")));
        Assert.Equal("未找到 HybridMount.Native.exe，请重新生成或安装",error.Message);
    }
    [Fact]
    public void HelloBuildDiagnosticsDoNotBypassAuthentication()
    {
        var build=typeof(NativeRuntimeClient).Assembly.GetName().Version!.ToString();var nonce=Guid.NewGuid().ToString("N");var stale=build=="0.0.0.0"?"1.0.0.0":"0.0.0.0";
        System.Text.Json.JsonElement Hello(string? version,string proof)=>NativeRuntimeProtocol.Element(new{kind="hello",version=1,nonce=proof,scope=ApplicationPaths.ProductScope,processId=42,build=version});
        NativeRuntimeClient.VerifyHello(Hello(build,nonce),nonce,42);
        var error=Assert.Throws<IOException>(()=>NativeRuntimeClient.VerifyHello(Hello(stale,nonce),nonce,42));
        Assert.Equal($"原生后台版本({stale})与主程序({build})不一致，请重新生成",error.Message);
        Assert.Contains("版本(未知)",Assert.Throws<IOException>(()=>NativeRuntimeClient.VerifyHello(Hello(null,nonce),nonce,42)).Message);
        Assert.Equal("原生运行时握手不匹配。",Assert.Throws<IOException>(()=>NativeRuntimeClient.VerifyHello(Hello(build,"wrong"),nonce,42)).Message);
        Assert.Contains("0x80070005",NativeRuntimeClient.HandshakeExited(unchecked((int)0x80070005)).Message);
    }
    /// <summary>八个长操作含队列等待；短操作和控制取消仍完成，使用真实后台和实际路由，不写挂载内容。</summary>
    [Fact(Timeout=30000)]
    public async Task OccupiedLongPoolLeavesShortCommandsAndCancellationAvailable()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var cancel=new CancellationTokenSource();var started=0;var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs=Enumerable.Range(0,8).Select(i=>runtime.InvokeAsync((i%3) switch{0=>"hydrateFile",1=>"hydratePinned",_=>"applyRemoteSnapshot"},new{testHold=true},(stage,data)=>{if(Interlocked.Increment(ref started)>=4)ready.TrySetResult();return Task.FromResult<object?>(null);},cancel.Token)).ToArray();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True((await runtime.InvokeAsync("ping",new{}).WaitAsync(TimeSpan.FromSeconds(3))).GetProperty("ready").GetBoolean());
            Assert.Equal(LocalFileState.Read(typeof(NativeRuntimeClient).Assembly.Location),(await runtime.InspectAsync(typeof(NativeRuntimeClient).Assembly.Location).WaitAsync(TimeSpan.FromSeconds(3))).Stamp);
        }
        finally{await cancel.CancelAsync();foreach(var job in jobs)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>job.WaitAsync(TimeSpan.FromSeconds(8)));}
        Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
    }
    /// <summary>畸形领域参数返回有界安全阶段，不泄漏参数正文且不会破坏其他请求。</summary>
    [Fact(Timeout=30000)]
    public async Task MalformedCommandReportsSafeStageWithoutClosingTransport()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();
        var error=await Assert.ThrowsAsync<COMException>(()=>runtime.InvokeAsync("queryFile",new{path=new{secret="do-not-log"}}));
        Assert.Contains("json:",error.Message);Assert.DoesNotContain("do-not-log",error.ToString());
        Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
    }
    /// <summary>实际部署的原生映像因未知角色立即退出；握手不等待12秒到期，报告数字退出原因并归还子进程。</summary>
    [Fact(Timeout=15000)]
    public async Task EarlyExitDuringHandshakeIsReportedPromptly()
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();var error=await Assert.ThrowsAsync<IOException>(()=>NativeRuntimeClient.StartOwnedForTestsAsync(true));
        Assert.Contains("握手期间退出",error.Message);Assert.True(watch.Elapsed<TimeSpan.FromSeconds(5));
    }
}
