using System.Buffers.Binary;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>预览协议、预算和真实隔离解码器回归，不将直接 COM 调用冒充 Explorer 自动选择。</summary>
public sealed class PreviewPipelineTests
{
    internal static byte[] Bmp(int width=128,int height=64)
    {
        var stride=(width*3+3)&~3;var bytes=new byte[54+stride*height];bytes[0]=66;bytes[1]=77;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2),bytes.Length);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10),54);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14),40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18),width);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22),-height);BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26),1);BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28),24);
        for(var y=0;y<height;y++)for(var x=0;x<width;x++){var offset=54+y*stride+x*3;bytes[offset]=(byte)(x*255/width);bytes[offset+1]=(byte)(y*255/height);bytes[offset+2]=170;}return bytes;
    }
    private static PreviewRangeBudget Memory(byte[] bytes)=>new(bytes.LongLength,(offset,buffer,_)=>{bytes.AsMemory((int)offset,buffer.Length).CopyTo(buffer);return ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length));});
    [Fact]
    public async Task DavStrictPreviewRefusesWholeFileFallbackAndUnreliableVersions()
    {
        await using var fixture=new DavFixture{IgnoreRange=true};await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");
        Assert.False((await source.TryReadPreviewRangeAsync(entry,0,new byte[64])).Supported);Assert.Equal(1,fixture.Server.Requests.Count(r=>r.Method=="GET"));
        fixture.IgnoreRange=false;fixture.WeakEtag=true;entry=await source.GetMetadataAsync("文件.bin");var gets=fixture.Server.Requests.Count(r=>r.Method=="GET");
        Assert.False((await source.TryReadPreviewRangeAsync(entry,0,new byte[64])).Supported);Assert.Equal(gets,fixture.Server.Requests.Count(r=>r.Method=="GET"));
        fixture.WeakEtag=false;entry=await source.GetMetadataAsync("文件.bin");fixture.BadRange=true;await Assert.ThrowsAsync<SourceConflictException>(()=>source.TryReadPreviewRangeAsync(entry,0,new byte[64]).AsTask());
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method is "PUT" or "MKCOL");Assert.False(Directory.Exists(Path.Combine(fixture.Area,"cache")));
    }
    [Fact]
    public async Task WindowBudgetCoalescesReadsAndStopsAtEightMiBAndThirtyTwoRequests()
    {
        var calls=0;var budget=new PreviewRangeBudget(16L*1024*1024,(_,buffer,_)=>{calls++;buffer.Span.Clear();return ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length));});
        await budget.ReadAsync(0,16,CancellationToken.None);await budget.ReadAsync(8,32,CancellationToken.None);Assert.Equal(1,calls);
        for(var index=1;index<32;index++)await budget.ReadAsync(index*(long)PreviewRangeBudget.WindowBytes,1,CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(()=>budget.ReadAsync(32L*PreviewRangeBudget.WindowBytes,1,CancellationToken.None));Assert.Equal(32,calls);Assert.Equal(8*1024*1024,budget.Bytes);
    }
    [Theory(Timeout=30000)]
    [InlineData(".bmp")][InlineData(".gif")][InlineData(".exe")][InlineData(".jpg")][InlineData(".png")][InlineData(".mp4")]
    public async Task RealWorkerDecodesAllCacheSizesUsingBudgetedAnonymousPipe(string extension)
    {
        var bytes=extension switch{".exe"=>PreviewTests.LargeExecutable(),".gif"=>Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"),".bmp"=>Bmp(),_=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","sample"+extension))};
        var budget=Memory(bytes);var frames=await new PreviewWorkerClient().DecodeAsync(extension,bytes.Length,budget.ReadAsync);
        Assert.NotNull(frames);Assert.Equal(4,frames.Count);Assert.True(budget.Bytes<=PreviewRangeBudget.MaximumBytes);Assert.True(budget.Requests<=32);
        foreach(var frame in frames){Assert.Equal(new byte[]{137,80,78,71,13,10,26,10},frame.Value[..8]);Assert.InRange(BinaryPrimitives.ReadInt32BigEndian(frame.Value.AsSpan(16)),1,frame.Key);}
    }
    [Fact(Timeout=30000)]
    public async Task WorkCancellationEndsDecoderAndDoesNotStartApplicationSession()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var cancel=new CancellationTokenSource();
        var decode=new PreviewWorkerClient().DecodeAsync(".bmp",1024,async(_,_,token)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return [];},cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>decode);
        // 后续成功解码证明取消不占用工作槽或留下共享会话。
        var bytes=Bmp();var frames=await new PreviewWorkerClient().DecodeAsync(".bmp",bytes.Length,Memory(bytes).ReadAsync);Assert.NotNull(frames);
    }
    [Fact]
    public async Task NativeFormatsDefaultOffPersistAndDoNotReadRemoteUntilRequested()
    {
        await using var test=await UploadFlowTests.Fixture.Create();test.Dav.Put("photo.bmp",Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var path=Path.Combine(test.Local,"photo.bmp");var gets=test.Dav.Server.Requests.Count(r=>r.Method=="GET");
        Assert.False(test.Repository.LoadDefinitions().Single().ThumbnailsEnabled);Assert.Null(test.Manager.GetPreview(path,64));Assert.Equal("disabled",test.Manager.RequestPreview(path,64));
        await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);Assert.Equal(gets,test.Dav.Server.Requests.Count(r=>r.Method=="GET"));
        Assert.Equal("queued",test.Manager.RequestPreview(path,64));var deadline=DateTime.UtcNow.AddSeconds(20);byte[]? png;
        while((png=test.Manager.GetPreview(path,64)) is null&&DateTime.UtcNow<deadline)await Task.Delay(25);
        Assert.NotNull(png);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));Assert.True(test.Repository.LoadDefinitions().Single().ThumbnailsEnabled);
        await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);Assert.Null(test.Manager.GetPreview(path,64));Assert.Equal("disabled",test.Manager.RequestPreview(path,64));
    }    [Fact(Timeout=60000)]
    public async Task ClosingOneMountCancelsSlowPreviewAndKeepsOtherMountWorking()
    {
        await using var test=await UploadFlowTests.Fixture.Create();await using var other=new DavFixture();test.Dav.Put("slow.bmp",Bmp());other.Put("other.bmp",Bmp());
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var otherLocal=Path.Combine(other.Area,"second-view");Directory.CreateDirectory(otherLocal);
        var second=await test.Manager.AddAsync("第二个预览挂载",otherLocal,true,source:other.Options);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var handler=test.Dav.Server.Handler;
        test.Dav.Server.Handler=async request=>{var response=await handler(request);return request.Method=="GET"&&request.Target.Contains("slow.bmp",StringComparison.Ordinal)?response with{BeforeBody=release.Task,HeadersSent=entered}:response;};
        try
        {
            await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);await test.Manager.SetThumbnailsEnabledAsync(second.Id,true);
            test.Manager.RequestPreview(Path.Combine(test.Local,"slow.bmp"),64);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var path=Path.Combine(otherLocal,"other.bmp");test.Manager.RequestPreview(path,64);
            await test.Manager.SetThumbnailsEnabledAsync(test.Id,false).WaitAsync(TimeSpan.FromSeconds(5));
            var deadline=DateTime.UtcNow.AddSeconds(20);while(test.Manager.GetPreview(path,64) is null&&DateTime.UtcNow<deadline)await Task.Delay(25);
            Assert.NotNull(test.Manager.GetPreview(path,64));Assert.False(test.Repository.LoadDefinitions().Single(x=>x.Id==test.Id).ThumbnailsEnabled);Assert.True(test.Repository.LoadDefinitions().Single(x=>x.Id==second.Id).ThumbnailsEnabled);
        }
        finally{release.TrySetResult();await WebDavTests.Finished(test.Manager,test.Manager.Queue(second.Id,"clearAndRemove"));}
    }
    [Fact(Timeout=30000)]
    public async Task JpegExifDirectionIsRespectedBeforeCaching()
    {
        var original=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"Assets","sample.jpg"));
        var exif=new byte[]{69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0};
        var bytes=new byte[original.Length+exif.Length+4];original.AsSpan(0,2).CopyTo(bytes);bytes[2]=255;bytes[3]=225;BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4),(ushort)(exif.Length+2));exif.CopyTo(bytes,6);original.AsSpan(2).CopyTo(bytes.AsSpan(6+exif.Length));
        var frames=await new PreviewWorkerClient().DecodeAsync(".jpg",bytes.Length,Memory(bytes).ReadAsync);Assert.NotNull(frames);
        Assert.Equal(32,BinaryPrimitives.ReadInt32BigEndian(frames[128].AsSpan(16)));Assert.Equal(64,BinaryPrimitives.ReadInt32BigEndian(frames[128].AsSpan(20)));
    }
    [Fact(Timeout=60000)]
    public async Task WeakVersionColdFallbackDoesNotBlockCompleteLocalPreview()
    {
        await using var test=await UploadFlowTests.Fixture.Create();test.Dav.WeakEtag=true;test.Dav.Put("weak.bmp",Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var path=Path.Combine(test.Local,"weak.bmp");await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);var gets=test.Dav.Server.Requests.Count(r=>r.Method=="GET");test.Manager.RequestPreview(path,64);
        var deadline=DateTime.UtcNow.AddSeconds(15);while(test.Manager.PreviewState(path)=="queued"&&DateTime.UtcNow<deadline)await Task.Delay(20);
        Assert.Equal("unsupported",test.Manager.PreviewState(path));Assert.Equal(gets,test.Dav.Server.Requests.Count(r=>r.Method=="GET"));
        for(var i=0;i<5;i++)Assert.Equal("unsupported",test.Manager.RequestPreview(path,64));Assert.Equal(gets,test.Dav.Server.Requests.Count(r=>r.Method=="GET"));
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]))).State);
        deadline=DateTime.UtcNow.AddSeconds(20);while(test.Manager.GetPreview(path,64) is null&&DateTime.UtcNow<deadline)await Task.Delay(25);Assert.NotNull(test.Manager.GetPreview(path,64));
    }

    /// <summary>模拟存储失败；内存与持久设置保持原值，页面恢复时不会显示一个未保存的开关状态。</summary>
    [Fact]
    public async Task FailedThumbnailSaveDoesNotChangeRuntimeSetting()
    {
        FailingDefinitionStore? store=null;
        await using var test=await UploadFlowTests.Fixture.Create(definitionStore:registry=>store=new FailingDefinitionStore(new HybridMount.Models.Persistence.RegistryMountDefinitionStore(registry)));
        try
        {
            store!.FailSave=true;
            await Assert.ThrowsAsync<IOException>(()=>test.Manager.SetThumbnailsEnabledAsync(test.Id,true));
            Assert.False(test.Manager.Mounts.Single().Definition.ThumbnailsEnabled);
        }
        finally{store!.FailSave=false;}
        Assert.False(test.Repository.LoadDefinitions().Single().ThumbnailsEnabled);
        await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);Assert.True(test.Manager.Mounts.Single().Definition.ThumbnailsEnabled);
    }

    /// <summary>只注入定义保存失败，实际读写仍使用测试专属注册表，不修改仓库内部字段。</summary>
    private sealed class FailingDefinitionStore(IMountDefinitionStore inner):IMountDefinitionStore
    {
        public bool FailSave;
        public IReadOnlyList<MountDefinition> LoadDefinitions()=>inner.LoadDefinitions();
        public void SaveDefinition(MountDefinition definition){if(FailSave)throw new IOException("test definition save failure");inner.SaveDefinition(definition);}
        public void DeleteDefinition(Guid id)=>inner.DeleteDefinition(id);
        public long CacheLimitBytes{get=>inner.CacheLimitBytes;set=>inner.CacheLimitBytes=value;}
    }

}
