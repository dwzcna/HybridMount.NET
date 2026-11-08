using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>隔离NTFS与回环服务验证生产本地路径、独立读库和脱水重绑；不写正式缓存或服务。</summary>
public sealed class LocalThumbnailCacheIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static async Task<string> Photo(UploadFlowTests.Fixture test)
    {
        test.Manager.LocalPreviewsForTests=true;test.Dav.Put("image.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);return Path.Combine(test.Local,"image.bmp");
    }
    private static async Task<byte[]> Ready(UploadFlowTests.Fixture test,string path)
    {
        var deadline=DateTime.UtcNow.AddSeconds(20);while(DateTime.UtcNow<deadline){var png=test.Manager.GetPreview(path,64);if(png is not null)return png;await Task.Delay(30);}throw new TimeoutException(await test.Manager.ReadDiagnosticsAsync(test.Id));
    }
    [Fact(Timeout=90000)]
    public async Task OnlineOnlyMissNeverReadsRemoteOrHydratesEvenThroughLegacyRequestName()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=await Photo(test);var before=CloudFilesNative.Inspect(path)!.Value;var requests=test.Dav.Server.Requests.Count;
        var reader=new ThumbnailCacheReader(Path.Combine(test.Repository.DataDirectory,"Thumbnails","thumbnails.db"));
        for(var i=0;i<10;i++){Assert.Null(reader.TryGet(path,64));Assert.Null(test.Manager.GetPreview(path,64));test.Manager.RequestLocalPreview(path,64);test.Manager.RequestPreview(path,64);}
        var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(before.Info.OnDiskSize,after.Info.OnDiskSize);
        Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));Assert.Equal(requests,test.Dav.Server.Requests.Count);
    }
    [Fact(Timeout=90000)]
    public async Task CachedBlobSurvivesDehydrationAndMainManagerExitWithoutAnyNewRemoteRead()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=await Photo(test);
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]))).State);var png=await Ready(test,path);
        var reader=new ThumbnailCacheReader(Path.Combine(test.Repository.DataDirectory,"Thumbnails","thumbnails.db"));Assert.Equal(png,reader.TryGet(path,64));var requests=test.Dav.Server.Requests.Count;
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"freeSpace",[path]))).State);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));Assert.Equal(png,reader.TryGet(path,64));
        await test.Manager.DisposeAsync();
        try{Assert.True(png.SequenceEqual(reader.TryGet(path,64)??[]),System.Text.Json.JsonSerializer.Serialize(new{Stored=new ThumbnailCacheStore(Path.Combine(test.Repository.DataDirectory,"Thumbnails","thumbnails.db")).Binding(path),Current=LocalFileState.Read(path),Native=CloudFilesNative.Inspect(path),Raw=RawFacts(path)}));Assert.Equal(requests,test.Dav.Server.Requests.Count);}
        finally{test.Manager=new(test.Repository,test.Logger);}
        Assert.All(Directory.GetFiles(Path.Combine(test.Repository.DataDirectory,"Thumbnails")),file=>Assert.True(Path.GetFileName(file) is "thumbnails.db" or "thumbnails.db-wal" or "thumbnails.db-shm"));
    }
    [Fact(Timeout=90000)]
    public async Task LocalDirtyContentCanGenerateWithoutChangingInSyncPinOrUploading()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=await Photo(test);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));var original=await Ready(test,path);
        var bytes=PreviewPipelineTests.Bmp();for(var i=54;i<bytes.Length;i++)bytes[i]=(byte)(255-bytes[i]);using(var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.Read)){writer.Write(bytes);writer.Flush(true);}
        await test.Manager.ScanUploadChangesAsync(test.Id);var before=CloudFilesNative.Inspect(path)!.Value;var identity=LocalFileState.Read(path).Identity;var requests=test.Dav.Server.Requests.Count;
        Assert.Null(test.Manager.GetPreview(path,64));test.Manager.RequestLocalPreview(path,64);var current=await Ready(test,path);Assert.False(original.SequenceEqual(current));
        var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="PUT");
    }
    [Fact(Timeout=90000)]
    public async Task DisabledOrReplacedFileNeverReturnsTheOldBlob()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=await Photo(test);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));await Ready(test,path);
        var reader=new ThumbnailCacheReader(Path.Combine(test.Repository.DataDirectory,"Thumbnails","thumbnails.db"));Assert.NotNull(reader.TryGet(path,64));await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);Assert.Null(reader.TryGet(path,64));
        await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);await Ready(test,path);var temporary=Path.Combine(test.Dav.Area,"new.bmp");await File.WriteAllBytesAsync(temporary,PreviewPipelineTests.Bmp());File.Move(temporary,path,true);Assert.Null(reader.TryGet(path,64));
    }
    [Fact(Timeout=90000)]
    public async Task StaleQueuedSnapshotCannotReenableDisabledCacheOrPublishLateFrames()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);output.WriteLine("Root="+test.Local);var path=await Photo(test);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));
        var mount=test.Manager.Mounts.Single().Definition;var current=mount;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<Dictionary<int,byte[]>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory=Path.Combine(test.Repository.DataDirectory,"race-cache");
        await using var cache=new ExePreviewCache(directory,null,null,async(_,_,_,token)=>{entered.TrySetResult();return await release.Task.WaitAsync(token);},()=>true,_=>current);
        var entry=test.Repository.Entries(test.Id).Single(x=>x.LocalPath=="image.bmp");cache.Queue(mount,entry);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        current=mount with{ThumbnailsEnabled=false};Assert.False(cache.ConfigureMount(mount)!.Enabled);release.SetResult(new(){{64,File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png"))}});
        var deadline=DateTime.UtcNow.AddSeconds(10);while(cache.Describe(mount.Id).Contains("待处理=1",StringComparison.Ordinal)&&DateTime.UtcNow<deadline)await Task.Delay(20);
        Assert.False(new ThumbnailCacheStore(Path.Combine(directory,"thumbnails.db")).Root(mount.Id)!.Enabled);Assert.Null(cache.Get(mount,entry,64));Assert.Null(new ThumbnailCacheReader(Path.Combine(directory,"thumbnails.db")).TryGet(path,64));
        await cache.DisposeAsync();var removal=test.Manager.Queue(test.Id,"clearAndRemove");
        try{await WebDavTests.Finished(test.Manager,removal);}
        catch{output.WriteLine(System.Text.Json.JsonSerializer.Serialize(test.Manager.Operations));output.WriteLine(await test.Manager.ReadDiagnosticsAsync(test.Id));throw;}
    }
    private static object RawFacts(string path)
    {
        using var handle=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0);CloudFilesNative.GetFileInformationByHandleEx(handle,9,out var tag,8);var memory=System.Runtime.InteropServices.Marshal.AllocHGlobal(8192);
        try{return new{tag.Attributes,tag.Tag,State=CloudFilesNative.CfGetPlaceholderStateFromAttributeTag(tag.Attributes,tag.Tag),InfoResult=CloudFilesNative.CfGetPlaceholderInfo(handle,1,memory,8192,out _),Root=CloudFilesNative.RootIdentity(Path.GetDirectoryName(path)!,true)?.Length};}
        finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);}
    }
    [Theory]
    [InlineData("--thumbnail-server")][InlineData("--thumbnail-server","-Embedding")][InlineData("--thumbnail-server","/embedding")]
    public void ThumbnailEntryAcceptsOnlyExplicitComRole(params string[] args)=>Assert.True(ThumbnailServer.ValidArguments(args));
    [Theory]
    [InlineData("--thumbnail-server","C:\\arbitrary.db")][InlineData("--thumbnail-server","-Embedding","extra")][InlineData("--tray")]
    public void ThumbnailEntryRejectsExternalPathsOrOtherAppModes(params string[] args)=>Assert.False(ThumbnailServer.ValidArguments(args));
}
