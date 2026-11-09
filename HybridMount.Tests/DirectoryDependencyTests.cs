using HybridMount.Services.Mounting;
using HybridMount.Models.Mounting;
namespace HybridMount.Tests;

/// <summary>真实隔离CFAPI根和回环DAV验证父目录元数据不会封锁子文件，取消的真实内容任务仍保持屏障。</summary>
public sealed class DirectoryDependencyTests
{
    [Fact(Timeout=60000)]
    public async Task DirectoryMetadataDoesNotTurnChildUploadIntoConflict()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);
        test.Dav.Put("空目录/child.bin",[1,2,3]);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var path=Path.Combine(test.Local,"空目录","child.bin");await File.ReadAllBytesAsync(path);await File.WriteAllBytesAsync(path,[8,9]);await test.Manager.ScanUploadChangesAsync(test.Id);
        var upload=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="空目录/child.bin");
        test.Dav.Objects["空目录"]=new([],"\"new-directory\"",true);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        Assert.DoesNotContain(test.Repository.AllRemotePending(test.Id),x=>x.Path=="空目录"&&x.State is "Conflict" or "Waiting");
        Assert.False(test.Repository.HasPendingRemote(test.Id,upload.SourcePath));Assert.Equal("Queued",test.Repository.Upload(upload.Id)!.State);
        Assert.Equal(new byte[]{8,9},await File.ReadAllBytesAsync(path));await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        await test.Wait(upload.LocalPath,bytes:[8,9]);
    }
    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task CanceledDirectoryCanBeProvedHarmlessButDeletionKeepsBarrier(bool deletion)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var scope=test.Repository.AllRemotePending(test.Id).First().Scope;var entry=test.Repository.Entries(test.Id).Single(x=>x.SourcePath=="空目录");
        await using var source=test.Dav.Source();var metadata=await source.GetMetadataAsync("空目录");
        var pending=new RemotePendingChange(Guid.NewGuid().ToString("N"),test.Id,scope,"空目录",deletion?null:metadata,State:"Canceled",Error:"用户取消");test.Repository.SaveRemotePending(pending);
        Assert.True(test.Repository.HasPendingRemote(test.Id,"空目录/child.bin"));await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var current=test.Repository.AllRemotePending(test.Id).Single(x=>x.Id==pending.Id);Assert.Equal("Canceled",current.State);Assert.Equal(!deletion,current.MetadataOnly);
        Assert.Equal(deletion,test.Repository.HasPendingRemote(test.Id,"空目录/child.bin"));Assert.Equal(entry.Version,test.Repository.Entries(test.Id).Single(x=>x.Id==entry.Id).Version);
        await test.Manager.ClearOperationHistoryAsync(TaskHistoryClearMode.All);
        if(deletion)Assert.Contains(test.Manager.Operations,x=>x.Id==pending.Id);
        Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method is "PUT" or "MOVE" or "DELETE");
    }
    [Fact(Timeout=60000)]
    public async Task WaitingReasonSurvivesHistoryClearAndRecheckDoesNotSend()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);
        var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);await File.WriteAllBytesAsync(path,[5,6]);await test.Manager.ScanUploadChangesAsync(test.Id);
        var upload=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="文件.bin");var barrier=new RemotePendingChange(Guid.NewGuid().ToString("N"),test.Id,"proof",upload.SourcePath,null,State:"Canceled",Error:"请核对删除");test.Repository.SaveRemotePending(barrier);
        await test.Manager.ClearOperationHistoryAsync(TaskHistoryClearMode.All);var shown=test.Manager.GetOperation(upload.Id)!;
        Assert.Equal(barrier.Id,shown.WaitingTaskId);Assert.Contains("请核对删除",shown.DisplayWaitDetails);Assert.Contains(test.Manager.Operations,x=>x.Id==barrier.Id);
        Assert.True(test.Manager.RecheckOperation(upload.Id));
        var until=DateTime.UtcNow.AddSeconds(5);while(test.Manager.GetOperation(upload.Id)!.InspectionMessage?.StartsWith("只读核对完成")!=true&&DateTime.UtcNow<until)await Task.Delay(30);
        Assert.StartsWith("只读核对完成",test.Manager.GetOperation(upload.Id)!.InspectionMessage);Assert.Equal("Queued",test.Repository.Upload(upload.Id)!.State);Assert.True(test.Repository.HasPendingRemote(test.Id,upload.SourcePath));
        Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method is "PUT" or "MOVE" or "DELETE");
    }
}
