using System.Diagnostics;
namespace HybridMount.Tests;
/// <summary>隔离子进程与内存源验证首块预算不挤占解码时间；取消不被当成编解码器故障。</summary>
[Collection("Native IPC")]
public sealed class PreviewPhaseDeadlineTests
{
    [Fact(Timeout=15000)]public async Task SlowFirstRangeHasIndependentDeadlineAndStillProducesFourFrames()
    {
        var bytes=PreviewPipelineTests.Bmp();var stages=new Dictionary<string,long>();var first=true;
        var budget=new PreviewRangeBudget(bytes.Length,async(offset,buffer,token)=>{if(first){first=false;await Task.Delay(800,token);}bytes.AsMemory((int)offset,buffer.Length).CopyTo(buffer);return new PreviewRangeResult(true,buffer.Length);});
        var result=await new PreviewWorkerClient(timing:(stage,ms)=>stages[stage]=ms){PrefetchTimeout=TimeSpan.FromSeconds(2),DecodeTimeout=TimeSpan.FromMilliseconds(500)}.DecodeAsync(".bmp",bytes.Length,budget);
        Assert.NotNull(result);Assert.Equal(4,result.Count);Assert.True(stages["首块预取"]>=700);Assert.InRange(stages["解码"],0,499);Assert.Equal(1,budget.Requests);
    }
    [Theory(Timeout=5000)][InlineData(false)][InlineData(true)]public async Task FirstRangeTimeoutAndCallerCancellationRemainDistinct(bool callerCancel)
    {
        using var stop=new CancellationTokenSource();if(callerCancel)stop.CancelAfter(80);var stages=new List<string>();var budget=new PreviewRangeBudget(1024,async(_,buffer,token)=>{await Task.Delay(Timeout.Infinite,token);return new PreviewRangeResult(true,buffer.Length);});
        var client=new PreviewWorkerClient("must-not-start.exe",timing:(stage,_)=>stages.Add(stage)){PrefetchTimeout=TimeSpan.FromMilliseconds(150)};
        var error=await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>client.DecodeAsync(".bmp",1024,budget,stop.Token));
        if(callerCancel)Assert.IsNotType<PreviewPhaseTimeoutException>(error);else Assert.Equal(PreviewPhase.Prefetch,Assert.IsType<PreviewPhaseTimeoutException>(error).Phase);
        Assert.Equal(new[]{"首块预取"},stages);Assert.Equal(1,budget.Requests);
    }
    [Fact]public void WriteVerificationDiagnosticPreservesOnlyStageTypeAndCode()
    {
        var error=new IOException("https://user:password@example.test/private?token=secret");var safe=new SourceWriteVerificationException("readback-version",error);Assert.Equal("readback-version",safe.Stage);Assert.Equal(error.HResult,safe.HResult);Assert.Null(safe.InnerException);Assert.DoesNotContain("password",safe.ToString());Assert.DoesNotContain("token=",safe.ToString());
    }
}
