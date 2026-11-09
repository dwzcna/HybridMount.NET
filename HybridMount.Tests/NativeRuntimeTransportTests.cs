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
    [Fact(Timeout=30000)]
    public async Task TerminatedOwnedProcessWakesPendingCallsAndDisposalDoesNotUnregisterAnything()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var process=System.Diagnostics.Process.GetProcessById(runtime.ProcessId);process.Kill();await process.WaitForExitAsync();
        await Assert.ThrowsAnyAsync<Exception>(()=>runtime.InvokeAsync("ping",new{}).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
