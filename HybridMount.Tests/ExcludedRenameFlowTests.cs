using System.Text;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>隔离NTFS根和回环DAV验证退出同步、条件删除及新同名对象；不连接正式服务。</summary>
public sealed partial class ExcludedRenameFlowTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private async Task<(string Old,string New,byte[] Bytes,string Identity)> Prepare(UploadFlowTests.Fixture test,int size,bool hydrated)
    {
        output.WriteLine("OwnedRoot="+System.Text.Json.JsonSerializer.Serialize(test.Repository.LoadDefinitions().Single()));
        await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);var bytes=Enumerable.Range(0,size).Select(i=>(byte)(i%251)).ToArray();test.Dav.Put("123.txt",bytes);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var old=Path.Combine(test.Local,"123.txt");if(hydrated)await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[old]));
        return(old,Path.Combine(test.Local,"123.bak"),bytes,LocalFileState.Read(old).Identity);
    }
    private async Task<MountOperationSnapshot> Wait(UploadFlowTests.Fixture test,string state="Completed")
    {
        var deadline=DateTime.UtcNow.AddSeconds(30);MountOperationSnapshot? row=null;
        while(DateTime.UtcNow<deadline)
        {
            row=test.Manager.Operations.FirstOrDefault(x=>x.Operation=="excludedRename"&&x.State!="Superseded");if(row?.State==state)return row;
            if(row?.State is "Failed" or "Conflict"&&state=="Completed"){Dump(test);throw new IOException(row.IssueDetails+"\n"+string.Join("\n",row.Results.Select(x=>x.Message)));}
            await Task.Delay(40);
        }
        Dump(test);output.WriteLine(await test.Manager.ReadDiagnosticsAsync(test.Id));throw new TimeoutException(System.Text.Json.JsonSerializer.Serialize(row));
    }
    private void Dump(UploadFlowTests.Fixture test)
    {
        output.WriteLine("Root="+test.Local);output.WriteLine(System.Text.Json.JsonSerializer.Serialize(test.Repository.Uploads(test.Id)));
        foreach(var request in test.Dav.Server.Requests.Where(x=>x.Target.EndsWith("/123.txt",StringComparison.Ordinal)))output.WriteLine(request.Method+" "+request.Target+" "+System.Text.Json.JsonSerializer.Serialize(request.Headers));
    }
    [Theory(Timeout=90000)]
    [InlineData(0,false)][InlineData(512,false)][InlineData(512,true)]
    public async Task RenameIntoExclusionMovesRemoteVersionAndKeepsLocalFile(int size,bool hydrated)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,size,hydrated);File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        var task=await Wait(test);Assert.False(test.Dav.Objects.ContainsKey("123.txt"));Assert.Equal(file.Bytes,test.Dav.Objects["123.bak"].Bytes);Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));Assert.Null(CloudFilesNative.Inspect(file.New));Assert.Equal(file.Identity,LocalFileState.Read(file.New).Identity);Assert.Equal(9u,CloudShellState.Read(file.New).ProviderState);
        var deletion=Assert.Single(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));Assert.True(deletion.Headers.ContainsKey("If-Match"));Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("/123.bak",StringComparison.Ordinal));
        Assert.DoesNotContain(test.Repository.Entries(test.Id),x=>x.SourcePath=="123.txt");Assert.Contains(test.Repository.Entries(test.Id),x=>x.SourcePath=="123.bak");await test.Manager.ClearOperationHistoryAsync(TaskHistoryClearMode.All);Assert.Equal("Completed",test.Manager.GetOperation(task.Id)!.State);
    }
    [Fact(Timeout=90000)]
    public async Task NewFileAtOriginalNameWaitsForMoveAndUsesFreshCreateBaseline()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,32,true);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.ExcludedRenameCheckpointForTests=async(stage,_)=>{if(stage=="BeforeMove"){entered.TrySetResult();await release.Task;}};
        try
        {
            File.Move(file.Old,file.New);await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await File.WriteAllTextAsync(file.Old,"fresh new file");await test.Manager.ScanUploadChangesAsync(test.Id);Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));
        }
        finally{release.TrySetResult();}
        await Wait(test);await test.Wait("123.txt",bytes:Encoding.UTF8.GetBytes("fresh new file"));Assert.Equal(file.Bytes,await File.ReadAllBytesAsync(file.New));
        var put=Assert.Single(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));Assert.Equal("*",put.Headers["If-None-Match"]);
    }
    [Fact(Timeout=90000)]
    public async Task SavingRulesAloneDoesNotDeleteExistingRemoteContent()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=await Prepare(test,16,false);await test.Manager.SetFileExclusionRulesAsync(test.Id,[new(FileExclusionMatchKind.Suffix,".txt")]);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.True(test.Dav.Objects.ContainsKey("123.txt"));Assert.DoesNotContain(test.Manager.Operations,x=>x.Operation=="excludedRename");Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="MOVE"&&x.Target.EndsWith("/123.txt",StringComparison.Ordinal));Assert.NotNull(CloudFilesNative.Inspect(file.Old));
    }
}
