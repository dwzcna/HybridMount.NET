using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>新缩略图请求复用真实CFAPI与回环DAV；不把后台范围读取当作占位水合。</summary>
public sealed class ThumbnailRequestTests
{
    private static async Task<string> Prepare(UploadFlowTests.Fixture test)
    {
        test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;
        test.Dav.Put("first.png",File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","sample.png")));
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);return Path.Combine(test.Local,"first.png");
    }
    private static async Task<byte[]> Ready(UploadFlowTests.Fixture test,string path)
    {
        var deadline=DateTime.UtcNow.AddSeconds(25);byte[]? png;
        while((png=test.Manager.GetPreview(path,64)) is null&&DateTime.UtcNow<deadline)await Task.Delay(25);
        Assert.NotNull(png);return png;
    }
    [Fact(Timeout=60000)]
    public async Task NewRequestBuildsPerMountCacheWithoutHydratingAndOldLocalRequestStaysLocal()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=await Prepare(test);var before=CloudFilesNative.Inspect(path)!.Value;var requests=test.Dav.Server.Requests.Count(r=>r.Method=="GET");
        test.Manager.RequestLocalPreview(path,64);test.Manager.RequestPreview(path,64);await Task.Delay(100);Assert.Equal(requests,test.Dav.Server.Requests.Count(r=>r.Method=="GET"));
        Assert.Equal("queued",test.Manager.RequestThumbnail(path,64));for(var i=0;i<12;i++)test.Manager.RequestThumbnail(path,128);var png=await Ready(test,path);
        Assert.NotEmpty(png);var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(before.Info.OnDiskSize,after.Info.OnDiskSize);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
        Assert.InRange(test.Dav.Server.Requests.Count(r=>r.Method=="GET")-requests,1,32);Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method is "PUT" or "MOVE" or "DELETE");
        var directory=ThumbnailCachePaths.DirectoryPath;var database=ThumbnailCachePaths.ForMount(directory,test.Id);Assert.True(File.Exists(database));Assert.False(File.Exists(Path.Combine(directory,"thumbnails.db")));Assert.Equal(png,new ThumbnailCacheReader(directory,true).TryGet(path,64));
        await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);Assert.Null(new ThumbnailCacheReader(directory,true).TryGet(path,64));requests=test.Dav.Server.Requests.Count(r=>r.Method=="GET");Assert.Equal("disabled",test.Manager.RequestThumbnail(path,64));Assert.Equal(requests,test.Dav.Server.Requests.Count(r=>r.Method=="GET"));
    }
    [Fact(Timeout=60000)]
    public async Task IgnoredRangeFallsBackWithoutFullDownloadOrRepeatedRequests()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=await Prepare(test);test.Dav.IgnoreRange=true;var gets=test.Dav.Server.Requests.Count(r=>r.Method=="GET");
        test.Manager.RequestThumbnail(path,64);var deadline=DateTime.UtcNow.AddSeconds(20);while(test.Manager.PreviewState(path)=="queued"&&DateTime.UtcNow<deadline)await Task.Delay(25);
        Assert.Equal("unsupported",test.Manager.PreviewState(path));Assert.Null(test.Manager.GetPreview(path,64));for(var i=0;i<8;i++)test.Manager.RequestThumbnail(path,64);Assert.Equal(gets+1,test.Dav.Server.Requests.Count(r=>r.Method=="GET"));Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
    }
    [Theory]
    [InlineData(16,true)][InlineData(256,true)][InlineData(15,false)][InlineData(257,false)]
    public void NewOperationKeepsVersionTwoAndValidatesSize(int size,bool valid)
    {Assert.Equal(valid,IpcProtocol.IsValid(new(2,Guid.NewGuid().ToString(),"requestThumbnail",["C:\\owned\\photo.png"],PreviewSize:size)));}
}
