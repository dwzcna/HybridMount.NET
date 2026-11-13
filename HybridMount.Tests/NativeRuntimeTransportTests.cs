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
    [Fact(Timeout=15000)]
    public async Task DroppedPreviewWakeStillPublishesTheLatestWithdrawalAfterNoticeFlood()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var release=new ManualResetEventSlim();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var withdrawn=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var mount=Guid.NewGuid().ToString("N");
        runtime.EventReceived+=value=>{if(value.Event=="renameRejected"&&value.Data.TryGetProperty("block",out _)){entered.TrySetResult();release.Wait(TimeSpan.FromSeconds(10));}else if(value.Event=="previewDemand"&&value.Data.GetProperty("paths").GetArrayLength()==0)withdrawn.TrySetResult();};
        try
        {
            runtime.PostPreviewForTests(new(mount,"session","renameRejected",NativeRuntimeProtocol.Element(new{block=true})));await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            runtime.PostPreviewForTests(new(mount,"session","previewDemand",NativeRuntimeProtocol.Element(new{complete=true,paths=new[]{"old.bmp"}})));
            for(var index=0;index<512;index++)runtime.PostPreviewForTests(new(mount,"session","renameRejected",NativeRuntimeProtocol.Element(new{sequence=index})));
            runtime.PostPreviewForTests(new(mount,"session","previewDemand",NativeRuntimeProtocol.Element(new{complete=true,paths=Array.Empty<string>()})));
            release.Set();await withdrawn.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.True(runtime.IsAvailable);
        }
        finally{release.Set();}
    }
    [Fact(Timeout=15000)]
    public async Task SlowMetadataPoolCannotOccupyCompletionOrOrdinaryWorkers()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var cancel=new CancellationTokenSource();
        var slow=Enumerable.Range(0,4).Select(_=>runtime.InvokeAsync("testPoolDelay",new{pool="metadata",milliseconds=5000},token:cancel.Token)).ToArray();
        try
        {
            var deadline=Environment.TickCount64+3000;System.Text.Json.JsonElement diagnostics;
            do{diagnostics=await runtime.InvokeAsync("runtimeSchedulingDiagnostics",new{});if(diagnostics.GetProperty("metadata").GetProperty("active").GetInt32()==4)break;await Task.Delay(5);}while(Environment.TickCount64<deadline);
            Assert.Equal(4,diagnostics.GetProperty("metadata").GetProperty("active").GetInt32());
            Assert.Equal(8,diagnostics.GetProperty("ordinary").GetProperty("threads").GetInt32());Assert.Equal(4,diagnostics.GetProperty("long").GetProperty("threads").GetInt32());Assert.Equal(2,diagnostics.GetProperty("completion").GetProperty("threads").GetInt32());
            Assert.True((await runtime.InvokeAsync("testCompletion",new{}).WaitAsync(TimeSpan.FromMilliseconds(500))).GetProperty("completed").GetBoolean());
            Assert.True((await runtime.InvokeAsync("ping",new{}).WaitAsync(TimeSpan.FromMilliseconds(500))).GetProperty("ready").GetBoolean());
        }
        finally{cancel.Cancel();foreach(var task in slow)try{await task;}catch(OperationCanceledException){} }
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
        var error=Assert.Throws<IOException>(()=>NativeRuntimeClient.VerifyExecutable(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"HybridMount.Stock.exe")));
        Assert.Equal("未找到 HybridMount.Stock.exe，请重新生成或安装",error.Message);
    }
    [Fact]
    public void HelloBuildDiagnosticsDoNotBypassAuthentication()
    {
        var build=typeof(NativeRuntimeClient).Assembly.GetName().Version!.ToString();var nonce=Guid.NewGuid().ToString("N");var stale=build=="0.0.0.0"?"1.0.0.0":"0.0.0.0";
        System.Text.Json.JsonElement Hello(string? version,string proof)=>NativeRuntimeProtocol.Element(new{kind="hello",version=NativeRuntimeProtocol.Version,nonce=proof,scope=ApplicationPaths.ProductScope,processId=42,build=version});
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
        var jobs=Enumerable.Range(0,8).Select(i=>runtime.InvokeAsync((i%3) switch{0=>"hydrateFile",1=>"hydratePinned",_=>"applyRemoteSnapshot"},new{testHold=true},(stage,data,token)=>{if(Interlocked.Increment(ref started)>=4)ready.TrySetResult();return Task.FromResult<object?>(null);},cancel.Token)).ToArray();
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
    [Fact(Timeout=60000)]
    public async Task CheckpointCanWaitBeyondThirtySecondsForItsAcknowledgement()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();
        var result=await runtime.InvokeAsync("testCheckpoint",new{},async(stage,_,token)=>{Assert.Equal("test",stage);await Task.Delay(TimeSpan.FromSeconds(31));return new{acknowledged=true};});
        Assert.True(result.GetProperty("acknowledged").GetBoolean());Assert.True(runtime.IsAvailable);
    }
    [Theory(Timeout=15000)]
    [InlineData(false)][InlineData(true)]
    public async Task BlockedCheckpointEndsOnCancellationOrRuntimeFailure(bool terminate)
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var cancel=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request=runtime.InvokeAsync("testCheckpoint",new{},async(_,_,token)=>{entered.TrySetResult();await release.Task.WaitAsync(token);return null;},cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(terminate){using var process=System.Diagnostics.Process.GetProcessById(runtime.ProcessId);process.Kill();await process.WaitForExitAsync();await Assert.ThrowsAnyAsync<IOException>(()=>request.WaitAsync(TimeSpan.FromSeconds(5)));}
            else{cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request.WaitAsync(TimeSpan.FromSeconds(5)));}
        }
        finally{release.TrySetResult();}
    }
    [Fact(Timeout=15000)]
    public async Task ManagedCheckpointAcknowledgesOnlyAfterTheHandlerReturns()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var cancel=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);CancellationToken handlerToken=default;
        var request=runtime.InvokeAsync("testManagedCheckpoint",new{},async(stage,_,token)=>{Assert.Equal("test",stage);handlerToken=token;entered.TrySetResult();await release.Task;returned.TrySetResult();return new{acknowledged=true};},cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();Assert.True(handlerToken.IsCancellationRequested);
            Assert.False(request.IsCompleted);release.TrySetResult();await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
        }
        finally{release.TrySetResult();}
    }
    [Theory(Timeout=25000)]
    [InlineData(false)][InlineData(true)]
    public async Task ManagedCheckpointCancellationAndShutdownKeepOwnersUntilHandlerEnds(bool stop)
    {
        var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var cancel=new CancellationTokenSource();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handlerToken=default;var persisted=false;
        var request=runtime.InvokeAsync("testManagedCheckpoint",new{},async(_,_,token)=>{handlerToken=token;entered.TrySetResult();if(stop)await release.Task;else await Task.Delay(Timeout.Infinite,token);token.ThrowIfCancellationRequested();persisted=true;return null;},cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(stop){runtime.CheckpointStopTimeout=TimeSpan.FromMilliseconds(20);var diagnosed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);runtime.CheckpointStopTimeoutForTests=()=>diagnosed.TrySetResult();var stopping=runtime.DisposeAsync().AsTask();await diagnosed.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(stopping.IsCompleted);release.TrySetResult();await stopping.WaitAsync(TimeSpan.FromSeconds(5));await Assert.ThrowsAnyAsync<IOException>(()=>request);}
            else{cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>request.WaitAsync(TimeSpan.FromSeconds(5)));}
            Assert.True(handlerToken.IsCancellationRequested);Assert.False(persisted);
        }
        finally{release.TrySetResult();await runtime.DisposeAsync();}
    }
    [Fact(Timeout=15000)]
    public async Task ManagedCheckpointWithoutCancellationReturnsItsResultAndMissingHandlerAborts()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();
        var result=await runtime.InvokeAsync("testManagedCheckpoint",new{},(_,_,token)=>{token.ThrowIfCancellationRequested();return Task.FromResult<object?>(new{acknowledged=true});});
        Assert.True(result.GetProperty("acknowledged").GetBoolean());
        var error=await Assert.ThrowsAsync<COMException>(()=>runtime.InvokeAsync("testManagedCheckpoint",new{}));Assert.Equal(unchecked((int)0x800703e3),error.HResult);
    }
    /// <summary>用户取消发生在不可逆许可之后，安全落库收到独立令牌，提交终态必须完成。</summary>
    [Fact(Timeout=15000)]
    public async Task IrreversibleSafetyCheckpointDoesNotInheritCallerCancellation()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var cancel=new CancellationTokenSource();var committed=false;
        var result=await runtime.InvokeAsync("testManagedCheckpoint",new{irreversible=true},(stage,_,token)=>{token.ThrowIfCancellationRequested();if(stage=="beginWrite")cancel.Cancel();else{Assert.Equal("commit",stage);Assert.False(token.IsCancellationRequested);committed=true;}return Task.FromResult<object?>(new{acknowledged=true});},cancel.Token);
        Assert.True(committed);Assert.True(result.GetProperty("acknowledged").GetBoolean());
    }
    /// <summary>期限请求合作取消，ACK必须晚于处理器finally；不以计时器替代实际处理完成。</summary>
    [Fact(Timeout=15000)]
    public async Task CooperativeHandlerDeadlineWaitsForActualHandlerCompletion()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();runtime.CheckpointHandlerTimeout=TimeSpan.FromMilliseconds(30);var completed=false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>runtime.InvokeAsync("testManagedCheckpoint",new{},async(_,_,token)=>{try{await Task.Delay(Timeout.Infinite,token);return null;}finally{completed=true;}}));Assert.True(completed);
    }
    /// <summary>说明事件的慢消费者和突发提示只允许丢弃提示，不占关键回调队列或关闭共享连接。</summary>
    [Fact(Timeout=30000)]
    public async Task DiscardableHintsCannotBlockControlResponses()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.EventReceived+=message=>{if(message.Event=="renameRejected"){entered.TrySetResult();release.Wait(TimeSpan.FromSeconds(10));}};
        try{var hints=runtime.InvokeAsync("testDiscardableHints",new{mountId=Guid.NewGuid(),sessionId=Guid.NewGuid(),count=4096});await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));await hints.WaitAsync(TimeSpan.FromSeconds(5));Assert.True((await runtime.InvokeAsync("ping",new{}).WaitAsync(TimeSpan.FromSeconds(3))).GetProperty("ready").GetBoolean());Assert.True(runtime.IsAvailable);}
        finally{release.Set();}
    }
}
