using Microsoft.Data.Sqlite;
namespace HybridMount.Tests;

/// <summary>瞬时业务失败仅在所属挂载重试；故障饱和与非法协议分别验证。全部使用独立宿主和临时索引。</summary>
[Collection("Native IPC")]
public sealed class NativeNotificationFailureTests
{
    [Fact(Timeout=20000)]
    public async Task TransientBusinessFailureRetriesAndOtherMountKeepsWorking()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();
        await using var slow=new CloudMountSession(new(Guid.NewGuid(),"slow",area.Path,false),null,repo,logger);slow.AttachNotificationRuntimeForTests(runtime);
        await using var other=new CloudMountSession(new(Guid.NewGuid(),"other",area.Path,false),null,repo,logger);other.AttachNotificationRuntimeForTests(runtime);
        var attempts=0;var finished=0;var fast=0;var failed=false;slow.RuntimeFailed+=_=>failed=true;
        slow.QueueNotificationForTests(()=>{if(Interlocked.Increment(ref attempts)<3)throw new SqliteException("busy",5);finished++;});other.QueueNotificationForTests(()=>fast++);
        await other.DrainNotificationsForTests().WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(1,fast);await slow.DrainNotificationsForTests().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3,attempts);Assert.Equal(1,finished);Assert.Equal(2,slow.NotificationRetriesForTests);Assert.False(failed);Assert.True(runtime.IsAvailable);Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
    }
    [Fact(Timeout=20000)]
    public async Task PersistentFailureStopsOnlyItsMountAndRejectsLaterActions()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();
        await using var broken=new CloudMountSession(new(Guid.NewGuid(),"broken",area.Path,false),null,repo,logger);broken.AttachNotificationRuntimeForTests(runtime);
        await using var other=new CloudMountSession(new(Guid.NewGuid(),"other",area.Path,false),null,repo,logger);other.AttachNotificationRuntimeForTests(runtime);
        var failed=new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);var attempts=0;broken.RuntimeFailed+=error=>failed.TrySetResult(error);
        broken.QueueNotificationForTests(()=>{Interlocked.Increment(ref attempts);throw new IOException("temporary database I/O");});await failed.Task.WaitAsync(TimeSpan.FromSeconds(8));await broken.DrainNotificationsForTests();
        Assert.Equal(4,attempts);Assert.Equal(3,broken.NotificationRetriesForTests);var after=0;broken.QueueNotificationForTests(()=>after++);Assert.Equal(0,after);
        var fast=0;other.QueueNotificationForTests(()=>fast++);await other.DrainNotificationsForTests();Assert.Equal(1,fast);Assert.True(runtime.IsAvailable);Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
    }
    [Fact(Timeout=20000)]
    public async Task SaturatedMountDoesNotDestroySharedRuntimeOrRunUncertainTail()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();await using var session=new CloudMountSession(new(Guid.NewGuid(),"full",area.Path,false),null,repo,logger);session.AttachNotificationRuntimeForTests(runtime);
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);session.RuntimeFailed+=_=>failed.TrySetResult();var later=0;
        try
        {
            session.QueueNotificationForTests(()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));});await entered.Task;
            for(var index=0;index<300;index++)session.QueueNotificationForTests(()=>later++);Assert.InRange(session.PendingNotificationsForTests,0,256);await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.True(runtime.IsAvailable);
            release.Set();await session.DrainNotificationsForTests();Assert.Equal(0,later);Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
        }
        finally{release.Set();}
    }
    [Fact(Timeout=20000)]
    public async Task RenameTransactionRollsBackAndRetriesWholeSubtreeBeforePublishing()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));var mount=new MountDefinition(Guid.NewGuid(),"rename",area.Path,false);
        repo.SaveEntry(mount.Id,new("folder","É","É","v",true));repo.SaveEntry(mount.Id,new("file","É/file","É/file","v",false));repo.SaveEntry(mount.Id,new("sibling","Éx/file","Éx/file","v",false));
        repo.NativeNotificationCommittingForTests=()=>throw new SqliteException("busy",5);Assert.Throws<SqliteException>(()=>repo.ApplyNativeRename(mount.Id,"folder","é","New"));Assert.Equal("É",repo.EntryById(mount.Id,"folder")!.LocalPath);Assert.Equal("É/file",repo.EntryById(mount.Id,"file")!.LocalPath);
        var attempts=0;repo.NativeNotificationCommittingForTests=()=>{if(++attempts<3)throw new SqliteException("busy",5);};await using var session=new CloudMountSession(mount,null,repo,logger);
        session.NativeNotificationForTests("renamed",NativeRuntimeProtocol.Element(new{entryId="folder",oldPath=Path.Combine(area.Path,"É"),path=Path.Combine(area.Path,"New")}));await session.DrainNotificationsForTests();Assert.Equal(3,attempts);
        Assert.Equal("New",repo.EntryById(mount.Id,"folder")!.LocalPath);Assert.Equal("New/file",repo.EntryById(mount.Id,"file")!.LocalPath);Assert.Equal("Éx/file",repo.EntryById(mount.Id,"sibling")!.LocalPath);
        var again=repo.ApplyNativeRename(mount.Id,"folder","É","New");Assert.NotNull(again);Assert.Equal(3,attempts);
    }
    [Fact]
    public void DeleteTransactionRollsBackWithoutPartialProtectionAndReplaysIdempotently()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));var mount=Guid.NewGuid();repo.SaveEntry(mount,new("folder","É","É","v",true));repo.SaveEntry(mount,new("file","É/file","É/file","v",false));repo.SaveEntry(mount,new("sibling","Éx/file","Éx/file","v",false));
        repo.NativeNotificationCommittingForTests=()=>throw new SqliteException("busy",5);Assert.Throws<SqliteException>(()=>repo.ApplyNativeDeletion(mount,"folder"));Assert.All(repo.Entries(mount),entry=>Assert.False(entry.Deleted||entry.Protected));
        repo.NativeNotificationCommittingForTests=null;repo.ApplyNativeDeletion(mount,"folder");repo.ApplyNativeDeletion(mount,"folder");Assert.True(repo.EntryById(mount,"folder")!.Deleted);Assert.True(repo.EntryById(mount,"file")!.Protected);Assert.False(repo.EntryById(mount,"sibling")!.Deleted);
    }
    [Fact(Timeout=20000)]
    public async Task StopCancelsBackoffAndDrainsWithoutClaimingSuccess()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));await using var session=new CloudMountSession(new(Guid.NewGuid(),"stop",area.Path,false),null,repo,logger);var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        session.QueueNotificationForTests(()=>{Interlocked.Increment(ref calls);attempted.TrySetResult();throw new SqliteException("busy",5);});await attempted.Task;await session.RequestStop();await session.DrainNotificationsForTests().WaitAsync(TimeSpan.FromSeconds(3));Assert.Equal(1,calls);
    }
    [Theory(Timeout=20000)]
    [InlineData("sequence")][InlineData("open")][InlineData("path")][InlineData("entry")]
    public async Task InvalidProtocolStillClosesUntrustedSharedRuntime(string invalid)
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var mount=new MountDefinition(Guid.NewGuid(),"protocol",area.Path,false);await using var session=new CloudMountSession(mount,null,repo,logger);session.AttachNotificationRuntimeForTests(runtime);
        var failed=new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);runtime.Failed+=error=>failed.TrySetResult(error);
        var data=invalid=="path"?NativeRuntimeProtocol.Element(new{entryId="entry",path=Path.GetTempPath(),oldPath=Path.Combine(area.Path,"file")}):NativeRuntimeProtocol.Element(new{entryId=invalid=="entry"?new string('e',4097):"entry",sequence=invalid=="sequence"?-1L:1L,openCount=invalid=="open"?-1:0,closeCount=1L});
        runtime.PostRequiredEventForTests(new(mount.Id.ToString("N"),session.SessionId,invalid=="path"?"renamed":"activity",data));var error=await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.IsType<InvalidDataException>(error);Assert.False(runtime.IsAvailable);
    }
    [Fact(Timeout=20000)]
    public async Task SubscriberRetryReusesCommittedRenameNoticeWithoutRepeatingDatabaseMutation()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));var mount=new MountDefinition(Guid.NewGuid(),"subscriber",area.Path,false);repo.SaveEntry(mount.Id,new("entry","before","before","v",false));
        var commits=0;repo.NativeNotificationCommittingForTests=()=>commits++;await using var session=new CloudMountSession(mount,null,repo,logger);var notices=new List<LocalRenameNotice>();
        session.LocalRenameCompleted+=notice=>{notices.Add(notice);if(notices.Count<3)throw new IOException("subscriber busy");};
        session.NativeNotificationForTests("renamed",NativeRuntimeProtocol.Element(new{entryId="entry",oldPath=Path.Combine(area.Path,"before"),path=Path.Combine(area.Path,"after")}));await session.DrainNotificationsForTests();
        Assert.Equal(1,commits);Assert.Equal(3,notices.Count);Assert.All(notices,notice=>{Assert.Equal("before",notice.OldPath);Assert.Equal("before",notice.Entry!.LocalPath);Assert.Equal("after",notice.NewPath);});Assert.Equal("after",repo.EntryById(mount.Id,"entry")!.LocalPath);
    }
    [Theory(Timeout=20000)]
    [InlineData(true)][InlineData(false)]
    public async Task NonRetryableIndexDataErrorIsLocalBusinessFailure(bool sqlite)
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();await using var session=new CloudMountSession(new(Guid.NewGuid(),"index",area.Path,false),null,repo,logger);session.AttachNotificationRuntimeForTests(runtime);var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);session.RuntimeFailed+=_=>failed.TrySetResult();var calls=0;
        session.QueueNotificationForTests(()=>{calls++;if(sqlite)throw new SqliteException("corrupt",26);throw new System.Text.Json.JsonException("index record shape");});await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));await session.DrainNotificationsForTests();Assert.Equal(1,calls);Assert.Equal(0,session.NotificationRetriesForTests);Assert.True(runtime.IsAvailable);Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
    }
    [Fact(Timeout=20000)]
    public async Task StaleRenameKeepsNewerMappingAndRequestsRescanWithoutDisconnecting()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var mount=new MountDefinition(Guid.NewGuid(),"stale",area.Path,false);repo.SaveEntry(mount.Id,new("entry","first","latest","v",false));await using var session=new CloudMountSession(mount,null,repo,logger);session.AttachNotificationRuntimeForTests(runtime);var scans=0;var failures=0;session.NotificationRescanRequired+=()=>scans++;session.RuntimeFailed+=_=>failures++;
        session.NativeNotificationForTests("renamed",NativeRuntimeProtocol.Element(new{entryId="entry",oldPath=Path.Combine(area.Path,"first"),path=Path.Combine(area.Path,"second")}));await session.DrainNotificationsForTests();Assert.Equal("latest",repo.EntryById(mount.Id,"entry")!.LocalPath);Assert.Equal(1,scans);Assert.Equal(0,failures);Assert.True(runtime.IsAvailable);
    }
    [Theory(Timeout=20000)]
    [InlineData(512)][InlineData(4096)]
    public async Task ExistingNativeEntryByteBudgetIsAcceptedWithoutNarrowingIdentityContract(int length)
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));var mount=new MountDefinition(Guid.NewGuid(),"identity",area.Path,false);var id=new string('e',length);repo.SaveEntry(mount.Id,new(id,"file","file","v",false));var notified=0;await using var session=new CloudMountSession(mount,null,repo,logger,localContentReady:_=>notified++);
        session.NotificationForTests(NativeRuntimeProtocol.Element(new{entryId=id,sequence=1L,openCount=0,closeCount=1L}));await session.DrainNotificationsForTests();Assert.Equal(1,notified);
    }
    private sealed class Area:IDisposable
    {
        internal string Path{get;}=Directory.CreateTempSubdirectory("HybridMount.NotificationFailure.").FullName;
        public void Dispose()=>Directory.Delete(Path,true);
    }
}
