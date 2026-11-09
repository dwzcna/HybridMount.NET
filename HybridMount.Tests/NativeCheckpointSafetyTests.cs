using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>真实隔离正文替换在许可、写入和提交阶段注入取消；保护恢复备份、文件身份和安全落库。</summary>
[Collection("Native IPC")]
public sealed class NativeCheckpointSafetyTests
{
    [Theory(Timeout=90000)]
    [InlineData("beginWrite",false)][InlineData("beginWrite",true)][InlineData("writing",false)][InlineData("writing",true)][InlineData("commit",false)][InlineData("commit",true)]
    public async Task ConflictReplacementCancellationKeepsContentAndJournalConsistent(string point,bool after)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);test.Dav.Put("文件.bin",System.Text.Encoding.UTF8.GetBytes("remote decision"));using(var content=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){var bytes=System.Text.Encoding.UTF8.GetBytes("local decision");await content.WriteAsync(bytes);content.SetLength(bytes.Length);await content.FlushAsync();}await test.Manager.ScanUploadChangesAsync(test.Id);
        var upload=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="文件.bin");upload=test.Repository.UpdateUpload(upload.Id,x=>x with{State="Conflict",Error="test conflict"})!;var comparison=await test.Manager.CompareConflictAsync(upload.Id);var identity=LocalFileState.Read(path).Identity;var pin=CloudFilesNative.Inspect(path)!.Value.Info.PinState;
        var runtime=await NativeRuntimeClient.GetAsync();using var cancel=new CancellationTokenSource();var observed=false;
        runtime.CheckpointForTests=(mount,stage,safety,completed,token)=>{if(mount==test.Id&&stage==point&&completed==after){observed=true;cancel.Cancel();if(safety)Assert.False(token.IsCancellationRequested);}return Task.CompletedTask;};
        try{try{await test.Manager.ResolveConflictAsync(comparison,false,cancel.Token);}catch(OperationCanceledException)when(cancel.IsCancellationRequested){}}
        finally{runtime.CheckpointForTests=null;}
        Assert.True(observed);var saved=test.Repository.Upload(upload.Id)!;
        if(point=="beginWrite"&&!after){Assert.Equal("local decision",await File.ReadAllTextAsync(path));Assert.NotNull(saved.Resolution);}
        else{Assert.Equal("remote decision",await File.ReadAllTextAsync(path));Assert.Equal("Completed",saved.State);Assert.Null(saved.Resolution);Assert.Equal("local decision",await File.ReadAllTextAsync(saved.ConflictRecoveryPath!));Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);}
        Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(pin,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
    }
    /// <summary>挂载停止排空属于本根的已接纳检查点，拥有者退出之前处理器仍可完成，退出后无迟到持久写入。</summary>
    [Fact(Timeout=30000)]
    public async Task MountDisposalRetainsAdmittedCheckpointOwnerUntilItReturns()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var runtime=await NativeRuntimeClient.GetAsync();var mount=test.Manager.Mounts.Single().Definition;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var disposed=false;var writes=0;
        var request=runtime.InvokeAsync("testManagedCheckpoint",NativeRuntimeArguments.Root(mount),async(_,_,token)=>{entered.TrySetResult();await release.Task;Assert.False(disposed);test.Repository.SaveOperation(new("checkpoint-owner-test","refresh","Completed",0,0,[],null,MountId:test.Id));Interlocked.Increment(ref writes);return null;});
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var stopping=test.Manager.DisposeAsync().AsTask();await Task.Delay(100);Assert.False(stopping.IsCompleted);release.TrySetResult();await request;await stopping.WaitAsync(TimeSpan.FromSeconds(10));disposed=true;Assert.Equal(1,writes);await Task.Delay(50);Assert.Equal(1,writes);}
        finally{release.TrySetResult();await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);}
    }
    [Theory(Timeout=90000)]
    [InlineData("remoteCommitting",false)][InlineData("remoteCommitting",true)][InlineData("remoteHydrating",false)][InlineData("remoteHydrating",true)][InlineData("remoteCompleted",false)][InlineData("remoteCompleted",true)]
    public async Task PinnedRemoteUpdateCancellationKeepsDurableStageAndPin(string point,bool after)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.SetRemoteDetectionAsync(test.Id,false,60,300);var path=Path.Combine(test.Local,"文件.bin");await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));var original=await File.ReadAllBytesAsync(path);var identity=LocalFileState.Read(path).Identity;var version=test.Repository.Entries(test.Id).Single(x=>x.LocalPath=="文件.bin").Version;var bytes=System.Text.Encoding.UTF8.GetBytes("updated pinned content");test.Dav.Put("文件.bin",bytes);
        var runtime=await NativeRuntimeClient.GetAsync();var ready=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);var observed=false;
        runtime.CheckpointForTests=async(mount,stage,safety,completed,token)=>{if(mount!=test.Id||stage!=point||completed!=after)return;var job=await ready.Task;observed=true;Assert.True(test.Manager.Cancel(job));if(safety)Assert.False(token.IsCancellationRequested);};
        try{var job=test.Manager.Queue(test.Id,"refresh");ready.SetResult(job);await WebDavTests.Finished(test.Manager,job);}
        finally{runtime.CheckpointForTests=null;}
        Assert.True(observed);Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
        if(point=="remoteCommitting"&&!after){Assert.Equal(original,await File.ReadAllBytesAsync(path));Assert.Equal(version,test.Repository.Entries(test.Id).Single(x=>x.LocalPath=="文件.bin").Version);}
        else{Assert.Equal(bytes,await File.ReadAllBytesAsync(path));Assert.NotEqual(version,test.Repository.Entries(test.Id).Single(x=>x.LocalPath=="文件.bin").Version);Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);Assert.Contains(test.Repository.AllRemotePending(test.Id),x=>x.Path=="文件.bin"&&x.State=="Completed");}
    }
    [Theory(Timeout=60000)]
    [InlineData("propertyBegin",false)][InlineData("propertyBegin",true)][InlineData("propertyWritten",false)][InlineData("propertyWritten",true)][InlineData("propertyCommit",false)][InlineData("propertyCommit",true)][InlineData("propertyProof",false)][InlineData("propertyProof",true)]
    public async Task PropertySafetyAndProofSurviveCallerCancellation(string point,bool after)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"issue-cancel.txt");await File.WriteAllTextAsync(path,"unchanged bytes");await test.Manager.ScanUploadChangesAsync(test.Id);var row=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="issue-cancel.txt");test.Repository.UpdateUpload(row.Id,x=>x with{State="Failed",Error="original failure"});var identity=LocalFileState.Read(path).Identity;var requests=test.Dav.Server.Requests.Count;using var cancel=new CancellationTokenSource();var runtime=await NativeRuntimeClient.GetAsync();var observed=false;
        runtime.CheckpointForTests=(mount,stage,safety,completed,token)=>{if(mount!=test.Id)return Task.CompletedTask;if(point=="propertyProof"&&stage=="propertyWritten"&&!completed)throw new IOException("test interrupted property write");if(stage==point&&completed==after){observed=true;cancel.Cancel();if(safety)Assert.False(token.IsCancellationRequested);}return Task.CompletedTask;};
        try{try{await test.Manager.PublishUploadIssuesAsync(cancel.Token);}catch(OperationCanceledException)when(cancel.IsCancellationRequested){}}
        finally{runtime.CheckpointForTests=null;}
        Assert.True(observed);var saved=test.Repository.Upload(row.Id)!;
        if(point=="propertyBegin"&&!after)Assert.DoesNotContain("1",CloudCustomStateReader.Read(path).Ids);
        else{Assert.Contains("1",CloudCustomStateReader.Read(path).Ids);Assert.Equal(point=="propertyProof"?"Publishing":"Published",saved.Attention!.ShellState);}
        Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal("unchanged bytes",await File.ReadAllTextAsync(path));Assert.Null(CloudFilesNative.Inspect(path));Assert.Equal(row.Generation,saved.Generation);Assert.Equal(requests,test.Dav.Server.Requests.Count);
    }
    [Theory(Timeout=60000)]
    [InlineData("exclusionBegin",false)][InlineData("exclusionBegin",true)][InlineData("exclusionCommit",false)][InlineData("exclusionCommit",true)][InlineData("exclusionProof",false)][InlineData("exclusionProof",true)]
    public async Task ExclusionSafetyAndProofKeepOwnedPinAndRecovery(string point,bool after)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);var mount=test.Manager.Mounts.Single().Definition;var original=CloudFilesNative.Inspect(path)!.Value;var identity=LocalFileState.Read(path).Identity;var requests=test.Dav.Server.Requests.Count;var store=new FileExclusionMarkerStore(Path.Combine(test.Repository.DataDirectory,"cancel-exclusion"),test.Id);using var cancel=new CancellationTokenSource();var runtime=await NativeRuntimeClient.GetAsync();var observed=false;
        runtime.CheckpointForTests=(id,stage,safety,completed,token)=>{if(id!=test.Id)return Task.CompletedTask;if(point=="exclusionProof"&&stage=="exclusionCommit"&&!completed)throw new IOException("test interrupted exclusion commit");if(stage==point&&completed==after){observed=true;cancel.Cancel();if(safety)Assert.False(token.IsCancellationRequested);}return Task.CompletedTask;};
        try{try{var result=store.Reconcile(mount,"文件.bin",true,cancel.Token);if(point!="exclusionProof")Assert.Null(result.Failure);}catch(OperationCanceledException)when(cancel.IsCancellationRequested){}}
        finally{runtime.CheckpointForTests=null;}
        Assert.True(observed);var final=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(original.Identity,final.Identity);Assert.Equal(original.Info.InSyncState,final.Info.InSyncState);Assert.Equal(original.Info.OnDiskSize,final.Info.OnDiskSize);
        Assert.Equal(point=="exclusionBegin"&&!after?original.Info.PinState:3u,final.Info.PinState);Assert.True(store.HasMarks);store.Reconcile(mount,"文件.bin",false);Assert.Equal(original.Info.PinState,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Equal(requests,test.Dav.Server.Requests.Count);
    }
    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task PinReleasingSafetyAcknowledgesEvenWithoutIrreversibleWrite(bool after)
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.NativeRoot."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);var mount=new HybridMount.Models.Mounting.MountDefinition(Guid.NewGuid(),"pin safety",area,RootIdentity:MountPathGuard.FileIdentity(area));var entry=new HybridMount.Models.Mounting.MountEntry("zero","zero.bin","zero.bin","v1",false,false,false,true);var session=Guid.NewGuid();await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();using var cancel=new CancellationTokenSource();var registered=false;var connected=false;var observed=false;
        try
        {
            await runtime.InvokeAsync("registerRoot",NativeRuntimeArguments.Root(mount));registered=true;await runtime.InvokeAsync("connectRoot",NativeRuntimeArguments.Root(mount,new{sessionId=session,blockHydration=false}));connected=true;await runtime.InvokeAsync("createPlaceholder",NativeRuntimeArguments.Entry(mount,entry,new{size=0L,time=DateTimeOffset.UtcNow.ToFileTime()}));var path=Path.Combine(area,"zero.bin");await runtime.InvokeAsync("requestPin",NativeRuntimeArguments.Entry(mount,entry));
            runtime.CheckpointForTests=(id,stage,safety,completed,token)=>{if(stage=="pinReleasing"&&completed==after){Assert.True(safety);observed=true;cancel.Cancel();Assert.False(token.IsCancellationRequested);}return Task.CompletedTask;};
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>runtime.InvokeAsync("hydratePinned",NativeRuntimeArguments.Entry(mount,entry),(_,_,token)=>{token.ThrowIfCancellationRequested();return Task.FromResult<object?>(null);},cancel.Token));Assert.True(observed);Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.True(CloudFilesNative.HasFullContent(path,0));
        }
        finally{runtime.CheckpointForTests=null;if(connected)await runtime.InvokeAsync("disconnectRoot",NativeRuntimeArguments.Root(mount,new{sessionId=session}));if(registered)await runtime.InvokeAsync("unregisterRoot",NativeRuntimeArguments.Root(mount));if(Directory.Exists(area))Directory.Delete(area,true);}
    }
}
