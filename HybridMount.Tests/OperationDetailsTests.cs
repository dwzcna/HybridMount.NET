using Microsoft.Data.Sqlite;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>展示证据、升级和逻辑批次的隔离回归；不会读取正式配置或使用正式挂载。</summary>
public sealed class OperationDetailsTests
{
    [Fact]
    public void UpgradeKeepsBusinessDataAndCreatesConsistentVersionSevenBackup()
    {
        using var area=new Area();var repository=new MountRepository(area.Path);repository.SaveOperation(new("old","refresh","Completed",1,1,[],null));
        MakeSeven(area.Path);Assert.Equal("Upgradeable",MountIndexFormat.Inspect(area.Path).State);
        var upgraded=new MountRepository(area.Path);Assert.Equal("Current",MountIndexFormat.Inspect(area.Path).State);Assert.Equal("old",upgraded.FindOperation("old")!.Id);
        var backup=Assert.Single(Directory.GetFiles(Path.Combine(area.Path,"Backups"),"*.db"));using var connection=Open(backup);using var query=connection.CreateCommand();query.CommandText="PRAGMA user_version";Assert.Equal(7L,query.ExecuteScalar());query.CommandText="SELECT id FROM operations";Assert.Equal("old",query.ExecuteScalar());
    }
    [Fact]
    public void FailedUpgradeRollsBackAndRetainsEvidenceAndBackup()
    {
        using var area=new Area();_=new MountRepository(area.Path);MakeSeven(area.Path);
        using(var db=Open(Path.Combine(area.Path,"mounts.db"))){using var command=db.CreateCommand();command.CommandText="CREATE VIEW operation_runs AS SELECT 1 AS id";command.ExecuteNonQuery();}
        Assert.Throws<SqliteException>(()=>new MountRepository(area.Path));
        using(var db=Open(Path.Combine(area.Path,"mounts.db"))){using var command=db.CreateCommand();command.CommandText="PRAGMA user_version";Assert.Equal(7L,command.ExecuteScalar());command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='operation_steps'";Assert.Equal(0L,command.ExecuteScalar());}
        Assert.Single(Directory.GetFiles(Path.Combine(area.Path,"Backups"),"*.db"));
    }
    [Fact]
    public void FailureDoesNotCompleteUnexecutedStepsAndRetryPreservesAttempt()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);
        repo.BeginOperationRun("task","g1",["OperationStagePrepareSnapshot","OperationStageTransfer","OperationStageVerifyContent"]);repo.AdvanceOperation("task","OperationStagePrepareSnapshot");repo.AdvanceOperation("task","OperationStageTransfer");repo.EndOperationRun("task","Failed","server failed");
        var first=Assert.Single(repo.OperationRuns("task"));Assert.Equal(new[]{"Completed","Failed","Pending"},first.Steps.Select(step=>step.State));Assert.Equal("server failed",first.Steps[2].Reason);
        repo.BeginOperationRun("task","g1",["OperationStagePrepareSnapshot","OperationStageTransfer","OperationStageVerifyContent"],retry:true);repo.AdvanceOperation("task","OperationStageVerifyContent");repo.EndOperationRun("task","Completed");
        var runs=repo.OperationRuns("task");Assert.Equal(2,runs.Count);Assert.Equal("Failed",runs[1].State);Assert.Equal("Pending",runs[0].Steps[0].State);Assert.Equal("Completed",runs[0].Steps[2].State);
        Assert.Empty(repo.TaskSteps("task"));
    }
    [Fact]
    public void BatchesKeepLogicalRoundAndIndependentDownloadCancellation()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();var detection=new RemoteDetection("round",mount,"scope","connection");
        for(var page=0;page<3;page++)repo.CommitDiscoveredBatch(mount,"scope",new(""),Enumerable.Range(page*256,256).Select(i=>new RemoteShadowEntry(new(i.ToString(),"file-"+i,false,1,DateTimeOffset.UnixEpoch,"v1"))).ToArray(),[],detection:detection);
        var first=Assert.Single(repo.RemoteBatches(mount));Assert.Equal(768,first.Total);Assert.Equal("Running",first.State);
        var member=repo.RemoteBatchMembers(first.Id).First();var download=repo.FindRemotePending(member.PendingId)! with{DownloadTask=true};repo.SaveRemotePending(download);
        Assert.Equal(767,repo.FindRemoteBatch(first.Id)!.Total);var ids=repo.ChangeRemoteBatch(first.Id,false);Assert.Equal(767,ids.Count);Assert.Equal("Pending",repo.FindRemotePending(download.Id)!.State);
        repo.CommitDiscoveredBatch(mount,"scope",new(""),[new(new("new","later",false,1,DateTimeOffset.UnixEpoch,"v1"))],[],detection:detection);Assert.Equal("Canceled",repo.AllRemotePending(mount).Single(item=>item.Path=="later").State);
        repo.FinishRemoteDetection(first.Id,"Completed");Assert.Equal("Canceled",repo.FindRemoteBatch(first.Id)!.State);
        Assert.Equal(768,repo.ChangeRemoteBatch(first.Id,true).Count);Assert.Equal("Pending",repo.FindRemotePending(member.PendingId)!.State);
        Assert.Equal(256,repo.RemoteBatchMembers(first.Id).Count);Assert.Equal(256,repo.RemoteBatchMembers(first.Id,256).Count);
        Assert.Equal(0,repo.ClearOperations(TaskHistoryClearMode.All));
    }
    [Fact]
    public void CancelBeforeClassificationDoesNotCancelFutureIndependentDownload()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();
        foreach(var path in new[]{"hot","cold"})repo.SaveEntry(mount,new(path,path,path,"v0",false,Materialized:true));
        repo.CommitDiscoveredBatch(mount,"scope",new(""),new[]{"hot","cold"}.Select(path=>new RemoteShadowEntry(new(path,path,false,1,DateTimeOffset.UnixEpoch,"v1"))).ToArray(),[],detection:new("round",mount,"scope","connection"));
        Assert.Empty(repo.ChangeRemoteBatch("round",false));repo.FinishRemoteDetection("round","Completed");
        var hot=repo.AllRemotePending(mount).Single(item=>item.Path=="hot");var cold=repo.AllRemotePending(mount).Single(item=>item.Path=="cold");
        Assert.Equal("Pending",repo.ClassifyRemotePending(hot,true).State);Assert.True(repo.FindRemotePending(hot.Id)!.DownloadTask);
        Assert.Equal("Canceled",repo.ClassifyRemotePending(cold,false).State);Assert.Equal(1,repo.FindRemoteBatch("round")!.Total);Assert.Equal("Canceled",repo.FindRemoteBatch("round")!.State);
    }
    [Fact]
    public void HistoryPagingAndClearingKeepMemberOwnershipAfterIndexRemoval()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);
        for(var i=0;i<70;i++){repo.BeginOperationRun("many","generation",["OperationStageExecute"],retry:true);repo.AdvanceOperation("many","OperationStageExecute");repo.EndOperationRun("many","Failed","failure");}
        Assert.Equal(70,repo.OperationRunCount("many"));Assert.Equal(64,repo.OperationRuns("many").Count);Assert.Equal(6,repo.OperationRuns("many",64).Count);
        var mount=Guid.NewGuid();repo.CommitDiscoveredBatch(mount,"scope",new(""),[new(new("id","file",false,1,DateTimeOffset.UnixEpoch,"v1"))],[],detection:new("round",mount,"scope","connection"));
        var pending=Assert.Single(repo.AllRemotePending(mount));repo.SaveRemotePending(pending with{State="Completed"});repo.FinishRemoteDetection("round","Completed");repo.DeleteEntries(mount);
        Assert.Equal("Completed",repo.FindRemoteBatch("round")!.State);Assert.Equal("Completed",Assert.Single(repo.RemoteBatchMembers("round")).State);
        Assert.Equal(1,repo.ClearOperations(TaskHistoryClearMode.Completed));Assert.Null(repo.FindRemoteBatch("round"));Assert.Contains(pending.Id,repo.ReadTaskState().BatchedRemoteIds);
    }
    [Theory(Timeout=60000)]
    [InlineData(false,false)][InlineData(true,false)][InlineData(true,true)]
    public async Task FixedExamplesGroupColdChangesAndKeepHotDownloadSeparate(bool modifyCold,bool modifyHot)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);
        foreach(var name in new[]{"a","c","e"})test.Dav.Put(name,[1,2,3]);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        if(modifyHot){await File.ReadAllBytesAsync(Path.Combine(test.Local,"e"));await Task.Delay(80);}
        var before=test.Repository.RemoteBatches(test.Id).Select(item=>item.Id).ToHashSet();var getCount=test.Dav.Server.Requests.Count(item=>item.Method=="GET");
        foreach(var name in modifyCold?new[]{"b","d"}:new[]{"new-a","b","new-c","d"})test.Dav.Put(name,[8,9]);
        if(modifyCold)foreach(var name in new[]{"a","c"})test.Dav.Put(name,[7,8,9,10]);
        if(modifyHot)test.Dav.Put("e",[11,12,13,14]);
        var refresh=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));Assert.True(refresh.State=="Completed",refresh.Error);
        var batch=Assert.Single(test.Repository.RemoteBatches(test.Id),item=>!before.Contains(item.Id));Assert.Equal(4,batch.Total);Assert.Equal(4,batch.Completed);
        var members=test.Repository.RemoteBatchMembers(batch.Id);Assert.Equal(4,members.Count);
        Assert.DoesNotContain(test.Manager.Operations,item=>members.Any(member=>member.PendingId==item.Id));
        if(modifyHot)
        {
            var pending=test.Repository.AllRemotePending(test.Id).Last(item=>item.Path=="e");Assert.True(pending.DownloadTask);Assert.Equal("download",test.Manager.GetOperation(pending.Id)!.Operation);Assert.Equal(new byte[]{11,12,13,14},await File.ReadAllBytesAsync(Path.Combine(test.Local,"e")));
            Assert.Equal(0u,CloudFilesNative.Inspect(Path.Combine(test.Local,"e"))!.Value.Info.PinState);
        }
        else Assert.Equal(getCount,test.Dav.Server.Requests.Count(item=>item.Method=="GET"));
    }
    [Theory]
    [InlineData(0,0,true,true)][InlineData(0,0,false,false)][InlineData(10,0,false,true)][InlineData(0,10,false,true)]
    public void ClassificationUsesActualRangesAndCompleteState(long onDisk,long validated,bool full,bool download)
    {
        var snapshot=new NativeFileSnapshot(new("file",0,10,0,1,false,1),0,0,0x11,new(onDisk,0,1,1,0,1,[],validated),full);
        Assert.Equal(download,MountManager.RequiresRemoteDownload(snapshot));
    }
    [Fact]
    public void ZeroByteClassificationUsesPlaceholderStateAndLateAttemptsCannotCommit()
    {
        var state=new NativeFileSnapshot(new("zero",0,0,0,0,false),0,0,0x11,new(0,0,1,1,0,1,[]),true);
        Assert.False(MountManager.RequiresRemoteDownload(state));Assert.True(MountManager.RequiresRemoteDownload(state with{State=1}));
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();var pending=new RemotePendingChange("task",mount,"scope","file",new("file","file",false,1,DateTimeOffset.UnixEpoch,"v1"),AttemptId:"first",AttemptGeneration:1);
        repo.SaveRemotePending(pending);var next=pending with{AttemptId="second",AttemptGeneration=2};repo.SaveRemotePending(next);
        Assert.Equal("second",repo.SaveRemotePendingCurrent(pending with{State="Prepared"}).AttemptId);
        Assert.Throws<SourceConflictException>(()=>repo.CommitRemotePlaceholder(pending with{State="Completed"},null));
    }
    [Fact(Timeout=45000)]
    public async Task ExplorerStyleReadHasConfirmedStagesAndDetailsDoNotReadSource()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);
        var content=await File.ReadAllBytesAsync(Path.Combine(test.Local,"文件.bin"));
        MountOperationSnapshot[] reads=[];var deadline=DateTime.UtcNow.AddSeconds(5);
        while(DateTime.UtcNow<deadline)
        {
            reads=test.Repository.LoadOperations().Where(item=>item.Operation=="download"&&item.ItemPath=="文件.bin").ToArray();
            if(reads.Length>0&&reads.All(item=>item.State=="Completed"))break;await Task.Delay(20);
        }
        Assert.NotEmpty(reads);Assert.All(reads,item=>Assert.Equal("Completed",item.State));Assert.True(reads.Sum(item=>item.BytesTransferred)>=content.Length);
        var count=test.Dav.Server.Requests.Count;
        foreach(var read in reads){var details=test.Manager.TasksQueue.ReadOperationDetails(read.Id)!;Assert.All(Assert.Single(details.Runs).Steps,step=>Assert.Equal("Completed",step.State));}
        Assert.Equal(count,test.Dav.Server.Requests.Count);
    }
    [Fact(Timeout=45000)]
    public async Task ProtectedColdRefreshRejectsCachedContentWithoutDiscardingIt()
    {
        await using var dav=new DavFixture();var path=Path.Combine(dav.Area,"guard-view");Directory.CreateDirectory(path);var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");var repo=new MountRepository(Path.Combine(dav.Area,"guard-data"),registry);await using var logger=new LoggerService(Path.Combine(dav.Area,"guard-logs.db"));
        var mount=new MountDefinition(Guid.NewGuid(),"guard",path,RootIdentity:MountPathGuard.FileIdentity(path),Source:dav.Options,RegistrationStage:MountRegistrationStage.NotRegistered);repo.SaveDefinition(mount);new CredentialStore(repo.DataDirectory).Save(mount.Id,new("user","app-password"));
        try
        {
            await CloudRootRegistration.EnsureAsync(mount,default);
            await using(var source=dav.Source())await using(var session=new CloudMountSession(mount,source,repo,logger))
            {
                session.Connect();var initial=await source.GetMetadataAsync("文件.bin");var entry=new MountEntry("guard",initial.Path,initial.Path,initial.Version,false);repo.SaveEntry(mount.Id,entry);session.CreatePlaceholder(entry,initial);entry=repo.EntryById(mount.Id,entry.Id)!;
                var file=Path.Combine(path,"文件.bin");var bytes=await File.ReadAllBytesAsync(file);for(var i=0;session.IsActive(entry.Id)&&i<100;i++)await Task.Delay(10);
                var gets=dav.Server.Requests.Count(item=>item.Method=="GET");var identity=LocalFileState.Read(file).Identity;
                Assert.False(session.RefreshColdPlaceholder(entry,initial with{Size=initial.Size+1}));Assert.Equal(bytes,await File.ReadAllBytesAsync(file));Assert.Equal(identity,LocalFileState.Read(file).Identity);Assert.Equal(gets,dav.Server.Requests.Count(item=>item.Method=="GET"));
            }
        }
        finally{await using var cleanup=new MountManager(repo,logger);Assert.Equal("Completed",(await WebDavTests.Finished(cleanup,cleanup.Queue(mount.Id,"clearAndRemove"))).State);Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
    private static void MakeSeven(string directory)
    {using var db=Open(Path.Combine(directory,"mounts.db"));using var command=db.CreateCommand();command.CommandText="DROP TABLE operation_steps;DROP TABLE operation_runs;DROP TABLE remote_task_batch_members;DROP TABLE remote_task_batches;PRAGMA user_version=7";command.ExecuteNonQuery();}
    private static SqliteConnection Open(string path){var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());db.Open();MountDatabase.ConfigurePathComparison(db);return db;}
    private sealed class Area:IDisposable
    {internal string Path{get;}=Directory.CreateTempSubdirectory("HybridMount.OperationDetails.").FullName;public void Dispose(){Directory.Delete(Path,true);}}
}
