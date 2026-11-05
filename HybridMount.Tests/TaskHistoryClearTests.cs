using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>失败与全部历史清理：验证事务边界、恢复证据、重启、活动任务及并发重排队，不接触正式配置。</summary>
public sealed class TaskHistoryClearTests
{
    private static MountRepository Repository(string area)=>new(Path.Combine(area,"history-data"),@"Software\HybridMount.NET.Tests\History-"+Guid.NewGuid().ToString("N"));
    private static MountOperationSnapshot Row(string id,string state)=>new(id,"upload",state,0,1,[],null);
    private static LocalUpload Upload(string id,string state)=>new(id,Guid.NewGuid(),id,id,false,"stamp",1,state,DateTimeOffset.UtcNow,ExpectedVersion:"baseline",BaselineSha256:new string('A',64));
    [Fact]
    public async Task Failed_includes_partial_failure_and_clears_beyond_visible_limit_after_reopen()
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);
        for(var i=0;i<150;i++)repository.SaveOperation(Row("failed-"+i,i%2==0?"Failed":"PartialFailure"));
        foreach(var state in new[]{"Completed","Canceled","Interrupted","Queued","Running","Waiting","Conflict","OutcomeUnknown","Unrecognized"})repository.SaveOperation(Row(state,state));
        Assert.Throws<ArgumentOutOfRangeException>(()=>repository.ClearOperations((TaskHistoryClearMode)99));
        Assert.Equal(150,repository.ClearOperations(TaskHistoryClearMode.Failed));Assert.Equal(0,repository.ClearOperations(TaskHistoryClearMode.Failed));
        var reopened=Repository(dav.Area);Assert.Equal(9,reopened.LoadOperations().Count);Assert.DoesNotContain(reopened.LoadOperations(),row=>TaskHistoryRules.Matches(TaskHistoryClearMode.Failed,row.State));
    }
    [Fact]
    public async Task All_keeps_upload_evidence_snapshots_and_baselines_and_does_not_restore_cleared_failure()
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);var path=Path.Combine(dav.Area,"immutable.snapshot");await File.WriteAllTextAsync(path,"evidence");
        var failed=Upload("failed-upload","Failed") with{SnapshotPath=path,SnapshotSha256=new string('B',64)};repository.SaveUpload(failed);repository.SaveOperation(failed.ToSnapshot());
        var entry=new MountEntry("entry",failed.SourcePath,failed.LocalPath,"baseline",false,Protected:true);repository.SaveEntry(failed.MountId,entry);repository.SaveBaseline(failed.MountId,new(failed.SourcePath,"baseline",new string('C',64)));
        foreach(var state in new[]{"Completed","PartialFailure","Canceled","Interrupted","Ignored","Superseded"})repository.SaveOperation(Row(state,state));
        repository.SaveOperation(Row("conflict","Conflict"));repository.SaveOperation(Row("unknown","OutcomeUnknown"));
        Assert.Equal(7,repository.ClearOperations(TaskHistoryClearMode.All));Assert.Equal(failed,repository.Upload(failed.Id));Assert.Equal(entry,Assert.Single(repository.Entries(failed.MountId)));Assert.Equal("evidence",await File.ReadAllTextAsync(path));Assert.Equal(new string('C',64),repository.Baseline(failed.MountId,failed.SourcePath,"baseline"));
        await using var logger=new LoggerService(Path.Combine(dav.Area,"log.db"));await using var manager=new MountManager(Repository(dav.Area),logger);
        Assert.DoesNotContain(manager.Operations,row=>row.Id==failed.Id);Assert.Equal(2,manager.Operations.Count);
        var queued=repository.UpdateUpload(failed.Id,item=>item with{State="Queued",Generation=2})!;repository.SaveOperation(queued.ToSnapshot());Assert.Contains(manager.Operations,row=>row.Id==failed.Id&&row.State=="Queued");
    }
    [Fact]
    public async Task Stale_failed_history_cannot_hide_current_upload_or_unknown_state()
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);
        foreach(var state in new[]{"Queued","Prepared","Sending","RemoteCommitted","WaitingCapacity","Conflict","OutcomeUnknown","Unrecognized"})
        {
            var upload=Upload(state,state);repository.SaveUpload(upload);repository.SaveOperation(Row(upload.Id,"Failed"));
            Assert.Equal(0,repository.ClearOperations(TaskHistoryClearMode.Failed));Assert.Equal(0,repository.ClearOperations(TaskHistoryClearMode.All));Assert.Equal(upload,repository.Upload(upload.Id));
        }
        Assert.Equal(8,repository.LoadOperations().Count);
    }
    [Fact]
    public async Task Unfinished_remote_recovery_is_not_cleared_even_when_history_says_failed()
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);var changes=new List<RemotePendingChange>();
        foreach(var state in new[]{"Pending","Downloading","Committing","Hydrating","Failed","Conflict","Waiting","Unrecognized",null!})
        {
            var change=new RemotePendingChange(state??"null-state",Guid.NewGuid(),"scope","file",null,State:state!,SnapshotPath:"evidence",Sha256:new string('A',64));changes.Add(change);repository.SaveRemotePending(change);repository.SaveOperation(Row(change.Id,"Failed"));
            Assert.Equal(0,repository.ClearOperations(TaskHistoryClearMode.Failed));Assert.Equal(0,repository.ClearOperations(TaskHistoryClearMode.All));Assert.Equal(change,Assert.Single(repository.RemotePending(change.MountId,change.Scope)));
        }
        var finished=changes[0] with{State="Completed"};repository.SaveRemotePending(finished);Assert.Equal(1,repository.ClearOperations(TaskHistoryClearMode.All));Assert.Equal(finished,Assert.Single(repository.RemotePending(finished.MountId,finished.Scope)));
    }
    [Fact]
    public async Task Requeue_races_remain_visible_and_shutdown_rejects_late_clear()
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);await using var logger=new LoggerService(Path.Combine(dav.Area,"log.db"));var manager=new MountManager(repository,logger);
        try
        {
            foreach(var mode in new[]{TaskHistoryClearMode.Failed,TaskHistoryClearMode.All})for(var i=0;i<10;i++)
            {
                var upload=Upload(mode+"-"+i,"Failed");repository.SaveUpload(upload);repository.SaveOperation(upload.ToSnapshot());
                await Task.WhenAll(manager.ClearOperationHistoryAsync(mode),Task.Run(()=>{var queued=repository.UpdateUpload(upload.Id,item=>item with{State="Queued",Generation=2})!;repository.SaveOperation(queued.ToSnapshot());}));
                Assert.Contains(manager.Operations,row=>row.Id==upload.Id&&row.State=="Queued");
            }
            await manager.DisposeAsync();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>manager.ClearOperationHistoryAsync(TaskHistoryClearMode.All));
        }
        finally{await manager.DisposeAsync();}
    }
    [Fact(Timeout=40000)]
    public async Task All_preserves_live_job_and_clears_old_interrupted_rows_without_replay()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);
        var handler=test.Dav.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Dav.Server.Handler=async request=>{if(request.Method=="PROPFIND"){entered.TrySetResult();await release.Task;}return await handler(request);};
        var id=test.Manager.Queue(test.Id,"refresh");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));test.Repository.SaveOperation(Row("orphan-queued","Queued"));test.Repository.SaveOperation(Row("orphan-running","Running"));
            // 连接夹具可能已有完成历史，使用清理前的精确ID集合核对，不假设这里只有两个旧条目。
            var historyIds=test.Manager.Operations.Where(row=>row.Id!=id).Select(row=>row.Id).ToHashSet();
            Assert.Equal(historyIds.Count,await test.Manager.ClearOperationHistoryAsync(TaskHistoryClearMode.All));
            Assert.All(historyIds,old=>Assert.DoesNotContain(test.Manager.Operations,row=>row.Id==old));
            Assert.Contains(test.Manager.Operations,row=>row.Id==id&&row.State is "Queued" or "Running");Assert.Contains(test.Repository.LoadOperations(),row=>row.Id==id);
        }
        finally{release.TrySetResult();test.Dav.Server.Handler=handler;}
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,id)).State);
    }
}
