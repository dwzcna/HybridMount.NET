using System.Collections.Concurrent;
using HybridMount.Models.Diagnostics;
using HybridMount.Services;
using HybridMount.ViewModels;
using Microsoft.Data.Sqlite;
namespace HybridMount.Tests;

/// <summary>独立SQLite目录，验证历史查询、导出、丢失证据和安全异常；不读取正式日志或配置。</summary>
public sealed class LogHistoryTests
{
    private sealed class Area:IAsyncDisposable
    {
        internal readonly string DirectoryPath=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HybridMount.LogTests",Guid.NewGuid().ToString("N"));
        internal string Path=>System.IO.Path.Combine(DirectoryPath,"logs.db");
        public ValueTask DisposeAsync(){if(Directory.Exists(DirectoryPath))Directory.Delete(DirectoryPath,true);return ValueTask.CompletedTask;}
    }
    private sealed class Dispatcher:IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _pending=new();
        public bool TryEnqueue(Action action){_pending.Enqueue(action);return true;}
        internal void Drain(){while(_pending.TryDequeue(out var action))action();}
    }
    [Fact]
    public async Task OldErrorRemainsSearchableBeyondLegacyHundredAndExportContainsWholeFilter()
    {
        await using var area=new Area();await using var logger=new LoggerService(area.Path);
        logger.Queue("最早","Error","ältere 中文错误");
        for(var i=0;i<180;i++)logger.Queue("后续","Information",$"info {i}");
        Assert.DoesNotContain(await logger.ReadAsync(),x=>x.Severity=="Error");
        var page=await logger.ReadAsync(new DiagnosticQuery("Error","ÄLTERE"));Assert.Single(page.Entries);Assert.False(page.HasMore);
        var exported=await logger.ExportAsync(new DiagnosticQuery("All",Limit:1));Assert.Equal(181,exported.Count);Assert.Contains("架构=",exported.Text);Assert.Contains("保留规则",exported.Text);Assert.Contains("中文错误",exported.Text);
        Assert.Empty((await logger.ReadAsync(new DiagnosticQuery(Text:"' OR 1=1 --"))).Entries);
        var stamp=page.Entries[0].RecordedUtc;
        Assert.Empty((await logger.ReadAsync(new DiagnosticQuery("Error",ToUtc:stamp))).Entries);
        Assert.Single((await logger.ReadAsync(new DiagnosticQuery("Error",FromUtc:stamp))).Entries);
    }
    [Fact]
    public async Task StableCursorHasNoDuplicateOrMissingRowsWhenNewLogsArrive()
    {
        await using var area=new Area();await using var logger=new LoggerService(area.Path);
        for(var i=0;i<173;i++)logger.Queue("分页","Warning",$"before {i}");
        var first=await logger.ReadAsync(new DiagnosticQuery(Limit:31));var rows=first.Entries.ToList();
        logger.Queue("新事件","Error","after snapshot");await logger.ReadAsync();var current=first;
        while(current.HasMore){current=await logger.ReadAsync(new DiagnosticQuery(BeforeId:current.NextBeforeId,ThroughId:first.ThroughId,Limit:31));rows.AddRange(current.Entries);}
        Assert.Equal(173,rows.Count);Assert.Equal(173,rows.Select(x=>x.Id).Distinct().Count());Assert.DoesNotContain(rows,x=>x.Message=="after snapshot");
        var exported=await logger.ExportAsync(new DiagnosticQuery(ThroughId:first.ThroughId));Assert.Equal(173,exported.Count);Assert.DoesNotContain("after snapshot",exported.Text);
    }
    [Fact(Timeout=30000)]
    public async Task QueueOverflowHasDurableSummaryAndStickyCountAndDrain()
    {
        await using var area=new Area();using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var block=1;
        await using(var logger=new LoggerService(area.Path,path=>{if(Interlocked.Exchange(ref block,0)==1){entered.TrySetResult();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();}return new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());}))
        {
            try{logger.Queue("溢出","Information","seed");await entered.Task;for(var i=0;i<700;i++)logger.Queue("溢出","Information",$"item {i}");Assert.True(logger.DroppedCount>0);}
            finally{release.Set();}
            var page=await logger.ReadAsync(new DiagnosticQuery("Warning"));Assert.Contains(page.Entries,x=>x.Source=="日志存储"&&x.Message.Contains("未保存"));
            var dropped=logger.DroppedCount;await logger.ClearAsync();Assert.Empty(await logger.ReadAsync());Assert.Equal(dropped,logger.DroppedCount);
            logger.Queue("收尾","Information","accepted after clear");
        }
        await using var reopened=new LoggerService(area.Path);Assert.Equal("accepted after clear",Assert.Single(await reopened.ReadAsync()).Message);
    }
    [Fact]
    public async Task StorageFailureIsCountedAndRecoveredWithoutLeakingExceptionMessage()
    {
        await using var area=new Area();var fail=true;
        await using var logger=new LoggerService(area.Path,path=>fail?throw new IOException("https://user:secret@server/?token=private"):new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString()));
        logger.Queue("数据库","Error","not saved");await Assert.ThrowsAsync<IOException>(()=>logger.ReadAsync());Assert.True(logger.DroppedCount>0);Assert.DoesNotContain("secret",logger.LastError);
        fail=false;var recovered=await logger.ReadAsync(new DiagnosticQuery("Warning"));Assert.Contains(recovered.Entries,x=>x.Source=="日志存储");
    }
    [Fact]
    public async Task ExceptionBoundaryExcludesMessagesDataAndUnsafeStructuredStrings()
    {
        await using var area=new Area();await using var logger=new LoggerService(area.Path);
        var error=new IOException("https://user:secret@host/?token=private",new UnauthorizedAccessException("Authorization: Bearer secret"));error.Data["password"]="private";
        logger.QueueException("测试","Error","固定说明",error);logger.QueueException("另一页面","Error","重复捕获",error);
        var protocol=new SourceConnectionException(new("https://secret",ConnectionFailureKind.Authentication,401,"bad?token=secret","private",null,"raw secret"));logger.QueueException("协议","Warning","认证失败",protocol);
        var export=await logger.ExportAsync(new DiagnosticQuery());Assert.Equal(2,export.Count);Assert.Contains("HTTP=401",export.Text);Assert.Contains("IOException",export.Text);Assert.Contains("HRESULT=0x",export.Text);
        Assert.DoesNotContain("secret",export.Text);Assert.DoesNotContain("private",export.Text);Assert.DoesNotContain("Authorization",export.Text);
    }
    [Fact]
    public async Task LatestViewModelQueryWinsAndClearDoesNotResurrectQueuedEvents()
    {
        await using var area=new Area();await using var logger=new LoggerService(area.Path);var dispatcher=new Dispatcher();using var vm=new LogsViewModel(logger,dispatcher);
        logger.Queue("旧页","Error","error old");for(var i=0;i<130;i++)logger.Queue("信息","Information",$"info {i}");
        await vm.RefreshAsync();dispatcher.Drain();vm.Query="does not exist";vm.Query="error";vm.Severity="Error";await vm.RefreshAsync();dispatcher.Drain();Assert.Single(vm.Entries);Assert.Equal("error old",vm.Entries[0].Message);
        vm.Query="";vm.Severity="All";await vm.RefreshAsync();Assert.True(vm.CanLoadMore);await vm.LoadOlderAsync();Assert.Equal(131,vm.Entries.Count);
        logger.Queue("晚通知","Error","not resurrected");await logger.ReadAsync();await vm.ClearAsync();dispatcher.Drain();Assert.Empty(vm.Entries);Assert.Empty(await logger.ReadAsync());
    }
    [Fact]
    public async Task BadHostEvidenceCannotHideValidRecordsAndPreventsFalseClearSuccess()
    {
        await using var area=new Area();var directory=System.IO.Path.Combine(area.DirectoryPath,"HostDiagnostics");Directory.CreateDirectory(directory);
        var bad=System.IO.Path.Combine(directory,Guid.Empty.ToString("N")+".json");await File.WriteAllTextAsync(bad,"{invalid secret");
        Assert.True(HostDiagnosticStore.Record("ShellServer",new UnauthorizedAccessException("password"),directory:directory));
        await using var logger=new LoggerService(area.Path);Assert.Single((await logger.ReadAsync(new DiagnosticQuery("Error"))).Entries);Assert.True(File.Exists(bad));Assert.Contains("宿主证据",logger.LastError);
        await Assert.ThrowsAsync<IOException>(()=>logger.ClearAsync());Assert.Single(await logger.ReadAsync());Assert.DoesNotContain("secret",logger.LastError);
        File.Delete(bad);await logger.ClearAsync();Assert.Empty(await logger.ReadAsync());Assert.Null(logger.LastError);
    }
    [Fact]
    public async Task HostEvidenceImportsOncePersistsAndClearDoesNotResurrectIt()
    {
        await using var area=new Area();var directory=System.IO.Path.Combine(area.DirectoryPath,"HostDiagnostics");
        Assert.True(HostDiagnosticStore.Record("PreviewWorker",new IOException("secret body"),directory:directory));
        var path=Assert.Single(Directory.GetFiles(directory,"*.json"));var bytes=await File.ReadAllBytesAsync(path);
        await using(var logger=new LoggerService(area.Path))
        {
            var first=Assert.Single((await logger.ReadAsync(new DiagnosticQuery("Error"))).Entries);Assert.Contains("PreviewWorker",first.Message);Assert.DoesNotContain("secret",first.Message);Assert.False(File.Exists(path));
            // 模拟提交后、移除小证据前崩溃；同一GUID只能导入一次。
            await File.WriteAllBytesAsync(path,bytes);Assert.Single(await logger.ReadAsync());Assert.False(File.Exists(path));await logger.ClearAsync();Assert.Empty(await logger.ReadAsync());
        }
        await using var reopened=new LoggerService(area.Path);Assert.Empty(await reopened.ReadAsync());
    }
    [Fact(Timeout=60000)]
    public async Task ConflictReplacementFailureAndRecoveryRemainReviewableAfterRestart()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=System.IO.Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);
        test.Dav.Put("文件.bin",System.Text.Encoding.UTF8.GetBytes("remote choice"));await File.WriteAllTextAsync(path,"preserve local");await test.Manager.ScanUploadChangesAsync(test.Id);
        var upload=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="文件.bin");upload=test.Repository.UpdateUpload(upload.Id,x=>x with{State="Conflict",Error="version mismatch"})!;
        var comparison=await test.Manager.CompareConflictAsync(upload.Id);test.Manager.ConflictWriteForTests=()=>throw new IOException("secret body https://user:password@server/?token=private");
        await Assert.ThrowsAsync<IOException>(()=>test.Manager.ResolveConflictAsync(comparison,false));
        var failures=(await test.Logger.ReadAsync(new DiagnosticQuery("Error",upload.Id))).Entries;Assert.Contains(failures,x=>x.Message.Contains("ResolutionPending"));Assert.DoesNotContain(failures,x=>x.Message.Contains("secret")||x.Message.Contains("password"));
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);comparison=await test.Manager.CompareConflictAsync(upload.Id);await test.Manager.ResolveConflictAsync(comparison,false);
        Assert.Contains((await test.Logger.ReadAsync(new DiagnosticQuery("Information",upload.Id))).Entries,x=>x.Message.Contains("问题已完成收尾"));
    }
    [Fact]
    public async Task FailedSubscriberCannotHidePersistedEntriesFromAnotherSubscriber()
    {
        await using var area=new Area();await using var logger=new LoggerService(area.Path);var calls=0;
        logger.EntryWritten+=(_,_)=>throw new IOException("do not block other subscribers");logger.EntryWritten+=(_,_)=>calls++;
        logger.Queue("订阅","Warning","visible");await logger.ReadAsync();Assert.Equal(1,calls);
        var exported=await logger.ExportAsync(new DiagnosticQuery(Text:"https://user:secret@host/?token=private"));Assert.DoesNotContain("secret",exported.Text);Assert.Contains("已启用",exported.Text);
    }
    [Fact(Timeout=60000)]
    public async Task RealUploadFailureIsLoggedOnceWithTaskAndSafeHttpCode()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=test.Dav.Server.Handler;
        test.Dav.Server.Handler=request=>request.Method=="PUT"&&request.Target.EndsWith("/log-failure.txt",StringComparison.Ordinal)?Task.FromResult(new HttpTestServer.Response(403,[])):original(request);
        await File.WriteAllTextAsync(System.IO.Path.Combine(test.Local,"log-failure.txt"),"local bytes");await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        LocalUpload? failed=null;var until=DateTime.UtcNow.AddSeconds(20);
        while(DateTime.UtcNow<until){failed=test.Repository.Uploads(test.Id).FirstOrDefault(x=>x.LocalPath=="log-failure.txt");if(failed?.State=="Failed")break;await Task.Delay(30);}
        Assert.Equal("Failed",failed?.State);await test.Manager.DisconnectAsync(test.Id);
        var entries=(await test.Logger.ReadAsync(new DiagnosticQuery("Error",failed!.Id))).Entries;Assert.Single(entries,x=>x.Source=="上传");Assert.Contains(entries,x=>x.Message.Contains("HTTP=403"));
        await test.Manager.ConnectAsync(test.Id);await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Single((await test.Logger.ReadAsync(new DiagnosticQuery("Error",failed.Id))).Entries,x=>x.Source=="上传");
    }
}
