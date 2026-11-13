using System.Diagnostics;
namespace HybridMount.Tests;

/// <summary>共享读循环调用真实会话入口，慢消费者仅影响自身；所有路径为随机普通临时目录。</summary>
public sealed class NativeCloseDispatchTests
{
    private static System.Text.Json.JsonElement State(string id,long sequence)=>NativeRuntimeProtocol.Element(new{entryId=id,sequence,openCount=0,closeCount=sequence});
    [Fact(Timeout=20000)]
    public async Task SlowMountDoesNotBlockSharedCallerAndTenThousandClosesCoalesce()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));
        var calls=0;var fast=0;using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var slow=new CloudMountSession(new(Guid.NewGuid(),"slow",area.Path,false),null,repo,logger,localChanged:()=>{Interlocked.Increment(ref calls);entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));});
        await using var other=new CloudMountSession(new(Guid.NewGuid(),"other",area.Path,false),null,repo,logger,localChanged:()=>Interlocked.Increment(ref fast));
        try
        {
            slow.NotificationForTests(State("file",1));await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var elapsed=Stopwatch.StartNew();
            for(var sequence=2;sequence<=10001;sequence++)slow.NotificationForTests(State("file",sequence));
            other.NotificationForTests(State("different",1));await other.DrainNotificationsForTests().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1,fast);Assert.Equal(1,calls);Assert.InRange(slow.PendingNotificationsForTests,1,2);Assert.True(elapsed.Elapsed<TimeSpan.FromSeconds(3));
            release.Set();await slow.DrainNotificationsForTests();Assert.Equal(2,calls);
            slow.NotificationForTests(State("file",9999));await slow.DrainNotificationsForTests();Assert.Equal(2,calls);
        }
        finally{release.Set();}
    }
    [Fact(Timeout=20000)]
    public async Task CloseQueriesRespectRenameBarrierAndSharePinContentSnapshot()
    {
        using var area=new Area();var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"log.db"));
        var mount=new MountDefinition(Guid.NewGuid(),"order",area.Path,false);repo.SaveEntry(mount.Id,new("entry","before.txt","before.txt","v",false));
        var content=new List<string>();var pins=new List<string>();await using var session=new CloudMountSession(mount,null,repo,logger,localContentReady:entry=>content.Add(entry.LocalPath));session.PinWorkCompleted+=pins.Add;
        using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            session.QueueNotificationForTests(()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(10)));});await entered.Task;
            session.NotificationForTests(State("entry",1));
            session.NativeNotificationForTests("renamed",NativeRuntimeProtocol.Element(new{entryId="entry",oldPath=Path.Combine(area.Path,"before.txt"),path=Path.Combine(area.Path,"after.txt")}));
            session.NotificationForTests(State("entry",2));release.Set();await session.DrainNotificationsForTests();
            Assert.Equal(new[]{"before.txt","after.txt"},content);Assert.Equal(content,pins);
            await session.RequestStop();session.NotificationForTests(State("entry",3));await session.DrainNotificationsForTests();Assert.Equal(2,content.Count);
        }
        finally{release.Set();}
    }
    private sealed class Area:IDisposable
    {
        internal string Path{get;}=Directory.CreateTempSubdirectory("HybridMount.CloseDispatch.").FullName;
        public void Dispose()=>Directory.Delete(Path,true);
    }
}
