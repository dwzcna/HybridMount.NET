using System.Diagnostics;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>按当前浏览项调度远端范围预览；提示不启动上传、不水合，也不扫描整根预生成。</summary>
[Collection("Native IPC")]
public sealed class BrowseThumbnailTests
{
    [Fact(Timeout=90000)]
    public async Task OnlyVisibleMediaGeneratesAndRepeatedHintsReuseCachedFrames()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var notifications=new System.Collections.Concurrent.ConcurrentQueue<string>();test.Manager.ThumbnailNotificationForTests=notifications.Enqueue;test.Dav.Put("visible.bmp",PreviewPipelineTests.Bmp());test.Dav.Put("hidden.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;test.Manager.BrowsePreviewsForTests=true;await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);
        await Task.Delay(150);Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="GET");var path=Path.Combine(test.Local,"visible.bmp");test.Manager.ApplyPreviewDemandForTests(test.Id,[path]);
        await Ready(test,path);var count=test.Dav.Server.Requests.Count;for(var i=0;i<50;i++)test.Manager.ApplyPreviewDemandForTests(test.Id,[path]);await Task.Delay(150);
        var notifiedUntil=DateTime.UtcNow.AddSeconds(3);while(notifications.IsEmpty&&DateTime.UtcNow<notifiedUntil)await Task.Delay(25);
        Assert.Equal(path,Assert.Single(notifications));Assert.Equal(count,test.Dav.Server.Requests.Count);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="GET"&&x.Target.Contains("hidden.bmp",StringComparison.Ordinal));
        Assert.All(test.Dav.Server.Requests.Where(x=>x.Method=="GET"),x=>Assert.NotNull(x.Headers.GetValueOrDefault("Range")));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method is "PUT" or "MOVE" or "DELETE");
    }
    [Fact(Timeout=90000)]
    public async Task OutsidePathsAndDisabledMountNeverStartSourceReads()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("visible.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;test.Manager.BrowsePreviewsForTests=true;await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);
        test.Manager.ApplyPreviewDemandForTests(test.Id,[Path.Combine(test.Dav.Area,"outside.bmp")]);await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);test.Manager.ApplyPreviewDemandForTests(test.Id,[Path.Combine(test.Local,"visible.bmp")]);await Task.Delay(200);Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="GET");
    }
    [Fact(Timeout=90000)]
    public async Task LeavingDirectoryCancelsRangeReadAndPreventsLatePublish()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var notifications=new System.Collections.Concurrent.ConcurrentQueue<string>();test.Manager.ThumbnailNotificationForTests=notifications.Enqueue;test.Dav.Put("slow.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var original=test.Dav.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Dav.Server.Handler=async request=>{if(request.Method=="GET"){entered.TrySetResult();await release.Task;}return await original(request);};
        test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;test.Manager.BrowsePreviewsForTests=true;await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);var path=Path.Combine(test.Local,"slow.bmp");
        try{test.Manager.ApplyPreviewDemandForTests(test.Id,[path]);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));test.Manager.ApplyPreviewDemandForTests(test.Id,[]);release.TrySetResult();await Task.Delay(400);Assert.Empty(notifications);Assert.Null(test.Manager.GetPreview(path,64));Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));}
        finally{release.TrySetResult();}
    }
    [Fact(Timeout=20000)]
    public async Task ObserverIsIndependentNativeProcessAndProducesBoundedMetadataFrames()
    {
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--preview-observer");using var process=Process.Start(start)!;
        try{var frame=await IpcProtocol.ReadAsync<System.Text.Json.JsonElement>(process.StandardOutput.BaseStream,default).WaitAsync(TimeSpan.FromSeconds(8));Assert.Equal("previewViews",frame.GetProperty("kind").GetString());Assert.InRange(frame.GetProperty("views").GetArrayLength(),0,16);process.Refresh();Assert.DoesNotContain(process.Modules.Cast<ProcessModule>(),x=>x.ModuleName.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase));}
        finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}}
    }
    [Fact(Timeout=90000)]
    public async Task ReturningToViewWhileOldReadCancelsRestartsOnlyCurrentDemand()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("return.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var original=test.Dav.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var reads=0;
        test.Dav.Server.Handler=async request=>{if(request.Method=="GET"&&Interlocked.Increment(ref reads)==1){entered.TrySetResult();await release.Task;}return await original(request);};
        test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;test.Manager.BrowsePreviewsForTests=true;await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);var path=Path.Combine(test.Local,"return.bmp");
        try{test.Manager.ApplyPreviewDemandForTests(test.Id,[path]);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));test.Manager.ApplyPreviewDemandForTests(test.Id,[]);test.Manager.ApplyPreviewDemandForTests(test.Id,[path]);release.TrySetResult();await Ready(test,path);Assert.InRange(reads,2,3);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));}
        finally{release.TrySetResult();}
    }
    [Fact(Timeout=90000)]
    public async Task FullyAvailableFileGeneratesFromProtectedLocalContentWithoutSourceReads()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var bytes=PreviewPipelineTests.Bmp(600,600);test.Dav.Put("available.bmp",bytes);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var path=Path.Combine(test.Local,"available.bmp");
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]))).State);var before=CloudFilesNative.InspectPersisted(path);var reads=test.Dav.Server.Requests.Count(x=>x.Method=="GET");
        test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;test.Manager.BrowsePreviewsForTests=true;await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);test.Manager.ApplyPreviewDemandForTests(test.Id,[path]);await Ready(test,path);
        var after=CloudFilesNative.InspectPersisted(path);Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(reads,test.Dav.Server.Requests.Count(x=>x.Method=="GET"));Assert.Equal(bytes,File.ReadAllBytes(path));
    }
    /// <summary>真实15秒负缓存只恢复仍在视口的需求；已经离开的文件不再次读取远端。</summary>
    [Fact(Timeout=60000)]
    public async Task RemoteFailureRetriesAfterFifteenSecondsOnlyForCurrentVisibleDemand()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;test.Manager.BrowsePreviewsForTests=true;
        foreach(var name in new[]{"visible-retry.bmp","abandoned.bmp"})test.Dav.Put(name,PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);
        var counts=new System.Collections.Concurrent.ConcurrentDictionary<string,int>();var original=test.Dav.Server.Handler;
        test.Dav.Server.Handler=async request=>{var response=await original(request);if(request.Method=="GET"&&counts.AddOrUpdate(request.Target,1,(_,count)=>count+1)==1)return response with{Body=new byte[response.Body.Length]};return response;};
        var visible=Path.Combine(test.Local,"visible-retry.bmp");var abandoned=Path.Combine(test.Local,"abandoned.bmp");test.Manager.ApplyPreviewDemandForTests(test.Id,[visible,abandoned]);
        var until=DateTime.UtcNow.AddSeconds(8);while((test.Manager.PreviewState(visible)!="unsupported"||test.Manager.PreviewState(abandoned)!="unsupported")&&DateTime.UtcNow<until)await Task.Delay(25);
        Assert.Equal("unsupported",test.Manager.PreviewState(visible));Assert.Equal("unsupported",test.Manager.PreviewState(abandoned));var failedAt=Stopwatch.StartNew();
        for(var i=0;i<50;i++)test.Manager.ApplyPreviewDemandForTests(test.Id,[visible]);await Task.Delay(1000);Assert.All(counts.Values,count=>Assert.Equal(1,count));
        await Ready(test,visible);Assert.True(failedAt.Elapsed>=TimeSpan.FromSeconds(12));Assert.Null(test.Manager.GetPreview(abandoned,64));Assert.Equal(2,counts.Single(pair=>pair.Key.Contains("visible-retry.bmp",StringComparison.Ordinal)).Value);Assert.Equal(1,counts.Single(pair=>pair.Key.Contains("abandoned.bmp",StringComparison.Ordinal)).Value);
        Assert.False(CloudFilesNative.HasFullContent(visible,new FileInfo(visible).Length));
    }
    private static async Task Ready(UploadFlowTests.Fixture test,string path){var until=DateTime.UtcNow.AddSeconds(15);while(test.Manager.GetPreview(path,64) is null&&DateTime.UtcNow<until)await Task.Delay(25);Assert.NotNull(test.Manager.GetPreview(path,64));}
    /// <summary>模拟裸PROPFIND标签、标准HTTP强标签；浏览生成仍不水合，重复提示不重读源。</summary>
    [Fact(Timeout=90000)]
    public async Task BareMetadataBrowseGeneratesOnceWithoutHydrating()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("bare.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var entry=test.Repository.Entries(test.Id).Single(x=>x.LocalPath=="bare.bmp");var version=System.Text.Json.Nodes.JsonNode.Parse(entry.Version)!;version["ETag"]=version["ETag"]!.GetValue<string>().Trim('"');test.Repository.SaveEntry(test.Id,entry with{Version=version.ToJsonString()});
        test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;test.Manager.BrowsePreviewsForTests=true;await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);var path=Path.Combine(test.Local,"bare.bmp");var before=CloudFilesNative.Inspect(path)!.Value;
        test.Manager.ApplyPreviewDemandForTests(test.Id,[path]);await Ready(test,path);var reads=test.Dav.Server.Requests.Count(x=>x.Method=="GET");for(var i=0;i<30;i++)test.Manager.ApplyPreviewDemandForTests(test.Id,[path]);await Task.Delay(150);
        Assert.Equal(reads,test.Dav.Server.Requests.Count(x=>x.Method=="GET"));Assert.InRange(reads,1,32);var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(before.Identity,after.Identity);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
    }

    /// <summary>原生视口查询瞬态失败不能伪装成空目录并取消当前读取；真正离开视图仍由既有回归验证取消。</summary>
    [Fact(Timeout=90000)]
    public async Task IncompleteViewportSampleDoesNotCancelOrRestartCurrentRead()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("current.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var handler=test.Dav.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Dav.Server.Handler=async request=>{if(request.Method=="GET"){entered.TrySetResult();await release.Task;}return await handler(request);};
        test.Manager.LocalPreviewsForTests=true;test.Manager.RemoteThumbnailsForTests=true;test.Manager.BrowsePreviewsForTests=true;await test.Manager.SetThumbnailsEnabledAsync(test.Id,true);var path=Path.Combine(test.Local,"current.bmp");
        try{test.Manager.ApplyPreviewDemandForTests(test.Id,[path]);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));for(var i=0;i<20;i++)test.Manager.PreviewObservationForTests(test.Id,[],false);await Task.Delay(150);release.TrySetResult();await Ready(test,path);Assert.Single(test.Dav.Server.Requests,x=>x.Method=="GET");}
        finally{release.TrySetResult();}
    }

}
