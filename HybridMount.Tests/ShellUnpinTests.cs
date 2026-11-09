using System.Text;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>真实NTFS下复现UNPINNED仍保留内容造成的挂起；验证独立于缓存限额的安全收尾。</summary>
public sealed class ShellUnpinTests
{
    private static void Unpin(string path)
    {NativeTestFiles.Pin(path,2);}

    [Theory(Timeout=60000)]
    [InlineData(0L,15)][InlineData(long.MaxValue,15)][InlineData(0L,0)]
    public async Task PendingUnpinCompletesWithoutCachePressureAndPreservesIdentity(long limit,int size)
    {
        await using var test=await UploadFlowTests.Fixture.Create();await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);test.Manager.CacheLimitBytes=limit;
        var path=Path.Combine(test.Local,"unpin.txt");var bytes=Encoding.UTF8.GetBytes(new string('a',size));
        await File.WriteAllBytesAsync(path,bytes);await test.Wait("unpin.txt");
        var identity=LocalFileState.Read(path).Identity;var marker=CloudFilesNative.Inspect(path)!.Value.Identity;Unpin(path);
        Assert.Equal(10u,CloudShellState.Read(path).ProviderState);var requests=test.Dav.Server.Requests.Count;
        await test.Manager.EnforceCacheLimitAsync();
        var after=CloudFilesNative.Inspect(path)!.Value;
        Assert.Equal(1u,CloudShellState.Read(path).ProviderState);Assert.Equal(2u,after.Info.PinState);Assert.Equal(1u,after.Info.InSyncState);
        Assert.Equal(0,after.Info.OnDiskSize);Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(marker,after.Identity);
        Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal("Completed",test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="unpin.txt").State);
        var stable=LocalFileState.Read(path);Assert.Equal(0,await test.Manager.EnforceCacheLimitAsync());
        Assert.Equal(2u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Equal(stable.Key,LocalFileState.Read(path).Key);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path));Assert.Equal(2u,CloudShellState.Read(path).ProviderState);Assert.Equal(identity,LocalFileState.Read(path).Identity);
    }

    [Theory(Timeout=60000)]
    [InlineData("Queued")][InlineData("Sending")][InlineData("OutcomeUnknown")][InlineData("Conflict")][InlineData("Failed")][InlineData("Canceled")]
    public async Task NonCompletedUploadProtectsCachedContent(string state)
    {
        await using var test=await UploadFlowTests.Fixture.Create();await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);test.Manager.CacheLimitBytes=0;
        var path=Path.Combine(test.Local,"protected.txt");await File.WriteAllTextAsync(path,"not discarded");var upload=await test.Wait("protected.txt");
        await test.Manager.SetSchedulingForTestsAsync(test.Id,false);Unpin(path);
        test.Repository.SaveUpload(upload with{State=state,ChangedUtc=DateTimeOffset.UtcNow});
        try
        {
            var before=CloudFilesNative.Inspect(path)!.Value;var requests=test.Dav.Server.Requests.Count;
            Assert.Equal(0,await test.Manager.EnforceCacheLimitAsync());Assert.Equal(before.Info.OnDiskSize,CloudFilesNative.Inspect(path)!.Value.Info.OnDiskSize);
            Assert.Equal(state,test.Repository.Upload(upload.Id)!.State);Assert.Equal(requests,test.Dav.Server.Requests.Count);
        }
        finally{test.Repository.SaveUpload(upload);}
    }

    [Theory(Timeout=60000)]
    [InlineData("pinned")][InlineData("unspecified")][InlineData("dirty")][InlineData("open")][InlineData("remote-pending")][InlineData("remote-deleted")]
    public async Task ModifiedActivePinnedAndRemoteBarriersArePreserved(string protection)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);test.Manager.CacheLimitBytes=0;
        var path=Path.Combine(test.Local,"文件.bin");_ = await File.ReadAllBytesAsync(path);var entry=test.Repository.Entries(test.Id).Single(e=>e.LocalPath=="文件.bin");
        HybridMount.Models.Mounting.RemotePendingChange? pending=null;
        if(protection=="pinned")Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]))).State);
        else if(protection!="unspecified")Unpin(path);
        if(protection=="dirty"){await File.WriteAllTextAsync(path,"local edit must survive");Unpin(path);}
        if(protection=="remote-pending")test.Repository.SaveRemotePending(pending=new(Guid.NewGuid().ToString("N"),test.Id,"unpin-safety",entry.SourcePath,null));
        if(protection=="remote-deleted")Assert.True(test.Repository.SetRemotePresence(test.Id,entry,HybridMount.Models.Mounting.RemotePresence.Deleted));
        try
        {
            using var held=protection=="open"?new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read):null;
            // 截断式编辑可能由系统还原为普通文件；这种本地内容同样必须保留。
            var before=CloudFilesNative.Inspect(path);var stamp=LocalFileState.Read(path);var requests=test.Dav.Server.Requests.Count;
            Assert.Equal(0,await test.Manager.EnforceCacheLimitAsync());var after=CloudFilesNative.Inspect(path);
            Assert.Equal(before?.Info.OnDiskSize,after?.Info.OnDiskSize);Assert.Equal(before?.Info.InSyncState,after?.Info.InSyncState);Assert.Equal(before?.Info.PinState,after?.Info.PinState);
            Assert.Equal(stamp.Identity,LocalFileState.Read(path).Identity);Assert.Equal(before?.Identity,after?.Identity);Assert.Equal(requests,test.Dav.Server.Requests.Count);
            if(protection=="dirty")Assert.Equal("local edit must survive",await File.ReadAllTextAsync(path));
        }
        finally
        {
            if(pending is not null)test.Repository.SaveRemotePending(pending with{State="Superseded"});
            if(protection=="remote-deleted")Assert.True(test.Repository.SetRemotePresence(test.Id,entry,entry.RemotePresence));
        }
    }

    [Fact(Timeout=60000)]
    public async Task RestartCompletesOldRequestAndCancelledMaintenanceDoesNotChangeIt()
    {
        await using var test=await UploadFlowTests.Fixture.Create();await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);test.Manager.CacheLimitBytes=0;
        var path=Path.Combine(test.Local,"restarted.txt");await File.WriteAllTextAsync(path,"restart content");await test.Wait("restarted.txt");Unpin(path);
        var identity=LocalFileState.Read(path).Identity;var puts=test.Dav.Server.Requests.Count(r=>r.Method=="PUT");
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);await test.Manager.ConnectAsync(test.Id);
        using(var cancel=new CancellationTokenSource())
        {cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>test.Manager.EnforceCacheLimitAsync(cancel.Token));}
        Assert.Equal(10u,CloudShellState.Read(path).ProviderState);await test.Manager.EnforceCacheLimitAsync();
        Assert.Equal(1u,CloudShellState.Read(path).ProviderState);Assert.Equal(identity,LocalFileState.Read(path).Identity);
        Assert.Equal(puts,test.Dav.Server.Requests.Count(r=>r.Method=="PUT"));Assert.Equal("restart content",await File.ReadAllTextAsync(path));
    }
}
