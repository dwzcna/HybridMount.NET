using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>文件系统、真实 CFAPI 与本地 HTTP 联合回归；网络模拟不替代真实 DAV 提供商认证。</summary>
public sealed class RemoteSyncIntegrationTests
{
    [Fact(Timeout=30000)]
    public async Task FullRefreshUpdatesUnpinnedAndKeepsLocalEditsAndRemoteDeletionBarrier()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");
        test.Dav.Put("文件.bin",[7,8,9,10]);
        var refreshed=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));Assert.True(refreshed.State=="Completed",refreshed.Error);
        Assert.Equal(new byte[]{7,8,9,10},await File.ReadAllBytesAsync(path));
        await File.WriteAllBytesAsync(path,[4,5]);test.Dav.Put("文件.bin",[11,12,13]);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));Assert.Equal(new byte[]{4,5},await File.ReadAllBytesAsync(path));
        Assert.Contains(test.Repository.AllRemotePending(test.Id),x=>x.Path=="文件.bin"&&x.State=="Conflict");
        test.Dav.Objects.TryRemove("文件.bin",out _);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));Assert.Equal(new byte[]{4,5},await File.ReadAllBytesAsync(path));
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.False(test.Dav.Objects.ContainsKey("文件.bin"));
    }
    [Fact(Timeout=30000)]
    public async Task UnpinnedCachedFileCanBecomeEmptyWithoutKeepingOldBytes()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);
        test.Dav.Put("文件.bin",[]);var result=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));Assert.True(result.State=="Completed",result.Error);
        Assert.Empty(await File.ReadAllBytesAsync(path));Assert.Equal(0,new FileInfo(path).Length);Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);
    }
    [Theory(Timeout=30000)]
    [InlineData(0)][InlineData(10)][InlineData(100000)]
    public async Task ManualRefreshUpdatesPinnedThroughSnapshotAndRetainsIdentity(int length)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");
        var pin=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));Assert.True(pin.State=="Completed",pin.Error);
        var original=LocalFileState.Read(path);var bytes=Enumerable.Repeat((byte)77,length).ToArray();test.Dav.Put("文件.bin",bytes);
        var result=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));Assert.True(result.State=="Completed",result.Error);
        var pending=test.Repository.AllRemotePending(test.Id).Last(x=>x.Path=="文件.bin");Assert.True(pending.State=="Completed",pending.Error);
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path));Assert.Equal(original.Identity,LocalFileState.Read(path).Identity);
        Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target=="/dav/文件.bin");
        Assert.False(test.Repository.HasPendingRemote(test.Id,"文件.bin"));
    }
    [Fact(Timeout=30000)]
    public async Task CancelledSameRemoteVersionStaysCancelledAndExplicitRetryResumes()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);
        test.Dav.Put("文件.bin",[9]);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var prior=test.Repository.AllRemotePending(test.Id).Last(x=>x.Path=="文件.bin");
        test.Repository.SaveRemotePending(prior with{State="Canceled"});
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        Assert.Equal("Canceled",test.Repository.AllRemotePending(test.Id).Single(x=>x.Id==prior.Id).State);
        Assert.True(test.Manager.RetryUpload(prior.Id));
        for(var i=0;i<100&&test.Repository.AllRemotePending(test.Id).Single(x=>x.Id==prior.Id).State!="Completed";i++)await Task.Delay(20);
        var final=test.Repository.AllRemotePending(test.Id).Single(x=>x.Id==prior.Id);
        Assert.True(final.State=="Completed",System.Text.Json.JsonSerializer.Serialize(new{Pending=final,Native=CloudFilesNative.Inspect(Path.Combine(test.Local,"文件.bin")),Entry=test.Repository.Entries(test.Id).Single(x=>x.SourcePath=="文件.bin")}));
    }
    [Fact(Timeout=45000)]
    public async Task WeakUnchangedObservationProvesBaselineBeforeResumingLocalEdit()
    {
        var locks=new DavLockFixture();await using var dav=new SyncDavFixture(locks.Dav);dav.Dav.WeakEtag=true;
        dav.Dav.XmlTag=item=>"W/"+item.ETag;dav.Dav.HttpTag=item=>"W/"+item.ETag;
        var repository=dav.Repository();await using var logger=new LoggerService(Path.Combine(dav.Dav.Area,"echo-logs.db"));
        await using var manager=new MountManager(repository,logger);var local=Path.Combine(dav.Dav.Area,"echo-view");Directory.CreateDirectory(local);
        var mount=await manager.AddAsync("弱版本回显",local,false,source:dav.Dav.Options,credentials:new("user","app-password"));
        try
        {
            await manager.SetSchedulingForTestsAsync(mount.Id,false);await manager.SetRemoteDetectionAsync(mount.Id,false,60,300);
            var path=Path.Combine(local,"文件.bin");await File.ReadAllBytesAsync(path);
            var edited=new byte[]{91,92,93,94};await File.WriteAllBytesAsync(path,edited);await manager.ScanUploadChangesAsync(mount.Id);
            var original=repository.Uploads(mount.Id).Single(x=>x.LocalPath=="文件.bin");Assert.NotNull(original.BaselineSha256);
            // REPORT 明确报告同一弱标签与相同内容；只读摘要证明后才能迁移原基线。
            dav.Sequence++;await manager.DetectRemoteForTestsAsync(mount.Id);
            Assert.Equal(edited,await File.ReadAllBytesAsync(path));var updated=repository.Upload(original.Id)!;
            Assert.NotEqual(original.ExpectedVersion,updated.ExpectedVersion);Assert.Equal(original.BaselineSha256,updated.BaselineSha256);
            Assert.Equal(original.ObservedStamp,updated.ObservedStamp);Assert.NotEqual("Completed",updated.State);Assert.False(repository.HasPendingRemote(mount.Id,"文件.bin"));
            await manager.SetSchedulingForTestsAsync(mount.Id,true);
            for(var i=0;i<250&&repository.Upload(original.Id)?.State!="Completed";i++)await Task.Delay(40);
            Assert.True(repository.Upload(original.Id)?.State=="Completed",System.Text.Json.JsonSerializer.Serialize(repository.Upload(original.Id)));
            Assert.Equal(edited,dav.Dav.Objects["文件.bin"].Bytes);
        }
        finally{Assert.Equal("Completed",(await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"))).State);}
    }
    [Theory(Timeout=30000)]
    [InlineData(false)][InlineData(true)]
    public async Task EditingOrCancellationAfterSnapshotNeverOverwritesOldLocalContent(bool cancel)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));var old=await File.ReadAllBytesAsync(path);
        var entered=new TaskCompletionSource<RemotePendingChange>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.RemoteCheckpointForTests=async(stage,pending)=>{entered.TrySetResult(pending);await release.Task;};test.Dav.Put("文件.bin",[3,4,5]);
        var job=test.Manager.Queue(test.Id,"refresh");var pending=await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(cancel)Assert.True(test.Manager.Cancel(pending.Id));else await File.WriteAllBytesAsync(path,[97,98]);
        release.SetResult();await WebDavTests.Finished(test.Manager,job);
        Assert.Equal(cancel?old:new byte[]{97,98},await File.ReadAllBytesAsync(path));
        Assert.Equal(cancel?"Canceled":"Conflict",test.Repository.AllRemotePending(test.Id).Single(x=>x.Id==pending.Id).State);
        if(cancel)Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
        test.Manager.RemoteCheckpointForTests=null;
        if(cancel)
        {
            await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));Assert.Equal(old,await File.ReadAllBytesAsync(path));
            Assert.True(test.Manager.RetryUpload(pending.Id));
            for(var i=0;i<150&&test.Repository.AllRemotePending(test.Id).Single(x=>x.Id==pending.Id).State!="Completed";i++)await Task.Delay(20);
            Assert.Equal("Completed",test.Repository.AllRemotePending(test.Id).Single(x=>x.Id==pending.Id).State);Assert.Equal(new byte[]{3,4,5},await File.ReadAllBytesAsync(path));
        }
    }
    [Theory(Timeout=30000)]
    [InlineData(false)][InlineData(true)]
    public async Task RemoteTypeInterchangeStaysConflictWithoutChangingLocalType(bool directoryToFile)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var name=directoryToFile?"空目录":"文件.bin";var path=Path.Combine(test.Local,name);
        if(directoryToFile)test.Dav.Put(name,[5]);else test.Dav.Objects[name]=new([],"\"directory\"",true);
        var result=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));Assert.Equal("PartialFailure",result.State);
        Assert.Equal(directoryToFile,Directory.Exists(path));Assert.Equal(!directoryToFile,File.Exists(path));
        Assert.Contains(test.Repository.AllRemotePending(test.Id),x=>x.Path==name&&x.State=="Conflict");
        Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method is "PUT" or "MKCOL" or "MOVE" or "DELETE");
    }
    [Fact(Timeout=30000)]
    public async Task SettingsArePerMountAndDisconnectStopsBlockedReportWithoutErrors()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,71,601);
        var definition=test.Repository.LoadDefinitions().Single();Assert.False(definition.RemoteAutoDetectEnabled);Assert.Equal(71,definition.RemoteIncrementalSeconds);Assert.Equal(601,definition.RemoteFullSeconds);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var original=test.Dav.Server.Handler;
        test.Dav.Server.Handler=async request=>{var result=await original(request);if(request.Method=="PROPFIND"&&request.Headers.GetValueOrDefault("Depth")=="1")return result with{BeforeBody=new TaskCompletionSource().Task,HeadersSent=entered};return result;};
        var job=test.Manager.Queue(test.Id,"refresh");await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var started=DateTimeOffset.UtcNow;await Task.WhenAll(test.Manager.DisconnectAsync(test.Id),test.Manager.DisconnectAsync(test.Id));Assert.True(DateTimeOffset.UtcNow-started<TimeSpan.FromSeconds(5));
        Assert.Equal("未连接",test.Manager.Mounts.Single().Status);Assert.Equal("Canceled",(await WebDavTests.Finished(test.Manager,job)).State);
        Assert.DoesNotContain(await test.Logger.ReadAsync(),x=>x.Severity=="Error");
    }
}
