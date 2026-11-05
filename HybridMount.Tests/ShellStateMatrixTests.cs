using HybridMount.Services.Mounting;
using Xunit.Abstractions;
namespace HybridMount.Tests;

/// <summary>隔离本地HTTP和NTFS中对照转换、缓存和固定，定位Shell状态与原生已同步标记不一致。</summary>
public sealed class ShellStateMatrixTests(ITestOutputHelper output)
{
    [Fact(Timeout=60000)]
    public async Task ConvertedAndHydratedCleanFilesExposeConsistentShellState()
    {
        await using var test=await UploadFlowTests.Fixture.Create();var path=Path.Combine(test.Local,"shell-classify.txt");
        void Log(string stage,string target)
        {
            var native=CloudFilesNative.Inspect(target)!.Value.Info;
            output.WriteLine($"{stage}: {CloudShellState.Read(target)}; Native InSync={native.InSyncState}, Modified={native.ModifiedSize}, Pin={native.PinState}, Properties={native.PropertiesSize}");
        }
        await File.WriteAllTextAsync(path,"verified content");await test.Wait("shell-classify.txt");Log("converted",path);
        var converted=CloudShellState.Read(path);
        var pin=CloudFilesNative.Inspect(path)!.Value.Info.PinState;
        using(var handle=CloudFilesNative.Open(path,true))CloudFilesNative.Check(CloudFilesNative.CfSetPinState(handle,pin,0,0));
        CloudFilesNative.NotifyItemUpdated(path);Log("same pin",path);
        using(var handle=CloudFilesNative.Open(path,true))CloudFilesNative.Check(CloudFilesNative.CfSetPinState(handle,2,0,0));
        CloudFilesNative.NotifyItemUpdated(path);Log("unpinned",path);
        Assert.Equal(10u,CloudShellState.Read(path).ProviderState);
        using(var handle=CloudFilesNative.Open(path,true))CloudFilesNative.Check(CloudFilesNative.CfDehydratePlaceholder(handle,0,-1,0,0));
        CloudFilesNative.NotifyItemUpdated(path);Log("unpin completed",path);Assert.Equal(1u,CloudShellState.Read(path).ProviderState);
        _ = await File.ReadAllBytesAsync(path);Log("reopened",path);
        Assert.Equal(2u,CloudShellState.Read(path).ProviderState);
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]))).State);Log("pinned",path);
        var remote=Path.Combine(test.Local,"文件.bin");_ = await File.ReadAllBytesAsync(remote);Log("hydrated",remote);
        Assert.Equal(2u,converted.ProviderState);
    }
}
