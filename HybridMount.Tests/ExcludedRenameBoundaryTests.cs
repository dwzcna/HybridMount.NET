using System.Text;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>改名前后内容、下载失败及连续事件边界；全部使用私有回环服务和隔离根。</summary>
public sealed partial class ExcludedRenameFlowTests
{
    [Fact(Timeout=90000)]
    public async Task LocalEditsAreKeptButNeverPublishedAtExcludedName()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);
        await File.WriteAllTextAsync(file.Old,"local unsent edits");await test.Manager.ScanUploadChangesAsync(test.Id);File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test);
        Assert.Equal(file.Bytes,test.Dav.Objects["123.bak"].Bytes);Assert.Equal("local unsent edits",await File.ReadAllTextAsync(file.New));Assert.Equal(file.Identity,LocalFileState.Read(file.New).Identity);
        await File.WriteAllTextAsync(file.New,"later local edit");await test.Manager.ScanUploadChangesAsync(test.Id);await Task.Delay(400);
        Assert.Equal(file.Bytes,test.Dav.Objects["123.bak"].Bytes);Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("/123.bak",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task LaterRemoteChangeIsConflictAndDoesNotReplaceLocalFile()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,false);File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test);
        test.Dav.Put("123.bak",Encoding.UTF8.GetBytes("remote later version"));await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));Assert.Contains(test.Repository.AllRemotePending(test.Id),x=>x.Path=="123.bak"&&x.State=="Conflict");Assert.Contains(test.Manager.SyncIssues,x=>x.RemotePath=="123.bak");
    }
    [Fact(Timeout=90000)]
    public async Task ExistingDestinationIsNeverOverwritten()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);test.Dav.Put("123.bak",Encoding.UTF8.GetBytes("foreign"));
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test,"Conflict");
        Assert.Equal("foreign",Encoding.UTF8.GetString(test.Dav.Objects["123.bak"].Bytes));Assert.True(test.Dav.Objects.ContainsKey("123.txt"));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task UnreliableMoveConditionsKeepRemoteSource()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,false);test.Dav.IgnoreConditions=true;
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test,"Conflict");
        Assert.True(test.Dav.Objects.ContainsKey("123.txt"));Assert.False(test.Dav.Objects.ContainsKey("123.bak"));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=90000)]
    public async Task FailedDownloadRetriesLocalWorkWithoutRepeatingMove()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,false);var handler=test.Dav.Server.Handler;
        test.Dav.Server.Handler=r=>r.Method=="GET"&&r.Target.EndsWith("/123.bak",StringComparison.Ordinal)?Task.FromResult(new HttpTestServer.Response(403,[])):handler(r);
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var failed=await Wait(test,"Failed");
        Assert.True(test.Dav.Objects.ContainsKey("123.bak"));Assert.NotNull(CloudFilesNative.Inspect(file.New));Assert.False(CloudFilesNative.HasFullContent(file.New,file.Bytes.Length));
        test.Dav.Server.Handler=handler;Assert.True(test.Manager.RetryUpload(failed.Id));await Wait(test);Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));Assert.Single(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
    }
    [Theory(Timeout=90000)] [InlineData(false)] [InlineData(true)]
    public async Task ConsecutiveExcludedRenameKeepsLatestRemoteName(bool afterMove)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,64,true);var latest=Path.Combine(test.Local,"second.bak");var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var once=false;
        test.Manager.ExcludedRenameCheckpointForTests=async(stage,_)=>{if(!once&&stage==(afterMove?"Moved":"BeforeMove")){once=true;entered.TrySetResult();await release.Task;}};
        try{File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));File.Move(file.New,latest);await test.Manager.ScanUploadChangesAsync(test.Id);}
        finally{release.TrySetResult();}
        await Wait(test);Assert.Equal(file.Bytes,test.Dav.Objects["second.bak"].Bytes);Assert.False(test.Dav.Objects.ContainsKey("123.txt"));Assert.False(test.Dav.Objects.ContainsKey("123.bak"));Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(latest));Assert.Null(CloudFilesNative.Inspect(latest));
    }
    [Fact(Timeout=90000)]
    public async Task PartialPlaceholderIsCompletedFromNewRemotePath()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,2*1024*1024,false);
        await File.ReadAllBytesAsync(file.Old);
        NativeTestFiles.Dehydrate(file.Old,65536,file.Bytes.Length-65536);
        Assert.False(CloudFilesNative.HasFullContent(file.Old,file.Bytes.Length));Assert.True(CloudFilesNative.Inspect(file.Old)!.Value.Info.OnDiskSize>0);
        File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Wait(test);
        Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));Assert.Null(CloudFilesNative.Inspect(file.New));Assert.Equal(file.Identity,LocalFileState.Read(file.New).Identity);Assert.Contains(test.Dav.Server.Requests,x=>x.Method=="GET"&&x.Target.EndsWith("/123.bak",StringComparison.Ordinal));
    }
}
