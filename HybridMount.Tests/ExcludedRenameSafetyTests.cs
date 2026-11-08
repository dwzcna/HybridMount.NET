using HybridMount.Common;
using HybridMount.Services.Mounting;
using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>身份、连接作用域、只读入口和退出保护，不修改正式挂载。</summary>
public sealed partial class ExcludedRenameFlowTests
{
    private sealed class Dispatcher:IUiDispatcher {public bool TryEnqueue(Action callback){callback();return true;}}
    [Fact(Timeout=90000)]
    public async Task RenamedFailedUploadClearsOnlyOwnedOldIssueProperty()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);var definition=test.Repository.LoadDefinitions().Single();var entry=test.Repository.Entries(test.Id).Single(x=>x.SourcePath=="123.txt");
        await CloudItemStateProperty.SetAsync(definition,file.Old,false,CloudUploadIssueProperty.PropertyId,"原上传失败",ShellStatusIconResources.Issue,CancellationToken.None);var stamp=LocalFileState.Read(file.Old);
        var upload=new LocalUpload(Guid.NewGuid().ToString("N"),test.Id,"123.txt","123.txt",false,stamp.Key,1,"Failed",DateTimeOffset.UtcNow,entry.Id,entry.Version,Error:"原上传失败");test.Repository.SaveUpload(upload);
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test);
        var properties=CloudCustomStateReader.Read(file.New);Assert.DoesNotContain("1",properties.Ids);Assert.Contains("2",properties.Ids);Assert.Equal(file.Bytes,test.Dav.Objects["123.bak"].Bytes);Assert.False(test.Manager.RetryUpload(upload.Id));Assert.Throws<IOException>(()=>test.Manager.UploadAsCopy(upload.Id,"copy.txt"));
    }
    [Fact(Timeout=90000)]
    public async Task ReplacementAtLocalTargetCannotAuthorizeRemoteMove()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.ExcludedRenameCheckpointForTests=async(stage,_)=>{if(stage=="BeforeMove"){entered.TrySetResult();await release.Task;}};
        try{File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));File.Delete(file.New);await File.WriteAllTextAsync(file.New,"replacement");Assert.NotEqual(file.Identity,LocalFileState.Read(file.New).Identity);}
        finally{release.TrySetResult();}
        await Wait(test,"Conflict");Assert.True(test.Dav.Objects.ContainsKey("123.txt"));Assert.Equal("replacement",await File.ReadAllTextAsync(file.New));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task ChangedCredentialScopeStopsRecoveredIntent()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.DisposeAsync();
        var definition=test.Repository.LoadDefinitions().Single();test.Repository.SaveDefinition(definition with{CredentialRevision=definition.CredentialRevision+1});test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await Wait(test,"Conflict");
        Assert.True(test.Dav.Objects.ContainsKey("123.txt"));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task CancelAfterConfirmedMoveKeepsCloudDestination()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,false);var entered=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.ExcludedRenameCheckpointForTests=async(stage,row)=>{if(stage=="Moved"){entered.TrySetResult(row.Id);await release.Task;}};
        try{File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var id=await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));Assert.True(test.Manager.Cancel(id));}
        finally{release.TrySetResult();}
        await Wait(test,"Canceled");Assert.True(test.Dav.Objects.ContainsKey("123.bak"));Assert.False(test.Dav.Objects.ContainsKey("123.txt"));Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));Assert.Single(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task TaskViewKeepsIssueAfterHistoryClearAndRecheckDoesNotDownload()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,false);var handler=test.Dav.Server.Handler;
        test.Dav.Server.Handler=r=>r.Method=="GET"&&r.Target.EndsWith("/123.bak",StringComparison.Ordinal)?Task.FromResult(new HttpTestServer.Response(403,[])):handler(r);
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var failed=await Wait(test,"Failed");test.Dav.Server.Handler=handler;
        using var vm=new TasksViewModel(test.Manager,new Dispatcher()){IssuesOnly=true};vm.SelectedJobId=failed.Id;Assert.Equal(file.New,vm.SelectedLocation());await vm.ClearAllAsync();Assert.Contains(vm.Operations,x=>x.Id==failed.Id);
        var gets=test.Dav.Server.Requests.Count(x=>x.Method=="GET"&&x.Target.EndsWith("/123.bak",StringComparison.Ordinal));var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Dav.Server.Handler=async r=>{if(r.Method=="PROPFIND"&&r.Target.EndsWith("/123.bak",StringComparison.Ordinal)){entered.TrySetResult();await release.Task;}return await handler(r);};
        try{vm.RecheckSelected();await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(test.Manager.RetryUpload(failed.Id));}
        finally{release.TrySetResult();}await Task.Delay(350);test.Dav.Server.Handler=handler;
        Assert.Equal(gets,test.Dav.Server.Requests.Count(x=>x.Method=="GET"&&x.Target.EndsWith("/123.bak",StringComparison.Ordinal)));Assert.False(CloudFilesNative.HasFullContent(file.New,file.Bytes.Length));Assert.False(await vm.BeginConflictAsync());vm.RetrySelected();await Wait(test);Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));
    }
    [Fact(Timeout=90000)]
    public async Task ServerIgnoringOverwriteFlagFailsProbeBeforeUserMove()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);var handler=test.Dav.Server.Handler;
        test.Dav.Server.Handler=r=>
        {
            if(r.Method=="MOVE"&&r.Target.Contains("hybridmount-probe-",StringComparison.Ordinal))
            {
                var path=Uri.UnescapeDataString(new Uri(test.Dav.Server.Address,r.Target).AbsolutePath[5..]);var destination=Uri.UnescapeDataString(new Uri(r.Headers["Destination"]).AbsolutePath[5..]);
                if(test.Dav.Objects.TryGetValue(path,out var item)&&r.Headers.GetValueOrDefault("If-Match")==item.ETag&&test.Dav.Objects.ContainsKey(destination))
                {test.Dav.Objects[destination]=item;test.Dav.Objects.TryRemove(path,out _);return Task.FromResult(new HttpTestServer.Response(204,[]));}
            }
            return handler(r);
        };
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test,"Conflict");Assert.True(test.Dav.Objects.ContainsKey("123.txt"));Assert.False(test.Dav.Objects.ContainsKey("123.bak"));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
}
