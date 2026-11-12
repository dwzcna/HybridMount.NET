using System.Collections.Concurrent;
using HybridMount.Services.Thumbnails;
namespace HybridMount.Tests;

/// <summary>真实隔离根配合受控生成者，验证下一项优先级、消费者合并和通知与缓存的分离。</summary>
public sealed class PreviewFocusSchedulingTests
{
    private static async Task Until(Func<bool> condition)
    {for(var i=0;i<300&&!condition();i++)await Task.Delay(25);Assert.True(condition());}
    [Fact(Timeout=90000)]
    public async Task ForegroundOvertakesWaitingBackgroundAndBackgroundPublishesWithoutRefresh()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);
        foreach(var name in new[]{"first.bmp","background.bmp","foreground.bmp"})test.Dav.Put(name,PreviewPipelineTests.Bmp());
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var mount=test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true};
        var entries=test.Repository.Entries(test.Id).Where(entry=>entry.LocalPath.EndsWith(".bmp",StringComparison.Ordinal)).ToDictionary(entry=>entry.LocalPath);
        var first=entries["first.bmp"];var background=entries["background.bmp"];var foreground=entries["foreground.bmp"];
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order=new ConcurrentQueue<string>();var notices=new ConcurrentQueue<string>();var png=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png"));
        await using var cache=new ExePreviewCache(Path.Combine(test.Repository.DataDirectory,"FocusCache"),render:async(_,entry,_,token)=>
        {order.Enqueue(entry.LocalPath);if(entry.Id==first.Id){started.TrySetResult();await release.Task.WaitAsync(token);}return new Dictionary<int,byte[]>{{32,png},{64,png},{128,png},{256,png}};});
        cache.ThumbnailNotificationForTests=notices.Enqueue;
        try
        {
            cache.SetBrowseDemand(mount,[first,background,foreground],true,new HashSet<string>());await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cache.SetBrowseDemand(mount,[first,background,foreground],true,new HashSet<string>{foreground.Id});release.TrySetResult();
            await Until(()=>cache.Get(mount,background,64) is not null&&cache.Get(mount,foreground,64) is not null);
            Assert.Equal(new[]{"first.bmp","foreground.bmp","background.bmp"},order.ToArray());
            await Until(()=>notices.Any(path=>path.EndsWith("foreground.bmp",StringComparison.Ordinal)));Assert.DoesNotContain(notices,path=>path.EndsWith("background.bmp",StringComparison.Ordinal));
            cache.SetBrowseDemand(mount,[first,background,foreground],true,new HashSet<string>{background.Id});
            await Until(()=>notices.Any(path=>path.EndsWith("background.bmp",StringComparison.Ordinal)));Assert.Equal(3,order.Count);Assert.InRange(cache.PendingForTests,0,128);
        }
        finally{release.TrySetResult();}
    }
    [Fact(Timeout=90000)]
    public async Task RemovingBrowseConsumerKeepsExplicitConsumerOfTheSameDecode()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("shared.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var mount=test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true};var entry=test.Repository.Entries(test.Id).Single(entry=>entry.LocalPath=="shared.bmp");var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var count=0;var png=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png"));
        await using var cache=new ExePreviewCache(Path.Combine(test.Repository.DataDirectory,"SharedFocus"),render:async(_,_,_,token)=>{Interlocked.Increment(ref count);started.TrySetResult();await release.Task.WaitAsync(token);return new Dictionary<int,byte[]>{{32,png},{64,png},{128,png},{256,png}};});
        try{cache.SetBrowseDemand(mount,[entry],true);await started.Task;cache.Queue(mount,entry,true);cache.SetBrowseDemand(mount,[],true);release.TrySetResult();await Until(()=>cache.Get(mount,entry,64) is not null);Assert.Equal(1,count);}
        finally{release.TrySetResult();}
    }
    [Fact(Timeout=120000)]
    public async Task FullQueueLetsForegroundReplaceBackgroundAndRefillsTheDisplacedDemand()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var bitmap=PreviewPipelineTests.Bmp();
        for(var i=0;i<130;i++)test.Dav.Put($"bounded-{i:D3}.bmp",bitmap);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var mount=test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true};var entries=test.Repository.Entries(test.Id).Where(entry=>entry.LocalPath.StartsWith("bounded-",StringComparison.Ordinal)).OrderBy(entry=>entry.LocalPath,StringComparer.Ordinal).ToArray();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var order=new ConcurrentQueue<string>();var png=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png"));
        await using var cache=new ExePreviewCache(Path.Combine(test.Repository.DataDirectory,"BoundedFocus"),render:async(_,entry,_,token)=>
        {order.Enqueue(entry.Id);if(entry.Id==entries[0].Id){started.TrySetResult();await release.Task.WaitAsync(token);}return new Dictionary<int,byte[]>{{32,png},{64,png},{128,png},{256,png}};});
        cache.ThumbnailNotificationForTests=_=>{};
        try
        {
            cache.SetBrowseDemand(mount,entries,true,new HashSet<string>());await started.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(128,cache.PendingForTests);
            cache.SetBrowseDemand(mount,entries,true,new HashSet<string>{entries[^1].Id});Assert.Equal(128,cache.PendingForTests);release.TrySetResult();
            await Until(()=>order.Count==130&&cache.PendingForTests==0);Assert.Equal(entries[^1].Id,order.ToArray()[1]);Assert.Equal(130,order.Distinct().Count());
            Assert.NotNull(cache.Get(mount,entries[^3],64));
        }
        finally{release.TrySetResult();}
    }

}
