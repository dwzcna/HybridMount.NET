using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>清除只作用于成功展示历史，保留恢复／基线／并发重新入队，重启后不复现已清除的任务。</summary>
public sealed class CompletedTaskHistoryTests
{
    private static MountRepository Repository(string area)=>new(Path.Combine(area,"clear-data"),@"Software\HybridMount.NET.Tests\ClearHistory-"+Guid.NewGuid().ToString("N"));
    private static MountOperationSnapshot TaskRow(string id,string state)=>new(id,"upload",state,state=="Completed"?1:0,1,[],null);
    private static LocalUpload Upload(string id,Guid mount,string state="Completed")=>new(id,mount,id,id,false,"verified-stamp",1,state,DateTimeOffset.UtcNow,
        ExpectedVersion:"original-version",BaselineSha256:new string('A',64),Receipt:new("remote-"+id,id,false,3,DateTimeOffset.UtcNow,"receipt-version"));
    [Fact]
    public async Task Clear_all_completed_history_persists_and_preserves_every_other_state()
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);
        for(var i=0;i<150;i++)repository.SaveOperation(TaskRow("done-"+i,"Completed"));
        var states=new[]{"Queued","Running","Waiting","Sending","OutcomeUnknown","Conflict","Failed","PartialFailure","Canceled","Interrupted","Pending","Superseded","Ignored"};
        foreach(var state in states)repository.SaveOperation(TaskRow(state,state));
        Assert.Equal(150,repository.ClearCompletedOperations());Assert.Equal(0,repository.ClearCompletedOperations());
        var reopened=Repository(dav.Area);var remaining=reopened.LoadOperations();
        Assert.Equal(states.Length,remaining.Count);Assert.DoesNotContain(remaining,row=>row.State=="Completed");Assert.All(states,state=>Assert.Contains(remaining,row=>row.Id==state));
    }
    [Fact]
    public async Task Clearing_upload_history_keeps_verified_baselines_and_new_generations_visible()
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);var mount=Guid.NewGuid();
        var upload=Upload("completed",mount);repository.SaveUpload(upload);repository.SaveOperation(upload.ToSnapshot());
        var entry=new MountEntry("entry",upload.SourcePath,upload.LocalPath,"receipt-version",false,Materialized:true);
        repository.SaveEntry(mount,entry);repository.SaveBaseline(mount,new(upload.SourcePath,"receipt-version",new string('B',64)));
        Assert.Equal(1,repository.ClearCompletedOperations());Assert.Equal(upload,repository.Upload(upload.Id));Assert.Equal(entry,Assert.Single(repository.Entries(mount)));
        Assert.Equal(new string('B',64),repository.Baseline(mount,upload.SourcePath,"receipt-version"));
        await using var logger=new LoggerService(Path.Combine(dav.Area,"log.db"));await using var manager=new MountManager(repository,logger);
        Assert.DoesNotContain(manager.Operations,row=>row.Id==upload.Id);
        var next=repository.UpdateUpload(upload.Id,current=>current with{State="Queued",Generation=2});
        Assert.Empty(repository.LoadOperations());repository.SaveOperation(next!.ToSnapshot());
        Assert.Equal("Queued",Assert.Single(manager.Operations).State);Assert.DoesNotContain(repository.LoadOperations(),row=>row.State=="Completed");
    }
    [Theory]
    [InlineData("Queued")][InlineData("Sending")][InlineData("OutcomeUnknown")][InlineData("Conflict")]
    public async Task Stale_completed_history_cannot_remove_current_unfinished_upload(string state)
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);
        var upload=Upload("same-id",Guid.NewGuid(),state);repository.SaveUpload(upload);upload=repository.Upload(upload.Id)!;repository.SaveOperation(TaskRow(upload.Id,"Completed"));
        Assert.Equal(0,repository.ClearCompletedOperations());Assert.Equal(upload,repository.Upload(upload.Id));Assert.Single(repository.LoadOperations());
    }
    [Fact]
    public async Task Remote_recovery_evidence_is_preserved_and_blocks_stale_history_clear()
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);
        var pending=new RemotePendingChange("remote",Guid.NewGuid(),"scope","file.bin",null,State:"Hydrating",SnapshotPath:"recovery.snapshot",Sha256:new string('A',64));
        repository.SaveRemotePending(pending);repository.SaveOperation(TaskRow(pending.Id,"Completed"));
        Assert.Equal(0,repository.ClearCompletedOperations());Assert.Equal(pending,Assert.Single(repository.RemotePending(pending.MountId,pending.Scope)));
        repository.SaveRemotePending(pending with{State="Completed"});Assert.Equal(1,repository.ClearCompletedOperations());
        Assert.Single(repository.RemotePending(pending.MountId,pending.Scope));
    }
    [Fact]
    public async Task Concurrent_requeue_stays_visible_and_clear_is_owned_until_shutdown()
    {
        await using var dav=new DavFixture();var repository=Repository(dav.Area);
        await using var logger=new LoggerService(Path.Combine(dav.Area,"log.db"));var manager=new MountManager(repository,logger);var changed=0;manager.Changed+=()=>Interlocked.Increment(ref changed);
        try
        {
            for(var i=0;i<20;i++)
            {
                var upload=Upload("concurrent-"+i,Guid.NewGuid());repository.SaveUpload(upload);repository.SaveOperation(upload.ToSnapshot());
                await Task.WhenAll(manager.ClearCompletedOperationsAsync(),Task.Run(()=>
                {
                    var queued=repository.UpdateUpload(upload.Id,current=>current with{State="Queued",Generation=2});repository.SaveOperation(queued!.ToSnapshot());
                }));
                Assert.Contains(manager.Operations,row=>row.Id==upload.Id&&row.State=="Queued");
            }
            Assert.True(changed>=20);await manager.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>manager.ClearCompletedOperationsAsync());
        }
        finally{await manager.DisposeAsync();}
    }
    [Fact(Timeout=30000)]
    public async Task Clearing_completed_live_job_does_not_restore_it_from_the_memory_overlay()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);
        var entries=test.Repository.Entries(test.Id).ToArray();
        var id=test.Manager.Queue(test.Id,"refresh");var finished=await WebDavTests.Finished(test.Manager,id);Assert.Equal("Completed",finished.State);
        Assert.Contains(test.Manager.Operations,row=>row.Id==id);Assert.True(await test.Manager.ClearCompletedOperationsAsync()>0);
        Assert.DoesNotContain(test.Manager.Operations,row=>row.Id==id);Assert.Equal(entries,test.Repository.Entries(test.Id));
    }
}
