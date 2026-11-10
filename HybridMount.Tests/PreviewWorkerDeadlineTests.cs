using System.Diagnostics;
namespace HybridMount.Tests;

/// <summary>隔离注入只作用于测试原生映像，验证启动与解码分别计时，以及同步匿名管道的实际退出。</summary>
[Collection("Native IPC")]
public sealed class PreviewWorkerDeadlineTests
{
    [Fact(Timeout=30000)]
    public async Task StartupDoesNotConsumeDecodeBudgetAndBothDeadlinesReapTheirOwnProcess()
    {
        var bytes=PreviewPipelineTests.Bmp();Task<byte[]> Read(long offset,int count,CancellationToken token)=>Task.FromResult(bytes.AsSpan((int)offset,count).ToArray());
        try
        {
            Environment.SetEnvironmentVariable("HYBRIDMOUNT_TEST_PREVIEW_READY_MS","700");var stages=new Dictionary<string,long>();
            var frames=await new PreviewWorkerClient(timing:(stage,ms)=>stages[stage]=ms){DecodeTimeout=TimeSpan.FromMilliseconds(500)}.DecodeAsync(".bmp",bytes.Length,Read);
            Assert.NotNull(frames);Assert.True(stages["启动"]>=650);Assert.InRange(stages["解码"],0,499);
            var watch=Stopwatch.StartNew();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new PreviewWorkerClient{StartupTimeout=TimeSpan.FromMilliseconds(100)}.DecodeAsync(".bmp",bytes.Length,Read));Assert.True(watch.Elapsed<TimeSpan.FromSeconds(3));
            Environment.SetEnvironmentVariable("HYBRIDMOUNT_TEST_PREVIEW_READY_MS",null);Environment.SetEnvironmentVariable("HYBRIDMOUNT_TEST_PREVIEW_DECODE_MS","1500");watch.Restart();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new PreviewWorkerClient{DecodeTimeout=TimeSpan.FromMilliseconds(150)}.DecodeAsync(".bmp",bytes.Length,Read));Assert.True(watch.Elapsed<TimeSpan.FromSeconds(3));
        }
        finally{Environment.SetEnvironmentVariable("HYBRIDMOUNT_TEST_PREVIEW_READY_MS",null);Environment.SetEnvironmentVariable("HYBRIDMOUNT_TEST_PREVIEW_DECODE_MS",null);}
    }
}
