using System.Collections.Concurrent;
using System.Diagnostics;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>真实原生 COM 与隔离 IPC 验证，不将其等同于 Explorer 自动选择或视觉验收。</summary>
[Collection("Native IPC")]
public sealed class NativePreviewQueueTests
{
    [Fact(Timeout=60000)]
    public async Task DirectColdMissNeverUsesPipeAndExplicitHydrationAllowsCachedImage()
    {
        await using var test=await UploadFlowTests.Fixture.Create();test.Dav.Put("native.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var path=Path.Combine(test.Local,"native.bmp");var requests=new ConcurrentQueue<string>();
        await using var server=new IpcServerService(test.Logger,NativePreviewInterop.PipeName){Handler=request=>
        {
            requests.Enqueue(request.Operation);Assert.True(IpcProtocol.IsValid(request));
            if(request.Operation=="requestLocalPreview")return new(2,request.RequestId,"accepted","",PreviewStatus:test.Manager.RequestLocalPreview(request.Paths[0],request.PreviewSize!.Value));
            var png=test.Manager.GetPreview(request.Paths[0],request.PreviewSize!.Value);return new(2,request.RequestId,png is null?"notFound":"ok","",PreviewPng:png,PreviewStatus:test.Manager.PreviewState(request.Paths[0]));
        }};server.Start();
        using var provider=new NativePreviewInterop(path);
        Assert.True(provider.Thumbnail(64,out _,out var bitmap)<0);Assert.Equal(0,bitmap);Assert.Empty(requests);Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method=="GET");
        await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);
        var elapsed=Stopwatch.StartNew();Assert.True(provider.Thumbnail(64,out _,out bitmap)<0);Assert.Equal(0,bitmap);Assert.True(elapsed.Elapsed<TimeSpan.FromSeconds(1));
        Assert.Empty(requests);await Task.Delay(150);Assert.Null(test.Manager.GetPreview(path,64));Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method=="GET");Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]))).State);var deadline=DateTime.UtcNow.AddSeconds(20);while(test.Manager.GetPreview(path,64) is null&&DateTime.UtcNow<deadline)await Task.Delay(20);
        Assert.NotNull(test.Manager.GetPreview(path,64));Assert.True(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
        Assert.Equal(0,provider.Thumbnail(64,out _,out bitmap));try{Assert.InRange(NativePreviewInterop.BitmapWidth(bitmap),1,64);}finally{NativePreviewInterop.DeleteObject(bitmap);}
        Assert.Empty(requests);
    }
}
