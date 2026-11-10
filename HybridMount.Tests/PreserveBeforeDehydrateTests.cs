using System.Diagnostics;
namespace HybridMount.Tests;

/// <summary>没有浏览需求的完整本地文件只在真正释放前留图，原生失败不能取消释放，也不能回读远端。</summary>
[Collection("Native IPC")]
public sealed class PreserveBeforeDehydrateTests
{
    [Theory(Timeout=60000)][InlineData("release")][InlineData("pending")][InlineData("trim")][InlineData("unsupported")][InlineData("timeout")]
    public async Task EveryDehydratePathPreservesWhenPossibleAndContinuesAfterFailure(string mode)
    {
        if(mode=="timeout")await NativeRuntimeClient.StopSharedAsync();
        var old=Environment.GetEnvironmentVariable("HYBRIDMOUNT_TEST_LOCAL_DELAY_MS");if(mode=="timeout")Environment.SetEnvironmentVariable("HYBRIDMOUNT_TEST_LOCAL_DELAY_MS","12000");
        try
        {
            await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Manager.LocalPreviewsForTests=true;test.Manager.BrowsePreviewsForTests=true;await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
            test.Dav.Put("preserve.bmp",mode=="unsupported"?[1,2,3,4]:PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);var path=Path.Combine(test.Local,"preserve.bmp");
            await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));Assert.Null(test.Manager.GetPreview(path,64));var before=CloudFilesNative.InspectPersisted(path);var requests=test.Dav.Server.Requests.Count;
            if(mode is "pending" or "trim"){NativeTestFiles.Pin(path,mode=="pending"?2u:0u);}
            var watch=Stopwatch.StartNew();
            if(mode is "pending" or "trim"){test.Manager.CacheLimitBytes=mode=="trim"?1:0;await test.Manager.EnforceCacheLimitAsync();}
            else Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"freeSpace",[path]))).State);
            Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));Assert.True(watch.Elapsed<TimeSpan.FromSeconds(5));
            if(mode is "unsupported" or "timeout")Assert.Null(test.Manager.GetPreview(path,64));else Assert.All(PreviewWorker.Sizes,size=>Assert.NotNull(test.Manager.GetPreview(path,size)));
            var after=CloudFilesNative.InspectPersisted(path);Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(mode=="trim"?0u:2u,after.Info.PinState);Assert.Equal(requests,test.Dav.Server.Requests.Count);
        }
        finally{Environment.SetEnvironmentVariable("HYBRIDMOUNT_TEST_LOCAL_DELAY_MS",old);if(mode=="timeout")await NativeRuntimeClient.StopSharedAsync();}
    }
}
