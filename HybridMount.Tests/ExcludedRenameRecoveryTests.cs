using System.Text;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

public sealed partial class ExcludedRenameFlowTests
{
    [Fact(Timeout=90000)]
    public async Task LostMoveResponseKeepsUnknownWithoutSecondMove()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);var handler=test.Dav.Server.Handler;var lost=false;
        test.Dav.Server.Handler=async request=>{var response=await handler(request);if(!lost&&request.Method=="MOVE"&&request.Target.EndsWith("/123.txt",StringComparison.Ordinal)){lost=true;throw new IOException("模拟移动响应丢失");}return response;};
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var row=await Wait(test,"OutcomeUnknown");
        Assert.True(lost);Assert.Single(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));Assert.Equal(file.Bytes,test.Dav.Objects["123.bak"].Bytes);Assert.False(test.Dav.Objects.ContainsKey("123.txt"));Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));
        var recheckEnd=DateTime.UtcNow.AddSeconds(5);while(!test.Manager.RecheckExcludedRename(row.Id)){if(DateTime.UtcNow>=recheckEnd)throw new TimeoutException("核对未退出");await Task.Delay(20);}await Task.Delay(300);Assert.Single(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));Assert.Equal("OutcomeUnknown",test.Manager.GetOperation(row.Id)!.State);
    }
    [Theory(Timeout=90000)] [InlineData(401)] [InlineData(403)]
    public async Task MoveRejectionKeepsLocalFileAndShowsPersistentIssue(int status)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,false);var handler=test.Dav.Server.Handler;
        test.Dav.Server.Handler=request=>request.Method=="MOVE"&&request.Target.EndsWith("/123.txt",StringComparison.Ordinal)?Task.FromResult(new HttpTestServer.Response(status,[])):handler(request);
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var failed=await Wait(test,"Failed");
        Assert.True(test.Dav.Objects.ContainsKey("123.txt"));Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));Assert.NotNull(CloudFilesNative.Inspect(file.New));Assert.Contains(test.Manager.UploadIssues,x=>x.Id==failed.Id);Assert.True(test.Manager.Mounts.Single().UploadIssueCount>0);
        await test.Manager.ClearOperationHistoryAsync(TaskHistoryClearMode.All);Assert.Contains(test.Manager.SyncIssues,x=>x.Id==failed.Id);
        var before=test.Dav.Server.Requests.Count(x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));Assert.True(test.Manager.RecheckExcludedRename(failed.Id));
        var deadline=DateTime.UtcNow.AddSeconds(5);while(test.Manager.GetOperation(failed.Id)?.Results.First().Message.Contains("只读核对：",StringComparison.Ordinal)!=true&&DateTime.UtcNow<deadline)await Task.Delay(25);
        Assert.Equal(before,test.Dav.Server.Requests.Count(x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal)));Assert.Equal("Failed",test.Manager.GetOperation(failed.Id)!.State);
        test.Dav.Server.Handler=handler;var ready=DateTime.UtcNow.AddSeconds(5);while(!test.Manager.RetryUpload(failed.Id)){if(DateTime.UtcNow>=ready)throw new TimeoutException("任务仍忙");await Task.Delay(20);}Assert.False(test.Manager.RetryUpload(failed.Id));await Wait(test);
    }
    [Fact(Timeout=90000)]
    public async Task RemoteVersionChangedAtMoveBoundaryIsNeverAdoptedByRetry()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,32,true);var changed=false;
        test.Manager.ExcludedRenameCheckpointForTests=(stage,_)=>{if(stage=="BeforeMove"&&!changed){changed=true;test.Dav.Put("123.txt",Encoding.UTF8.GetBytes("another client"));}return Task.CompletedTask;};
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var task=await Wait(test,"Conflict");
        Assert.Equal("another client",Encoding.UTF8.GetString(test.Dav.Objects["123.txt"].Bytes));Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));Assert.True(test.Manager.RetryUpload(task.Id));await Wait(test,"Conflict");
        Assert.Equal("another client",Encoding.UTF8.GetString(test.Dav.Objects["123.txt"].Bytes));
    }
    [Fact(Timeout=90000)]
    public async Task InFlightCreateCommitsReceiptBeforeMovingAndNeverRepeatsPut()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
        var path=Path.Combine(test.Local,"123.txt");var target=Path.Combine(test.Local,"123.bak");var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.UploadCheckpointForTests=async(stage,item)=>{if(item.LocalPath=="123.txt"&&stage=="RemoteCommitted"){entered.TrySetResult();await release.Task;}};
        try{await File.WriteAllTextAsync(path,"written once");await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));File.Move(path,target);await test.Manager.ScanUploadChangesAsync(test.Id);}
        finally{release.TrySetResult();}
        await Wait(test);Assert.False(test.Dav.Objects.ContainsKey("123.txt"));Assert.Equal("written once",await File.ReadAllTextAsync(target));Assert.Single(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));Assert.Single(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task UnsentOrdinaryFileNeverDeletesForeignSameName()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var old=Path.Combine(test.Local,"123.txt");var target=Path.Combine(test.Local,"123.bak");
        await File.WriteAllTextAsync(old,"only local");await test.Manager.ScanUploadChangesAsync(test.Id);test.Dav.Put("123.txt",Encoding.UTF8.GetBytes("foreign remote"));File.Move(old,target);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test);
        Assert.Equal("foreign remote",Encoding.UTF8.GetString(test.Dav.Objects["123.txt"].Bytes));Assert.Equal("only local",await File.ReadAllTextAsync(target));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Theory(Timeout=90000)] [InlineData(false)] [InlineData(true)]
    public async Task CancelOrRenameBackBeforeDeleteKeepsRemoteAndPreservesContent(bool renameBack)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);var entered=new TaskCompletionSource<ExcludedRenameIntent>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.ExcludedRenameCheckpointForTests=async(stage,row)=>{if(stage=="BeforeMove"){entered.TrySetResult(row);await release.Task;}};
        try
        {
            File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var row=await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if(renameBack){File.Move(file.New,file.Old);await test.Manager.ScanUploadChangesAsync(test.Id);}else Assert.True(test.Manager.Cancel(row.Id));
        }
        finally{release.TrySetResult();}
        await Wait(test,"Canceled");Assert.True(test.Dav.Objects.ContainsKey("123.txt"));Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(renameBack?file.Old:file.New));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task RestartAfterRemoteMoveResumesOnlyLocalDownload()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);Task? stop=null;
        test.Manager.ExcludedRenameCheckpointForTests=async(stage,_)=>{if(stage=="Moved"){entered.TrySetResult();await release.Task;}};
        try
        {
            File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));stop=test.Manager.DisconnectAsync(test.Id);
        }
        finally{release.TrySetResult();if(stop is not null)await stop;}
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await Wait(test);
        Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));Assert.Single(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task LegacyEvidenceRecoversOnlyTheExactExcludedRename()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Manager.ObserveExcludedRenamesForTests=false;var file=await Prepare(test,64,true);
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);Assert.DoesNotContain(test.Manager.Operations,x=>x.Operation=="excludedRename");Assert.True(test.Dav.Objects.ContainsKey("123.txt"));await test.Manager.DisposeAsync();
        File.Delete(Path.Combine(test.Repository.DataDirectory,"ExcludedRenames",test.Id.ToString("N")+".json"));test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await Wait(test);
        Assert.Equal(file.Identity,LocalFileState.Read(file.New).Identity);Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));Assert.False(test.Dav.Objects.ContainsKey("123.txt"));
    }
}
