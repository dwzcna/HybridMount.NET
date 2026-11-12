using System.Collections.Concurrent;
using HybridMount.Services.Mounting;
using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>独立持久问题、合并提示、任务页面定位及页面重试／取消联动，所有定义和日志均隔离。</summary>
public sealed class UploadIssueIntegrationTests
{
    private sealed class Dispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue=new();
        public bool TryEnqueue(Action callback){_queue.Enqueue(callback);return true;}
        public void Drain(){while(_queue.TryDequeue(out var callback))callback();}
    }
    private sealed class Navigation : INavigationService
    {
        public readonly List<ApplicationPage> Requests=[];
        public bool CanGoBack=>false;
        public bool TryGoBack()=>false;
        public bool Navigate(ApplicationPage page){Requests.Add(page);return Enum.IsDefined(page);}
    }
    private sealed class Area : IAsyncDisposable
    {
        public readonly DavFixture Dav=new();public MountRepository Repository {get;}public MountManager Manager {get;}public LoggerService Logger {get;}
        public Guid Id {get;}=Guid.NewGuid();private readonly string _registry=@"Software\HybridMount.NET.Tests\Issue-"+Guid.NewGuid().ToString("N");
        public Area(FileSourceKind kind=FileSourceKind.WebDav)
        {
            var path=Path.Combine(Dav.Area,"local");Directory.CreateDirectory(path);Repository=new(Path.Combine(Dav.Area,"data"),_registry);
            Repository.SaveDefinition(new(Id,"issue mount",path,false,RootIdentity:MountPathGuard.FileIdentity(path),Source:new(kind,"https://example.test/"),RegistrationStage:MountRegistrationStage.NotRegistered));
            Logger=new(Path.Combine(Dav.Area,"log.db"));Manager=new(Repository,Logger);
        }
        public LocalUpload Put(string id,string state="Failed",string? reason="specific reason")
        {var item=new LocalUpload(id,Id,id+".txt",id+".txt",false,"identity:0:1:1:1:False",1,state,DateTimeOffset.UtcNow,Error:reason);Repository.SaveUpload(item);return Repository.Upload(id)!;}
        public Task Notify()=>Manager.ClearCompletedOperationsAsync();
        public async ValueTask DisposeAsync(){await Manager.DisposeAsync();await Logger.DisposeAsync();Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_registry,false);await Dav.DisposeAsync();}
    }
    [Theory]
    [InlineData(FileSourceKind.WebDav)][InlineData(FileSourceKind.S3)]
    public async Task FocusIsIndependentOfHistoryAndRepeatedRetryKeepsReason(FileSourceKind kind)
    {
        await using var area=new Area(kind);var row=area.Put("one");var ui=new Dispatcher();var navigation=new Navigation();var focus=new TaskFocusService(navigation);
        using var vm=new TasksViewModel(area.Manager,ui,focus);Assert.Empty(vm.Operations);
        Assert.True(focus.ShowIssues(area.Id,["one.txt"]));ui.Drain();await vm.SnapshotIdle;ui.Drain();Assert.True(vm.IssuesOnly);Assert.Equal(row.Id,Assert.Single(vm.Operations).Id);
        Assert.Equal("specific reason",vm.Selected!.DisplayIssueReason);await vm.RetrySelectedAsync();ui.Drain();await vm.SnapshotIdle;ui.Drain();Assert.Contains("正在重试",vm.Selected!.DisplayState);Assert.Equal("specific reason",vm.Selected.DisplayIssueReason);Assert.Equal("等待挂载连接。",vm.Selected.WaitingReason);Assert.Equal("specific reason",vm.Selected.Attention!.Reason);
        var queued=area.Repository.Upload(row.Id)!;await vm.RetrySelectedAsync();Assert.Equal(queued,area.Repository.Upload(row.Id));
        Assert.True(focus.ShowIssues(area.Id,["missing.txt"]));ui.Drain();await vm.SnapshotIdle;ui.Drain();Assert.Empty(vm.Operations);
        focus.ShowIssues(area.Id);ui.Drain();await vm.SnapshotIdle;ui.Drain();Assert.Single(vm.Operations);vm.SelectedJobId=row.Id;await vm.CancelSelectedAsync();ui.Drain();await vm.SnapshotIdle;ui.Drain();Assert.Empty(vm.Operations);Assert.Empty(area.Manager.UploadIssues);
        Assert.All(navigation.Requests,page=>Assert.Equal(ApplicationPage.Tasks,page));
        vm.Dispose();focus.ShowIssues(area.Id);ui.Drain();await vm.SnapshotIdle;ui.Drain();focus.RequestStop();Assert.False(focus.ShowIssues(area.Id));
    }
    [Fact]
    public async Task SegmentedSelectionIsExclusiveAndPreservesIssueFocusAcrossAll()
    {
        await using var area=new Area();var row=area.Put("one");var ui=new Dispatcher();var focus=new TaskFocusService(new Navigation());using var vm=new TasksViewModel(area.Manager,ui,focus);
        Assert.Equal(0,vm.SelectedTaskFilter);focus.ShowIssues(area.Id,["one.txt"]);ui.Drain();await vm.SnapshotIdle;ui.Drain();Assert.Equal(1,vm.SelectedTaskFilter);Assert.True(vm.HasIssueFilter);
        vm.SelectedTaskFilter=2;Assert.False(vm.IssuesOnly);Assert.True(vm.ConflictsOnly);vm.SelectedTaskFilter=0;Assert.False(vm.IssuesOnly);Assert.False(vm.ConflictsOnly);Assert.True(vm.HasIssueFilter);
        vm.SelectedTaskFilter=1;await vm.SnapshotIdle;ui.Drain();Assert.Equal(row.Id,Assert.Single(vm.Operations).Id);vm.SelectedTaskFilter=-1;Assert.Equal(1,vm.SelectedTaskFilter);vm.SelectedTaskFilter=3;Assert.Equal(1,vm.SelectedTaskFilter);
        vm.ConflictsOnly=true;Assert.Equal(2,vm.SelectedTaskFilter);vm.ConflictsOnly=false;Assert.Equal(0,vm.SelectedTaskFilter);vm.Dispose();vm.SelectedTaskFilter=1;Assert.Equal(0,vm.SelectedTaskFilter);
    }
    [Fact(Timeout=30000)]
    public async Task NotificationsMergeDeduplicateHistoryAndDoNotResolveDismissedIssues()
    {
        await using var area=new Area();area.Put("old");await using var notices=new UploadIssueNotificationService(area.Manager);
        await area.Notify();Assert.Empty(notices.Pending);
        var changed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);notices.Changed+=()=>changed.TrySetResult();
        area.Put("new1");area.Put("new2");await area.Notify();await changed.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var notice=Assert.Single(notices.Pending);Assert.Equal(3,notice.Count);notices.Dismiss(area.Id);Assert.Empty(notices.Pending);Assert.Equal(3,area.Manager.UploadIssues.Count);
        await area.Notify();Assert.Empty(notices.Pending);
        area.Repository.UpdateUpload("new1",x=>x with{State="Queued",Error=null});await area.Notify();Assert.Empty(notices.Pending);
        area.Repository.UpdateUpload("new1",x=>x with{State="Failed",Error="retry failed again"});await area.Notify();Assert.Single(notices.Pending);
        foreach(var row in area.Manager.UploadIssues)area.Repository.UpdateUpload(row.Id,x=>x with{State="Completed"});await area.Notify();Assert.Empty(notices.Pending);Assert.Empty(area.Manager.UploadIssues);
        await notices.DisposeAsync();var calls=0;notices.Changed+=()=>calls++;area.Put("late");await area.Notify();Assert.Equal(0,calls);
    }
    [Theory]
    [InlineData("outOfRoot","不支持将文件移出同步根")]
    [InlineData("sessionStopping","挂载正在停止")]
    [InlineData("differentMount","不支持跨挂载移动文件")]
    [InlineData("unsafeTarget","重命名目标目录不可安全访问")]
    [InlineData("invalidTarget","重命名未完成")]
    public void RejectedRenameMessagesExplainTheReasonWithoutExposingParentPaths(string reason,string expected)
    {
        var message=CloudMountSession.RenameRejectionMessage(@"C:\private\文件.txt",reason);
        Assert.Contains(expected,message);Assert.Contains("文件.txt",message);Assert.DoesNotContain("private",message);
        Assert.Contains("未知文件",CloudMountSession.RenameRejectionMessage(null,reason));
    }
    [Fact(Timeout=15000)]
    public async Task RejectedRenameEventsAreGenerationScopedLoggedAndMergedIntoDismissibleNotices()
    {
        await using var area=new Area();await using var notices=new UploadIssueNotificationService(area.Manager);
        var mount=area.Manager.Mounts.Single().Definition;await using var session=new CloudMountSession(mount,null,area.Repository,area.Logger);
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var generation=(string)typeof(CloudMountSession).GetField("_sessionId",flags)!.GetValue(session)!;
        var receive=typeof(CloudMountSession).GetMethod("OnNativeEvent",flags)!;var relay=typeof(MountManager).GetMethod("NotifyRenameRejected",flags)!;
        session.RenameRejected+=message=>relay.Invoke(area.Manager,[area.Id,message]);
        var data=NativeRuntimeProtocol.Element(new{entryId="entry",sourcePath=@"C:\root\文件.txt",targetPath=@"C:\outside\文件.txt",reason="outOfRoot"});
        void Emit(string id,string sessionId)=>receive.Invoke(session,[new NativeRuntimeEvent(id,sessionId,"renameRejected",data)]);
        Emit(Guid.NewGuid().ToString("N"),generation);Emit(area.Id.ToString("N"),"old generation");Assert.Empty(notices.Pending);
        var changed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;notices.Changed+=()=>{Interlocked.Increment(ref calls);changed.TrySetResult();};
        Emit(area.Id.ToString("N"),generation);Emit(area.Id.ToString("N"),generation);await changed.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var notice=Assert.Single(notices.Pending);Assert.Equal(0,notice.Count);Assert.Equal("不支持将文件移出同步根：文件.txt，请复制后再删除",notice.Message);Assert.Equal(1,calls);
        Assert.Contains(await area.Logger.ReadAsync(),row=>row.Message==notice.Message);Assert.Empty(area.Manager.UploadIssues);
        area.Put("upload");await area.Notify();notice=Assert.Single(notices.Pending);Assert.Equal(1,notice.Count);Assert.NotNull(notice.Message);
        notices.Dismiss(area.Id);Assert.Empty(notices.Pending);Assert.Single(area.Manager.UploadIssues);
        await notices.DisposeAsync();Emit(area.Id.ToString("N"),generation);Assert.Empty(notices.Pending);
    }
    [Fact]
    public async Task CanceledUnknownResultRetainsEvidenceWithoutNewWarningAndCompletedHistoryIsQuiet()
    {
        await using var area=new Area();var unknown=area.Put("unknown","OutcomeUnknown","request result unknown");
        Assert.True(area.Manager.RetryUpload(unknown.Id));Assert.Equal("request result unknown",Assert.Single(area.Manager.UploadIssues).DisplayIssueReason);
        Assert.True(area.Manager.Cancel(unknown.Id));var canceled=area.Repository.Upload(unknown.Id)!;Assert.True(canceled.CancelRequested);Assert.Equal("OutcomeUnknown",canceled.State);Assert.Empty(area.Manager.UploadIssues);
        area.Put("completed","Completed","historical error");area.Repository.RecoverUploadAttention();Assert.Empty(area.Manager.UploadIssues);
    }
    [Theory]
    [InlineData("showSyncIssues",true)][InlineData("delete",false)]
    public void IpcIssueCommandIsBoundedAndKeepsVersionTwo(string operation,bool valid)
    {
        var request=new IpcRequest(2,Guid.NewGuid().ToString(),operation,[@"D:\myweb\中文.txt"]);Assert.Equal(valid,IpcProtocol.IsValid(request));
        Assert.False(IpcProtocol.IsValid(request with{Paths=[]}));Assert.False(IpcProtocol.IsValid(request with{Paths=["relative"]}));Assert.False(IpcProtocol.IsValid(request with{Version=1}));
    }
    [Theory(Timeout=90000)]
    [InlineData(FileSourceKind.WebDav)][InlineData(FileSourceKind.S3)]
    public async Task HttpFailureExplicitRetryAndSuccessClearWithoutAutomaticResend(FileSourceKind kind)
    {
        await using var dav=new DavFixture();await using var s3=new S3Fixture();
        var server=kind==FileSourceKind.WebDav?dav.Server:s3.Server;var options=kind==FileSourceKind.WebDav?dav.Options:s3.Options;
        var area=kind==FileSourceKind.WebDav?dav.Area:s3.Area;var local=Path.Combine(area,"issue-view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\IssueFlow-"+Guid.NewGuid().ToString("N");var repository=new MountRepository(Path.Combine(area,"issue-data"),registry);
        await using var logger=new LoggerService(Path.Combine(area,"issue-log.db"));await using var manager=new MountManager(repository,logger);
        var mount=await manager.AddAsync("issue flow",local,false,source:options,credentials:kind==FileSourceKind.WebDav?new("user","app-password"):S3Fixture.Credentials);
        try
        {
            await using var notices=new UploadIssueNotificationService(manager);var handler=server.Handler;var fail=true;
            server.Handler=request=>request.Method=="PUT"&&request.Target.EndsWith("issue.txt",StringComparison.Ordinal)&&fail
                ?Task.FromResult(HttpTestServer.Response.Text(403,"<Error><Code>AccessDenied</Code></Error>")):handler(request);
            await File.WriteAllTextAsync(Path.Combine(local,"issue.txt"),"only this content");
            async Task<LocalUpload> Wait(string state)
            {
                var end=DateTime.UtcNow.AddSeconds(30);
                while(DateTime.UtcNow<end){var row=repository.Uploads(mount.Id).FirstOrDefault(x=>x.LocalPath=="issue.txt");if(row?.State==state)return row;await Task.Delay(50);}
                throw new TimeoutException(state);
            }
            async Task PublishUntil(string id,string expected)
            {
                var end=DateTime.UtcNow.AddSeconds(6);
                do{await manager.PublishUploadIssuesAsync();if(repository.Upload(id)!.Attention!.ShellState==expected)return;await Task.Delay(25);}while(DateTime.UtcNow<end);
                Assert.Fail(repository.Upload(id)!.Attention!.ShellState+": "+repository.Upload(id)!.Attention!.ShellError);
            }
            var failed=await Wait("Failed");Assert.Single(manager.UploadIssues);Assert.DoesNotContain(repository.Entries(mount.Id),x=>x.LocalPath=="issue.txt");
            await PublishUntil(failed.Id,"Published");
            var sends=server.Requests.Count(x=>x.Method=="PUT"&&x.Target.EndsWith("issue.txt",StringComparison.Ordinal));await Task.Delay(2300);
            Assert.Equal(sends,server.Requests.Count(x=>x.Method=="PUT"&&x.Target.EndsWith("issue.txt",StringComparison.Ordinal)));
            await manager.ClearOperationHistoryAsync(TaskHistoryClearMode.Failed);Assert.Single(manager.UploadIssues);
            Assert.True(manager.RetryUpload(failed.Id));Assert.Equal(failed.Attention!.Reason,Assert.Single(manager.UploadIssues).DisplayIssueReason);
            var again=await Wait("Failed");Assert.NotEqual(failed.Attention.NotificationKey,again.Attention!.NotificationKey);
            fail=false;Assert.True(manager.RetryUpload(failed.Id));await Wait("Completed");await PublishUntil(failed.Id,"Cleared");
            Assert.Empty(manager.UploadIssues);Assert.Equal("Cleared",repository.Upload(failed.Id)!.Attention!.ShellState);
            Assert.Equal(sends+2,server.Requests.Count(x=>x.Method=="PUT"&&x.Target.EndsWith("issue.txt",StringComparison.Ordinal)));
        }
        finally{await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));await manager.DisposeAsync();Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
    [Fact]
    public async Task NoIndexFailureIsIncludedInCompleteDiagnostics()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"first-failure.txt");await File.WriteAllTextAsync(path,"local");await test.Manager.ScanUploadChangesAsync(test.Id);
        var row=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="first-failure.txt");test.Repository.UpdateUpload(row.Id,x=>x with{State="Failed",Error="first upload refused"});
        Assert.DoesNotContain(test.Repository.Entries(test.Id),x=>x.LocalPath==row.LocalPath);var before=test.Dav.Server.Requests.Count;
        var diagnostic=await test.Manager.ReadDiagnosticsAsync(test.Id);Assert.Contains(row.LocalPath,diagnostic);Assert.Contains("first upload refused",diagnostic);Assert.Contains("尚未建立索引",diagnostic);Assert.Equal(before,test.Dav.Server.Requests.Count);
    }
}
