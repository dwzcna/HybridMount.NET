using System.Diagnostics;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;
/// <summary>独占原生IPC集合，故障停止和显式重连仅操作本轮隔离根。</summary>
[Collection("Native IPC")]
public sealed class NativeRuntimeFailureTests
{
    [Fact(Timeout=90000)]
    public async Task NativeCrashStopsMountButKeepsRegistrationAndExplicitReconnectCreatesNewGeneration()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var mount=test.Manager.Mounts.Single().Definition;var old=NativeRuntimeClient.Get();
        var oldProcessId=old.ProcessId;using(var process=Process.GetProcessById(oldProcessId)){var modules=process.Modules.Cast<ProcessModule>().Select(item=>item.ModuleName);Assert.DoesNotContain(modules,item=>item.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase));process.Kill();await process.WaitForExitAsync();}
        var until=DateTime.UtcNow.AddSeconds(10);while(test.Manager.Mounts.Single().Status!="原生后台已断开"&&DateTime.UtcNow<until)await Task.Delay(25);
        Assert.Equal("原生后台已断开",test.Manager.Mounts.Single().Status);Assert.Single(test.Repository.LoadDefinitions());await using(var inspector=await NativeRuntimeClient.StartOwnedForTestsAsync()){var state=await inspector.RootInfoAsync(test.Local);Assert.True(state.GetProperty("hresult").GetInt32()>=0);Assert.Equal(mount.Id,System.Text.Json.JsonSerializer.Deserialize<MountPathGuard.RootMarker>(state.GetProperty("identity").GetBytesFromBase64())!.MountId);}
        await test.Manager.ConnectAsync(test.Id);Assert.Equal("已连接",test.Manager.Mounts.Single().Status);Assert.NotEqual(oldProcessId,NativeRuntimeClient.Get().ProcessId);Assert.Equal(mount.RootIdentity,MountPathGuard.FileIdentity(test.Local));
        Assert.Equal(test.Dav.Objects["文件.bin"].Bytes,await File.ReadAllBytesAsync(Path.Combine(test.Local,"文件.bin")));
    }
}
