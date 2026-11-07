using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>隔离NTFS／CFAPI根验证已删除文件的属性收尾、退避和代次竞争，不修改正式挂载或访问外部服务。</summary>
public sealed class ShellIssueMissingFileTests
{
    [Theory(Timeout=60000)]
    [InlineData("ClearPending",false)][InlineData("Clearing",false)]
    [InlineData("ClearPending",true)][InlineData("Clearing",true)]
    public async Task ResolvedMissingFileClearsOnceWithoutThrowingOrChangingUpload(string stage,bool removeParent)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);
        var folder=Path.Combine(test.Local,"removed-folder");Directory.CreateDirectory(folder);var path=Path.Combine(folder,"管道特性表.xlsx");
        await File.WriteAllTextAsync(path,"local bytes");var stamp=LocalFileState.Read(path);
        var row=Failed(test,"removed-folder/管道特性表.xlsx",stamp);
        test.Repository.UpdateUpload(row.Id,x=>x with{State="Canceled",CancelRequested=true});
        var canceled=test.Repository.UpdateUpload(row.Id,x=>x with{Attention=x.Attention! with{ShellState=stage,PublishedIdentity=stamp.Identity,ShellError="old missing error",ShellOriginalStamp=stage=="Clearing"?stamp.Key:null}})!;
        await test.Manager.DisconnectAsync(test.Id);if(removeParent)Directory.Delete(folder,true);else File.Delete(path);
        var requests=test.Dav.Server.Requests.Count;var other=LocalFileState.Read(Path.Combine(test.Local,"文件.bin"));
        var errors=await ObserveMissing(path,async()=>{await test.Manager.PublishUploadIssuesAsync();await test.Manager.PublishUploadIssuesAsync();});
        var cleared=test.Repository.Upload(row.Id)!;
        Assert.Empty(errors);Assert.Equal("Cleared",cleared.Attention!.ShellState);Assert.Null(cleared.Attention.ShellError);Assert.Null(cleared.Attention.ShellOriginalStamp);
        Assert.Equal(canceled with{Attention=cleared.Attention},cleared);Assert.Equal(canceled.Attention!.ResolvedUtc,cleared.Attention.ResolvedUtc);
        Assert.Empty(test.Manager.UploadIssues);Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal(other,LocalFileState.Read(Path.Combine(test.Local,"文件.bin")));
        test.Repository.RecoverUploadAttention();Assert.Equal(cleared,test.Repository.Upload(row.Id));
        var reopened=new MountRepository(test.Repository.DataDirectory,test.Registry);Assert.Equal(cleared,reopened.Upload(row.Id));
    }

    [Fact(Timeout=60000)]
    public async Task MissingUnresolvedIssueRemainsVisibleAndCancelBypassesItsBackoff()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"missing.txt");await File.WriteAllTextAsync(path,"bytes");
        var row=Failed(test,"missing.txt",LocalFileState.Read(path));await test.Manager.DisconnectAsync(test.Id);File.Delete(path);var requests=test.Dav.Server.Requests.Count;
        var errors=await ObserveMissing(path,async()=>{await test.Manager.PublishUploadIssuesAsync();await test.Manager.PublishUploadIssuesAsync();});
        var pending=test.Repository.Upload(row.Id)!;Assert.Empty(errors);Assert.Equal("Failed",pending.State);Assert.Equal(row.Generation,pending.Generation);
        Assert.Equal(row.ObservedStamp,pending.ObservedStamp);Assert.Equal("original failure",pending.Attention!.Reason);Assert.Null(pending.Attention.ResolvedUtc);
        Assert.Contains("本地文件不存在",pending.Attention.ShellError);Assert.Equal(row.Id,Assert.Single(test.Manager.UploadIssues).Id);
        test.Repository.UpdateUpload(row.Id,x=>x with{State="Canceled",CancelRequested=true});await test.Manager.PublishUploadIssuesAsync();
        Assert.Equal("Cleared",test.Repository.Upload(row.Id)!.Attention!.ShellState);Assert.Empty(test.Manager.UploadIssues);Assert.Equal(requests,test.Dav.Server.Requests.Count);
    }

    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task BothPublicationDirectionsBackOffButChangedAttentionCanRunImmediately(bool resolved)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"backoff.txt");await File.WriteAllTextAsync(path,"bytes");
        var row=Failed(test,"backoff.txt",LocalFileState.Read(path));if(resolved)test.Repository.UpdateUpload(row.Id,x=>x with{State="Canceled",CancelRequested=true});
        var calls=0;test.Manager.IssuePropertyWrittenForTests=_=>{calls++;throw new IOException("injected property failure");};
        await test.Manager.PublishUploadIssuesAsync();var failed=test.Repository.Upload(row.Id)!;await test.Manager.PublishUploadIssuesAsync();
        Assert.Equal(1,calls);Assert.Equal(failed,test.Repository.Upload(row.Id));Assert.Contains("injected property failure",failed.Attention!.ShellError);
        test.Repository.UpdateUpload(row.Id,x=>resolved?x with{Generation=x.Generation+1,State="Failed",CancelRequested=false,Error="new failure"}:x with{State="Canceled",CancelRequested=true});
        test.Manager.IssuePropertyWrittenForTests=_=>{calls++;return Task.CompletedTask;};await test.Manager.PublishUploadIssuesAsync();
        var saved=test.Repository.Upload(row.Id)!;Assert.Equal(2,calls);Assert.Equal(resolved?"Published":"Cleared",saved.Attention!.ShellState);Assert.Null(saved.Attention.ShellError);
    }

    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task LateFailureCannotDelayOrOverwriteANewIssue(bool newGeneration)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"late.txt");await File.WriteAllTextAsync(path,"bytes");
        var row=Failed(test,"late.txt",LocalFileState.Read(path));
        test.Manager.IssuePropertyWrittenForTests=_=>
        {
            test.Repository.UpdateUpload(row.Id,x=>x with{Generation=x.Generation+(newGeneration?1:0),Error="new reason"});throw new IOException("late old error");
        };
        await test.Manager.PublishUploadIssuesAsync();var latest=test.Repository.Upload(row.Id)!;
        Assert.Equal("new reason",latest.Attention!.Reason);Assert.Null(latest.Attention.ShellError);Assert.Equal("Pending",latest.Attention.ShellState);
        test.Manager.IssuePropertyWrittenForTests=null;await test.Manager.PublishUploadIssuesAsync();Assert.Equal("Published",test.Repository.Upload(row.Id)!.Attention!.ShellState);
    }

    [Theory(Timeout=60000)]
    [InlineData("identity")][InlineData("outside")][InlineData("junction")]
    public async Task MissingPathStillRequiresOwnedRootAndSafeAncestors(string invalid)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var mount=test.Repository.LoadDefinitions().Single();
        Assert.False(MountPathGuard.TryValidateOperation(mount,Path.Combine(test.Local,"missing-parent","missing.txt")));
        var path=Path.Combine(test.Local,"missing.txt");
        if(invalid=="identity")mount=mount with{RootIdentity="different"};
        if(invalid=="outside")path=Path.Combine(test.Dav.Area,"outside-missing.txt");
        if(invalid=="junction")
        {
            var outside=Path.Combine(test.Dav.Area,"outside");Directory.CreateDirectory(outside);var link=Path.Combine(test.Local,"link");
            ClearRemovalTests.CreateJunction(link,outside);path=Path.Combine(link,"missing.txt");
        }
        Assert.Throws<IOException>(()=>MountPathGuard.TryValidateOperation(mount,path));
    }

    /// <summary>构造当前文件身份的独立失败日志，不发送上传或依赖占位索引。</summary>
    private static LocalUpload Failed(UploadFlowTests.Fixture test,string path,LocalFileStamp stamp)
    {
        var row=new LocalUpload(Guid.NewGuid().ToString("N"),test.Id,path,path,false,stamp.Key,1,"Failed",DateTimeOffset.UtcNow,Error:"original failure");
        test.Repository.SaveUpload(row);return test.Repository.Upload(row.Id)!;
    }
    /// <summary>只采样本用例目标的首次抛出，验证正常缺失未靠吞异常实现；其他线程的异常不纳入。</summary>
    private static async Task<IReadOnlyList<string>> ObserveMissing(string path,Func<Task> action)
    {
        var errors=new ConcurrentQueue<string>();
        void Observe(object? sender,FirstChanceExceptionEventArgs args)
        {if(args.Exception is FileNotFoundException or DirectoryNotFoundException&&args.Exception.Message.Contains(path,StringComparison.OrdinalIgnoreCase))errors.Enqueue(args.Exception.Message);}
        AppDomain.CurrentDomain.FirstChanceException+=Observe;
        try{await action();return errors.ToArray();}finally{AppDomain.CurrentDomain.FirstChanceException-=Observe;}
    }
}
