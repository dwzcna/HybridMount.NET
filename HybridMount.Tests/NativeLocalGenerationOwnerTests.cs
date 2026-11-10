using HybridMount.Models.Mounting;
namespace HybridMount.Tests;

/// <summary>应用发布完整本地媒体时必须经过Stock本地解码器，四帧只由托管缓存写入，不重复访问源。</summary>
[Collection("Native IPC")]
public sealed class NativeLocalGenerationOwnerTests
{
    [Fact(Timeout=90000)]
    public async Task FullLocalApplicationPreviewUsesNativeDecoderAndPersistsAllFourFrames()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Manager.LocalPreviewsForTests=true;test.Dav.Put("native-owner.bmp",PreviewPipelineTests.Bmp());
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var path=Path.Combine(test.Local,"native-owner.bmp");await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));var requests=test.Dav.Server.Requests.Count;var before=CloudFilesNative.InspectPersisted(path);
        var deadline=DateTime.UtcNow.AddSeconds(20);while(test.Manager.GetPreview(path,64) is null&&DateTime.UtcNow<deadline)await Task.Delay(25);
        var details=await test.Manager.ReadDiagnosticsAsync(test.Id);Assert.True(test.Manager.GetPreview(path,64) is not null,details);
        Assert.All(PreviewWorker.Sizes,size=>Assert.NotNull(test.Manager.GetPreview(path,size)));
        var diagnostics=await NativeRuntimeClient.Get().InvokeAsync("thumbnailDiagnostics",new{});Assert.True(diagnostics.GetProperty("decoder").GetProperty("started").GetInt64()>=1);
        var after=CloudFilesNative.InspectPersisted(path);Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(requests,test.Dav.Server.Requests.Count);
    }
}
