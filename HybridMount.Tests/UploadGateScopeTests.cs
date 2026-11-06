using System.Security.Cryptography;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class UploadGateScopeTests
{
    [Fact(Timeout=20000)]
    public async Task GateUserProceedsWhileActualSnapshotCopyIsPaused()
    {
        await using var test=new SnapshotFixture(3*1024*1024);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var paused=false;
        var copy=test.Run(async()=>{if(!paused){paused=true;entered.SetResult();await release.Task;}});
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(await test.Gate.WaitAsync(TimeSpan.FromSeconds(2)));test.Gate.Release();}finally{release.TrySetResult();}
        var result=await copy;Assert.NotNull(result);Assert.Equal("Prepared",result.State);Assert.Equal(result.Generation,result.SnapshotGeneration);Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(test.Path))),result.SnapshotSha256);Assert.Equal(await File.ReadAllBytesAsync(test.Path),await File.ReadAllBytesAsync(test.Snapshot));
    }
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(262145)]
    public async Task NormalAndEmptySnapshotsPublishCapturedGeneration(int size)
    {
        await using var test=new SnapshotFixture(size);var result=await test.Run();Assert.NotNull(result);Assert.Equal(1,result.SnapshotGeneration);Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(test.Path))),result.SnapshotSha256);Assert.Equal(size,new FileInfo(test.Snapshot).Length);
    }
    [Theory]
    [InlineData("generation")][InlineData("state")][InlineData("attempt")][InlineData("cancel")][InlineData("baseline")][InlineData("replacement")][InlineData("edit")]
    public async Task StaleAttemptCannotPublishOrEraseCurrentRow(string change)
    {
        await using var test=new SnapshotFixture(64);LocalUpload? expected=null;
        var result=await test.Run(afterCopy:()=>
        {
            if(change=="replacement"){File.Delete(test.Path);File.WriteAllBytes(test.Path,new byte[64]);}
            if(change=="edit")File.WriteAllBytes(test.Path,[9]);
            expected=test.Repository.UpdateUpload(test.Item.Id,x=>change switch
            {
                "generation"=>x with{Generation=x.Generation+1},"state"=>x with{State="Conflict",Error="preserve"},"attempt"=>x with{SnapshotAttemptId="another-attempt"},
                "cancel"=>x with{CancelRequested=true,State="Canceled"},"baseline"=>x with{ExpectedVersion="changed"},_=>x
            });
            return Task.CompletedTask;
        });
        Assert.Null(result);Assert.Equal(expected,test.Repository.Upload(test.Item.Id));Assert.False(File.Exists(test.Snapshot));Assert.Equal("previous",await File.ReadAllTextAsync(test.Previous));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CopyErrorOrCancellationReleasesGateAndPreservesPriorSnapshot(bool cancel)
    {
        await using var test=new SnapshotFixture(262144);using var stop=new CancellationTokenSource();
        var operation=test.Run(()=>{if(cancel){stop.Cancel();stop.Token.ThrowIfCancellationRequested();}throw new IOException("copy failed");},token:stop.Token);
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>operation);else await Assert.ThrowsAsync<IOException>(()=>operation);
        Assert.False(File.Exists(test.Snapshot));Assert.Equal("previous",await File.ReadAllTextAsync(test.Previous));Assert.Equal(test.Item,test.Repository.Upload(test.Item.Id));Assert.True(test.Gate.Wait(0));test.Gate.Release();
    }
    [Theory(Timeout=20000)]
    [InlineData("PROPFIND",false)][InlineData("PROPFIND",true)][InlineData("MKCOL",false)][InlineData("MKCOL",true)]
    public async Task ParentNetworkDoesNotHoldGateAndLateInvalidationStopsPreparation(string method,bool invalidate)
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();using var gate=new SemaphoreSlim(1,1);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var handle=fixture.Server.Handler;var invalid=false;
        fixture.Server.Handler=async request=>{if(request.Method==method&&request.Target.TrimEnd('/').EndsWith("/parent",StringComparison.Ordinal)){entered.TrySetResult();await release.Task;}return await handle(request);};
        var item=Item("parent/file");
        async Task Validate(string? parent){await gate.WaitAsync();try{if(invalid)throw new UploadPreparationChangedException();}finally{gate.Release();}}
        var operation=UploadPreparation.PrepareParentsAsync(source,item,Validate,CancellationToken.None);
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));try{invalid=invalidate;}finally{gate.Release();}}finally{release.TrySetResult();}
        if(invalidate)await Assert.ThrowsAsync<UploadPreparationChangedException>(()=>operation);else await operation;
        if(invalidate&&method=="PROPFIND")Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="MKCOL");else Assert.Single(fixture.Server.Requests,r=>r.Method=="MKCOL");
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method is "PUT" or "DELETE");
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task MissingKnownOrUpdateParentNeverGetsRecreated(bool update)
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();fixture.Put("parent/file",[1]);var handle=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Target.EndsWith("/parent",StringComparison.Ordinal)?Task.FromResult(new HttpTestServer.Response(404,[])):handle(request);
        var item=Item("parent/file") with{ExpectedVersion=update?"baseline":null};
        await Assert.ThrowsAsync<SourceConflictException>(()=>UploadPreparation.PrepareParentsAsync(source,item,parent=>parent is not null&&!update?Task.FromException(new SourceConflictException("known parent")):Task.CompletedTask,CancellationToken.None));Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="MKCOL");
    }
    [Fact]
    public async Task ParentlessCreateDoesNoNetworkAndStillRevalidates()
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();var validations=0;
        await UploadPreparation.PrepareParentsAsync(source,Item("file"),_=>{validations++;return Task.CompletedTask;},CancellationToken.None);Assert.Equal(2,validations);Assert.Empty(fixture.Server.Requests);
    }
    [Fact]
    public async Task ParentMetadataFailureNeverIssuesMkcol()
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();fixture.Server.Handler=_=>Task.FromResult(new HttpTestServer.Response(503,[]));
        await Assert.ThrowsAnyAsync<IOException>(()=>UploadPreparation.PrepareParentsAsync(source,Item("parent/file"),_=>Task.CompletedTask,CancellationToken.None));Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="MKCOL");
    }
    private static LocalUpload Item(string path)=>new(Guid.NewGuid().ToString("N"),Guid.NewGuid(),path,path,false,"stamp",1,"Queued",DateTimeOffset.UtcNow,SnapshotAttemptId:Guid.NewGuid().ToString("N"));
    private sealed class SnapshotFixture:IAsyncDisposable
    {
        private readonly string _area=Directory.CreateTempSubdirectory("HybridMount.UploadGate.").FullName;
        private readonly string _registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        internal readonly SemaphoreSlim Gate=new(1,1);
        internal MountRepository Repository {get;}
        internal string Path=>System.IO.Path.Combine(_area,"file");
        internal string Snapshot=>System.IO.Path.Combine(_area,"attempt.snapshot");
        internal string Previous=>System.IO.Path.Combine(_area,"previous.snapshot");
        internal LocalUpload Item {get;}
        internal SnapshotFixture(int size)
        {
            File.WriteAllBytes(Path,Enumerable.Repeat((byte)7,size).ToArray());File.WriteAllText(Previous,"previous");Repository=new(System.IO.Path.Combine(_area,"data"),_registry);Item=UploadGateScopeTests.Item("file") with{SnapshotPath=Previous};Repository.SaveUpload(Item);
        }
        internal Task<LocalUpload?> Run(Func<Task>? checkpoint=null,Func<Task>? afterCopy=null,CancellationToken token=default)
        {
            LocalFileStamp? stamp=null;string? digest=null;var accepted=false;
            return UploadPreparation.RunAsync(Gate,()=>{stamp=LocalFileState.Read(Path);return true;},async()=>
            {
                digest=await UploadPreparation.CopyAsync(Path,Snapshot,stamp!,token,checkpoint);if(afterCopy is not null)await afterCopy();
            },()=>
            {
                if(LocalFileState.Read(Path).Key!=stamp!.Key)return null;
                var result=UploadPreparation.PublishAttempt(Item,update=>Repository.UpdateUpload(Item.Id,update),current=>current with
                {State="Prepared",SnapshotAttemptId=null,SnapshotPath=Snapshot,SnapshotSha256=digest,SnapshotStamp=stamp.Key,SnapshotIdentity=stamp.Identity,SnapshotGeneration=Item.Generation});
                accepted=result is not null;return result;
            },()=>{if(!accepted&&File.Exists(Snapshot))File.Delete(Snapshot);},token);
        }
        public ValueTask DisposeAsync(){Gate.Dispose();Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_registry,false);Directory.Delete(_area,true);return ValueTask.CompletedTask;}
    }
}
