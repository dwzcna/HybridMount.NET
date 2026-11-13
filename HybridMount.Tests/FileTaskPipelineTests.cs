using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using HybridMount.Services.FileTasks;
namespace HybridMount.Tests;

/// <summary>隔离索引与执行器测试；依赖和未知结果不能因取消／恢复被误判成功。</summary>
public sealed class FileTaskPipelineTests
{
    [Fact]
    public void FreshCurrentNewerAndDamagedFormatsAreDistinctAndReadOnly()
    {
        using var area=new Area();Assert.Equal("New",MountIndexFormat.Inspect(area.Path).State);
        var repo=new MountRepository(area.Path);Assert.Equal("Current",MountIndexFormat.Inspect(area.Path).State);
        SetVersion(area.Path,99);var before=File.ReadAllBytes(Path.Combine(area.Path,"mounts.db"));Assert.Equal("Newer",MountIndexFormat.Inspect(area.Path).State);
        Assert.Throws<MountIndexFormatException>(()=>new MountRepository(area.Path));Assert.Equal(before,File.ReadAllBytes(Path.Combine(area.Path,"mounts.db")));
        File.WriteAllText(Path.Combine(area.Path,"mounts.db"),"damaged sqlite");Assert.Equal("Corrupt",MountIndexFormat.Inspect(area.Path).State);
    }
    [Theory]
    [InlineData(0)][InlineData(2)][InlineData(6)]
    public void OldFormatsAreRejectedWithoutMigrationOrDeletingEvidence(int version)
    {
        using var area=new Area();using(var db=Database(area.Path)){using var command=db.CreateCommand();command.CommandText="CREATE TABLE evidence(value TEXT);INSERT INTO evidence VALUES('keep')";command.ExecuteNonQuery();}
        SetVersion(area.Path,version);var path=Path.Combine(area.Path,"mounts.db");var before=SHA256.HashData(File.ReadAllBytes(path));
        Assert.Equal("Old",MountIndexFormat.Inspect(area.Path).State);Assert.Throws<MountIndexFormatException>(()=>new MountRepository(area.Path));
        Assert.Equal(before,SHA256.HashData(File.ReadAllBytes(path)));Assert.Single(Directory.GetFiles(area.Path));
    }
    [Fact]
    public async Task SameTaskHasOneRunnerAndVerificationOwnsCompletion()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);await using var logger=new LoggerService(Path.Combine(area.Path,"logs.db"));await using var dispatcher=new TaskDispatcher(repo,logger);
        var task=Record("file");var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        Task Run()=>dispatcher.ExecuteAsync(task,async token=>{Interlocked.Increment(ref calls);started.TrySetResult();await release.Task.WaitAsync(token);},_=>Task.FromResult(new TaskVerification("Completed")),default);
        var first=Run();await started.Task;var duplicates=Enumerable.Range(0,30).Select(_=>Run()).ToArray();Assert.Equal("Running",Assert.Single(repo.TaskSteps(task.Id)).State);
        release.SetResult();await Task.WhenAll(duplicates.Append(first));Assert.Equal(1,calls);Assert.Equal("Verified",Assert.Single(repo.TaskSteps(task.Id)).State);Assert.Equal("Completed",repo.ReadFileTask(task.Id)!.State);
    }
    [Theory]
    [InlineData("Failed")][InlineData("Canceled")][InlineData("OutcomeUnknown")][InlineData("Deferred")]
    public void DependencyRequiresVerifiedOutcome(string state)
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var task=Record("steps");repo.PrepareFileTask(task,["delete","upload"]);
        var attempt=repo.BeginTaskStep(task,0);repo.EndTaskStep(task,0,attempt,new(state));Assert.Throws<IOException>(()=>repo.BeginTaskStep(task,1));
        var retry=repo.BeginTaskStep(task,0);repo.EndTaskStep(task,0,retry,new("Completed"));Assert.NotEmpty(repo.BeginTaskStep(task,1));
    }
    [Fact]
    public void ParentTaskAndLateGenerationNeverReleaseCurrentChild()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var parent=Record("parent");var child=Record("parent/file") with{MountId=parent.MountId,Dependencies=[parent.Id]};
        repo.PrepareFileTask(parent,["mkdir"]);repo.PrepareFileTask(child,["upload"]);Assert.Throws<IOException>(()=>repo.BeginTaskStep(child,0));
        repo.SetFileTaskState(parent,"Completed","verify");Assert.NotEmpty(repo.BeginTaskStep(child,0));
        var replacement=child with{Generation=2,State="Ready"};repo.PrepareFileTask(replacement,["upload"]);repo.SetFileTaskState(child,"Completed","late");
        Assert.Equal(2,repo.ReadFileTask(child.Id)!.Generation);Assert.Equal("Ready",repo.ReadFileTask(child.Id)!.State);
    }
    [Fact]
    public async Task NewContentCancelsOldRunnerWithoutUserCancelOrConcurrentWrite()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);await using var logger=new LoggerService(Path.Combine(area.Path,"logs.db"));await using var dispatcher=new TaskDispatcher(repo,logger);
        var task=Record("editing");var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var active=0;var maximum=0;
        var old=dispatcher.ExecuteAsync(task,async token=>{maximum=Math.Max(maximum,Interlocked.Increment(ref active));started.SetResult();try{await Task.Delay(Timeout.Infinite,token);}finally{Interlocked.Decrement(ref active);}},_=>Task.FromResult(new TaskVerification("OutcomeUnknown")),default);
        await started.Task;dispatcher.ContentChanged(task.Id,2);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>old);
        await dispatcher.ExecuteAsync(task with{Generation=2},_=>{maximum=Math.Max(maximum,Interlocked.Increment(ref active));Interlocked.Decrement(ref active);return Task.CompletedTask;},_=>Task.FromResult(new TaskVerification("Completed")),default);
        Assert.Equal(1,maximum);Assert.Equal(2,repo.ReadFileTask(task.Id)!.Generation);Assert.Equal("Completed",repo.ReadFileTask(task.Id)!.State);
    }
    [Fact]
    public void IntentsAreDurableIdempotentAndFinalResultsNeedExactAcknowledgment()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var intent=Intent();Assert.Equal((true,false),repo.AcceptTaskIntent(intent));Assert.Equal((false,false),repo.AcceptTaskIntent(intent));
        Assert.Throws<InvalidDataException>(()=>repo.AcceptTaskIntent(intent with{Path="other"}));Assert.Throws<InvalidDataException>(()=>repo.AcceptTaskIntent(intent with{TaskId=Guid.NewGuid().ToString("N")}));
        var gap=intent with{TaskId=Guid.NewGuid().ToString("N"),Sequence=4};Assert.Equal((true,true),repo.AcceptTaskIntent(gap));
        repo.BindTaskIntent(intent,[]);var result=Assert.Single(repo.PendingTaskResults(intent.MountId));Assert.True(result.Final);Assert.Equal("NoOp",result.Outcome);
        var restarted=new MountRepository(area.Path);Assert.Equal(result,Assert.Single(restarted.PendingTaskResults(intent.MountId)));
        restarted.AcknowledgeTaskResult(Guid.NewGuid(),result.ResultId);Assert.Single(restarted.PendingTaskResults(intent.MountId));
        restarted.AcknowledgeTaskResult(intent.MountId,result.ResultId);Assert.Empty(restarted.PendingTaskResults(intent.MountId));
    }
    [Fact]
    public void TaskResultsCannotBeCompletedBeforeEveryBoundWorkflowAndRecoveryKeepsUnknown()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var intent=Intent();repo.AcceptTaskIntent(intent);var one=Record("one") with{MountId=intent.MountId};var two=Record("two") with{MountId=intent.MountId};
        repo.PrepareFileTask(one,["upload"]);repo.PrepareFileTask(two,["upload"]);repo.BindTaskIntent(intent,[one.Id,two.Id]);
        repo.SetFileTaskState(one,"Completed","verify");Assert.False(Assert.Single(repo.PendingTaskResults(intent.MountId)).Final);
        var attempt=repo.BeginTaskStep(two,0);repo.RecoverFileTaskAttempts();Assert.Equal("OutcomeUnknown",Assert.Single(repo.TaskSteps(two.Id)).State);
        repo.SetFileTaskState(two,"Completed","verify");Assert.True(Assert.Single(repo.PendingTaskResults(intent.MountId)).Final);
    }
    [Theory]
    [InlineData("../escape")][InlineData("C:/absolute")][InlineData("/absolute")][InlineData("dir\\file")][InlineData("dir//file")]
    public void InvalidIntentPathsAreRejectedWithoutFilesystemAccess(string path)=>Assert.Throws<InvalidDataException>(()=>{var intent=Intent() with{Path=path};intent.Validate();});
    [Fact]
    public void RepeatedObservationKeepsCancelledProtectedAndDependencyBarriers()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var mount=Guid.NewGuid();
        RemotePendingChange Change(string id,string state="Pending")=>new(id,mount,"scope",id,new(id,id,false,1,DateTimeOffset.UnixEpoch,"v"),State:state);
        foreach(var id in new[]{"same","canceled","protected","changed"})repo.SaveEntry(mount,new(id,id,id,"v",false,Protected:id=="protected",Materialized:true));
        var same=Change("same");var canceled=Change("canceled","Canceled");var protectedItem=Change("protected");var changed=Change("changed") with{Entry=new("changed","changed",false,2,DateTimeOffset.UnixEpoch,"new")};
        foreach(var item in new[]{same,canceled,protectedItem,changed})repo.SaveRemotePending(item);
        var complete=repo.CompleteRepeatedRemoteTasks(mount,[same,canceled,protectedItem,changed],(a,b)=>a==b);
        Assert.Equal(new[]{"same"},complete);Assert.Equal("Canceled",repo.FindRemotePending("canceled")!.State);Assert.Equal("Pending",repo.FindRemotePending("protected")!.State);Assert.Equal("Pending",repo.FindRemotePending("changed")!.State);
        Assert.Null(repo.ReadFileTask("same"));Assert.Single(repo.LoadOperations());
    }
    [Fact]
    public void StopBoundaryWaitsForMissingSequencesInsteadOfAcceptingTheLargestNumber()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);var first=Intent();var third=first with{TaskId=Guid.NewGuid().ToString("N"),Sequence=3};var second=first with{TaskId=Guid.NewGuid().ToString("N"),Sequence=2};
        Assert.True(repo.AcceptTaskIntent(third).Gap);Assert.Equal(0,repo.AcceptedTaskSequence(first.MountId,first.SessionId));
        repo.AcceptTaskIntent(first);Assert.Equal(1,repo.AcceptedTaskSequence(first.MountId,first.SessionId));repo.AcceptTaskIntent(second);Assert.Equal(3,repo.AcceptedTaskSequence(first.MountId,first.SessionId));
        Assert.True(repo.UnroutedTaskIntents(first.MountId).Single(item=>item.TaskId==third.TaskId).RescanRequired);
    }
    [Fact]
    public async Task StaleCandidateDoesNotExecuteOrStopAdmissionForNewGeneration()
    {
        using var area=new Area();var repo=new MountRepository(area.Path);await using var logger=new LoggerService(Path.Combine(area.Path,"logs.db"));await using var dispatcher=new TaskDispatcher(repo,logger);
        var task=Record("changed");repo.PrepareFileTask(task with{Generation=2},["upload"]);var calls=0;
        await dispatcher.ExecuteAsync(task,_=>{calls++;return Task.CompletedTask;},_=>Task.FromResult(new TaskVerification("Completed")),default);Assert.Equal(0,calls);
        await dispatcher.ExecuteAsync(task with{Generation=2},_=>{calls++;return Task.CompletedTask;},_=>Task.FromResult(new TaskVerification("Completed")),default);Assert.Equal(1,calls);Assert.Equal("Completed",repo.ReadFileTask(task.Id)!.State);
    }
    private static object? Validate(NativeTaskIntent intent){intent.Validate();return null;}
    private static FileTaskRecord Record(string path)=>new(Guid.NewGuid().ToString("N"),Guid.NewGuid(),path,"upload",1,"Ready",Guid.NewGuid().ToString("N"),DateTimeOffset.UtcNow);
    private static NativeTaskIntent Intent()=>new(Guid.NewGuid().ToString("N"),Guid.NewGuid(),Guid.NewGuid().ToString("N"),1,"changed","file");
    private static SqliteConnection Database(string directory){var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"mounts.db"),Pooling=false}.ToString());db.Open();return db;}
    private static void SetVersion(string directory,int version){using var db=Database(directory);using var command=db.CreateCommand();command.CommandText="PRAGMA user_version="+version;command.ExecuteNonQuery();}
    private sealed class Area:IDisposable
    {
        internal string Path {get;}=Directory.CreateTempSubdirectory("HybridMount.TaskPipeline.").FullName;
        public void Dispose(){if(!System.IO.Path.GetFullPath(Path).StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HybridMount.TaskPipeline."),StringComparison.OrdinalIgnoreCase))throw new IOException("测试根越界。");Directory.Delete(Path,true);}
    }
}
