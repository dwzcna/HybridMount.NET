using System.Text;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>隔离真实NTFS/CFAPI和回环DAV；直接取舍必须条件发送、保留原内容且不重复PUT。</summary>
public sealed class ConflictResolutionTests
{
    private static async Task<LocalUpload> Conflict(UploadFlowTests.Fixture test,string path="文件.bin",string body="local choice")
    {
        var local=Path.Combine(test.Local,path);if(File.Exists(local))await File.ReadAllBytesAsync(local);
        test.Dav.Put(path,Encoding.UTF8.GetBytes("remote choice"));await File.WriteAllTextAsync(local,body);await test.Manager.ScanUploadChangesAsync(test.Id);
        var upload=test.Repository.Uploads(test.Id).Single(x=>!x.IsCopy&&x.LocalPath==path);
        return test.Repository.UpdateUpload(upload.Id,x=>x with{State="Conflict",Error="远端已被其他客户端修改"})!;
    }
    [Fact(Timeout=90000)]
    public async Task LocalChoiceUsesFreshAuthorizedVersionAndNormalConditionalUpload()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=await Conflict(test);var path=Path.Combine(test.Local,"文件.bin");
        var compared=await test.Manager.CompareConflictAsync(original.Id);Assert.True(compared.CanUseLocal,compared.LocalRestriction);Assert.True(compared.CanUseRemote,compared.RemoteRestriction);
        await test.Manager.ResolveConflictAsync(compared,true);var result=await test.Wait("文件.bin",bytes:Encoding.UTF8.GetBytes("local choice"));
        Assert.Equal(original.Generation+1,result.Generation);Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);
        Assert.Equal("local choice",await File.ReadAllTextAsync(result.ConflictRecoveryPath!));Assert.Null(result.Resolution);
        Assert.True(test.Manager.Conflicts.All(x=>x.Id!=original.Id));
    }
    [Theory(Timeout=90000)]
    [InlineData(false)][InlineData(true)]
    public async Task RemoteChoicePreservesIdentityPinAndOriginalLocalBackup(bool ordinary)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var relative=ordinary?"new-file.txt":"文件.bin";var original=await Conflict(test,relative);
        var path=Path.Combine(test.Local,relative);var before=LocalFileState.Read(path);var info=CloudFilesNative.Inspect(path);var puts=test.Dav.Server.Requests.Count(x=>x.Method=="PUT");
        var compared=await test.Manager.CompareConflictAsync(original.Id);Assert.True(compared.CanUseRemote,compared.RemoteRestriction);
        var message=await test.Manager.ResolveConflictAsync(compared,false);var result=test.Repository.Upload(original.Id)!;
        Assert.Equal("remote choice",await File.ReadAllTextAsync(path));Assert.Equal("local choice",await File.ReadAllTextAsync(result.ConflictRecoveryPath!));Assert.Contains(result.ConflictRecoveryPath!,message);
        Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);Assert.Equal("Completed",result.State);Assert.Null(result.Resolution);
        Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);if(info is not null)Assert.Equal(info.Value.Info.PinState,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
        Assert.Equal(puts,test.Dav.Server.Requests.Count(x=>x.Method=="PUT"));Assert.False(test.Repository.Entries(test.Id).Single(x=>x.LocalPath==relative).Protected);
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Equal("Completed",test.Repository.Upload(original.Id)!.State);
    }
    [Theory(Timeout=90000)]
    [InlineData(true)][InlineData(false)]
    public async Task ChangedRemoteVersionNeverUsesStaleAuthorization(bool localChoice)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=await Conflict(test);var compared=await test.Manager.CompareConflictAsync(original.Id);
        test.Dav.Put("文件.bin",Encoding.UTF8.GetBytes("newer remote"));var puts=test.Dav.Server.Requests.Count(x=>x.Method=="PUT");
        await Assert.ThrowsAsync<SourceConflictException>(()=>test.Manager.ResolveConflictAsync(compared,localChoice));
        Assert.Equal("local choice",await File.ReadAllTextAsync(Path.Combine(test.Local,"文件.bin")));Assert.Equal("newer remote",Encoding.UTF8.GetString(test.Dav.Objects["文件.bin"].Bytes));
        Assert.Equal(puts,test.Dav.Server.Requests.Count(x=>x.Method=="PUT"));Assert.Equal("ResolutionPending",test.Repository.Upload(original.Id)!.State);
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Equal("ResolutionPending",test.Repository.Upload(original.Id)!.State);Assert.False(test.Manager.RetryUpload(original.Id));
    }
    [Fact(Timeout=90000)]
    public async Task ChangedLocalOrPendingEventsInvalidateComparisonBeforeCreatingIntent()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=await Conflict(test);var compared=await test.Manager.CompareConflictAsync(original.Id);
        await File.WriteAllTextAsync(Path.Combine(test.Local,"文件.bin"),"later local edit");
        await Assert.ThrowsAsync<SourceConflictException>(()=>test.Manager.ResolveConflictAsync(compared,false));Assert.Null(test.Repository.Upload(original.Id)!.Resolution);
        await test.Manager.ScanUploadChangesAsync(test.Id);original=test.Repository.UpdateUpload(original.Id,x=>x with{State="Conflict",Error="again"})!;compared=await test.Manager.CompareConflictAsync(original.Id);
        test.Repository.SaveRemotePending(new("late",test.Id,"scope","文件.bin",test.Dav.Objects.ContainsKey("文件.bin")?compared.Remote:null,"Conflict"));
        await Assert.ThrowsAsync<SourceConflictException>(()=>test.Manager.ResolveConflictAsync(compared,true));Assert.Null(test.Repository.Upload(original.Id)!.Resolution);
    }
    [Fact(Timeout=90000)]
    public async Task FailedNativeWriteKeepsBarrierAcrossRestartAndExplicitRemoteChoiceCanRecover()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=await Conflict(test,body:"original local long body that must be recoverable");var compared=await test.Manager.CompareConflictAsync(original.Id);
        test.Manager.ConflictWriteForTests=()=>throw new IOException("simulated interrupted local replacement");
        await Assert.ThrowsAsync<IOException>(()=>test.Manager.ResolveConflictAsync(compared,false));var pending=test.Repository.Upload(original.Id)!;Assert.Equal("Committing",pending.Resolution!.Phase);
        Assert.Equal("original local long body that must be recoverable",await File.ReadAllTextAsync(pending.Resolution.BackupPath));var partial=await File.ReadAllBytesAsync(Path.Combine(test.Local,"文件.bin"));
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.Equal("ResolutionPending",test.Repository.Upload(original.Id)!.State);Assert.Equal(partial,await File.ReadAllBytesAsync(Path.Combine(test.Local,"文件.bin")));Assert.False(test.Manager.RetryUpload(original.Id));
        compared=await test.Manager.CompareConflictAsync(original.Id);Assert.False(compared.CanUseLocal);Assert.True(compared.CanUseRemote,compared.RemoteRestriction);
        await test.Manager.ResolveConflictAsync(compared,false);Assert.Equal("remote choice",await File.ReadAllTextAsync(Path.Combine(test.Local,"文件.bin")));
        Assert.Equal(pending.Resolution.BackupPath,test.Repository.Upload(original.Id)!.ConflictRecoveryPath);
        Assert.Equal("original local long body that must be recoverable",await File.ReadAllTextAsync(pending.Resolution.BackupPath));
    }
    [Theory(Timeout=90000)]
    [InlineData("local")][InlineData("remote")][InlineData("backup")][InlineData("cancel")]
    public async Task DownloadBoundaryRechecksBothSidesBackupAndCancellation(string mutation)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=await Conflict(test);var path=Path.Combine(test.Local,"文件.bin");var compared=await test.Manager.CompareConflictAsync(original.Id);using var cancel=new CancellationTokenSource();
        test.Manager.ConflictCheckpointForTests=async phase=>
        {
            if(phase!="Downloaded")return;
            if(mutation=="local")await File.WriteAllTextAsync(path,"user edit during download");
            if(mutation=="remote")test.Dav.Put("文件.bin",Encoding.UTF8.GetBytes("later remote"));
            if(mutation=="backup")await File.WriteAllTextAsync(test.Repository.Upload(original.Id)!.Resolution!.BackupPath,"damaged backup");
            if(mutation=="cancel")cancel.Cancel();
        };
        await Assert.ThrowsAnyAsync<Exception>(()=>test.Manager.ResolveConflictAsync(compared,false,cancel.Token));
        Assert.Equal(mutation=="local"?"user edit during download":"local choice",await File.ReadAllTextAsync(path));Assert.NotNull(test.Repository.Upload(original.Id)!.Resolution);
    }
    [Fact(Timeout=90000)]
    public async Task RemoteConflictsArePersistentAndPreciselyRetiredByExplicitChoice()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var upload=await Conflict(test);var compared=await test.Manager.CompareConflictAsync(upload.Id);
        var scope="test-scope";
        // 上传选择同时捕获精确同路径事件；无关条目不能被退役。
        var pending=new RemotePendingChange("remote-conflict",test.Id,scope,"文件.bin",compared.Remote,"Conflict","本地与远端不同");test.Repository.SaveRemotePending(pending);
        test.Repository.SaveRemotePending(pending with{Id="unrelated",Path="another.txt"});Assert.Contains(test.Manager.Conflicts,x=>x.Id==pending.Id);Assert.Contains(test.Manager.SyncIssues,x=>x.Id==pending.Id);
        compared=await test.Manager.CompareConflictAsync(upload.Id);await test.Manager.ResolveConflictAsync(compared,false);
        Assert.Equal("Superseded",test.Repository.AllRemotePending(test.Id).Single(x=>x.Id==pending.Id).State);Assert.Equal("Conflict",test.Repository.AllRemotePending(test.Id).Single(x=>x.Id=="unrelated").State);
    }
    [Fact(Timeout=90000)]
    public async Task ViewModelCapturesOriginalTaskAndRejectsDuplicateOrLateSubmission()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var item=await Conflict(test);using var vm=new TasksViewModel(test.Manager,new InlineDispatcher());
        vm.ConflictsOnly=true;await vm.SnapshotIdle;Assert.Contains(vm.Operations,x=>x.Id==item.Id);vm.SelectedJobId=item.Id;Assert.True(await vm.BeginConflictAsync());Assert.False(await vm.BeginConflictAsync());
        Assert.True(vm.CanResolveWithRemote);vm.SelectedJobId="unrelated";Assert.True(await vm.ResolveConflictAsync(false));Assert.False(await vm.ResolveConflictAsync(false));vm.EndDialog();
        Assert.Equal("Completed",test.Repository.Upload(item.Id)!.State);Assert.Contains("备份",vm.Status);
    }
    [Theory(Timeout=90000)]
    [InlineData(0)][InlineData(32)][InlineData(700000)]
    public async Task DirtyPinnedPlaceholderIsReplacedWithoutDehydratingOrChangingPin(int length)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");
        var keep=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));Assert.Equal("Completed",keep.State);
        var identity=CloudFilesNative.Inspect(path)!.Value.Identity;
        using(var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.Read)){writer.WriteByte(99);writer.Flush(true);}
        Assert.NotNull(CloudFilesNative.Inspect(path));Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
        var originalBytes=await File.ReadAllBytesAsync(path);var remoteBytes=Enumerable.Range(0,length).Select(x=>(byte)(x*17)).ToArray();test.Dav.Put("文件.bin",remoteBytes);await test.Manager.ScanUploadChangesAsync(test.Id);
        var item=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="文件.bin");test.Repository.UpdateUpload(item.Id,x=>x with{State="Conflict",Error="conflict"});
        var compared=await test.Manager.CompareConflictAsync(item.Id);Assert.True(compared.CanUseRemote,compared.RemoteRestriction);await test.Manager.ResolveConflictAsync(compared,false);
        var final=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(identity,final.Identity);Assert.Equal(1u,final.Info.PinState);Assert.Equal(1u,final.Info.InSyncState);
        Assert.Equal(remoteBytes,await File.ReadAllBytesAsync(path));Assert.Equal(originalBytes,await File.ReadAllBytesAsync(test.Repository.Upload(item.Id)!.ConflictRecoveryPath!));
    }
    [Fact(Timeout=90000)]
    public async Task LaterChangeAtNormalUploadBoundaryReturnsConflictInsteadOfRenewingAuthorization()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var item=await Conflict(test);var compared=await test.Manager.CompareConflictAsync(item.Id);var changed=false;
        test.Manager.UploadCheckpointForTests=(stage,upload)=>{if(stage=="Prepared"&&upload.Id==item.Id&&!changed){changed=true;test.Dav.Put("文件.bin",Encoding.UTF8.GetBytes("changed again at send"));}return Task.CompletedTask;};
        await test.Manager.ResolveConflictAsync(compared,true);await test.Wait("文件.bin",state:"Conflict");Assert.True(changed);Assert.Equal("changed again at send",Encoding.UTF8.GetString(test.Dav.Objects["文件.bin"].Bytes));
        Assert.Equal("local choice",await File.ReadAllTextAsync(Path.Combine(test.Local,"文件.bin")));
    }
    [Fact(Timeout=90000)]
    public async Task RemoteTaskCanBeSelectedDirectlyAndHistoryClearDoesNotHideIt()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);
        test.Dav.Put("文件.bin",Encoding.UTF8.GetBytes("latest remote"));await using var source=test.Dav.Source();var metadata=await source.GetMetadataAsync("文件.bin");
        var scope=source.RemoteScope(test.Repository.LoadDefinitions().Single().CredentialRevision);
        var pending=new RemotePendingChange("standalone-remote",test.Id,scope,"文件.bin",metadata,"Conflict","需要手动取舍");test.Repository.SaveRemotePending(pending);
        await test.Manager.ClearOperationHistoryAsync(TaskHistoryClearMode.All);Assert.Contains(test.Manager.SyncIssues,x=>x.Id==pending.Id);
        var compared=await test.Manager.CompareConflictAsync(pending.Id);Assert.True(compared.CanUseRemote,compared.RemoteRestriction);await test.Manager.ResolveConflictAsync(compared,false);
        Assert.Equal("latest remote",await File.ReadAllTextAsync(path));Assert.DoesNotContain(test.Manager.Conflicts,x=>x.Id==pending.Id);
    }
    [Theory(Timeout=90000)]
    [InlineData(true)][InlineData(false)]
    public async Task S3UsesSameDirectChoicesWithVersionConditionsAndNoRemoteReplacementPut(bool useLocal)
    {
        await using var fixture=new S3Fixture();var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");var repository=new MountRepository(Path.Combine(fixture.Area,"data"),registry);
        await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));await using var manager=new MountManager(repository,logger);var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);
        var mount=await manager.AddAsync("S3冲突",local,false,source:fixture.Options,credentials:S3Fixture.Credentials,autoUpload:false);
        try
        {
            await manager.SetSchedulingForTestsAsync(mount.Id,false);await manager.SetRemoteDetectionAsync(mount.Id,false,60,300);var path=Path.Combine(local,"文件.bin");await File.ReadAllBytesAsync(path);
            fixture.Put("remote/文件.bin",Encoding.UTF8.GetBytes("S3 remote"));await File.WriteAllTextAsync(path,"S3 local");await manager.ScanUploadChangesAsync(mount.Id);
            var original=repository.Uploads(mount.Id).Single(x=>x.LocalPath=="文件.bin");repository.UpdateUpload(original.Id,x=>x with{State="Conflict",Error="another version"});
            var compared=await manager.CompareConflictAsync(original.Id);Assert.True(useLocal?compared.CanUseLocal:compared.CanUseRemote,useLocal?compared.LocalRestriction:compared.RemoteRestriction);
            var before=fixture.Server.Requests.Count(x=>x.Method is "PUT" or "POST");await manager.ResolveConflictAsync(compared,useLocal);
            if(useLocal)
            {
                var deadline=DateTime.UtcNow.AddSeconds(30);while(repository.Upload(original.Id)?.State!="Completed")
                {if(DateTime.UtcNow>deadline)throw new TimeoutException(repository.Upload(original.Id)?.Error);await Task.Delay(80);}
                Assert.Equal("S3 local",Encoding.UTF8.GetString(fixture.Objects["remote/文件.bin"].Bytes));
            }
            else{Assert.Equal("S3 remote",await File.ReadAllTextAsync(path));Assert.Equal(before,fixture.Server.Requests.Count(x=>x.Method is "PUT" or "POST"));}
            Assert.Equal("S3 local",await File.ReadAllTextAsync(repository.Upload(original.Id)!.ConflictRecoveryPath!));
        }
        finally{Assert.Equal("Completed",(await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"))).State);Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
    [Fact(Timeout=90000)]
    public async Task MultipleHardLinksNeverModifyContentOutsideTheRoot()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var item=await Conflict(test,"ordinary.txt");var path=Path.Combine(test.Local,"ordinary.txt");var external=Path.Combine(test.Dav.Area,"outside-link.txt");
        Assert.True(CreateHardLink(external,path,0));await test.Manager.ScanUploadChangesAsync(test.Id);test.Repository.UpdateUpload(item.Id,x=>x with{State="Conflict",Error="hard link conflict"});var compared=await test.Manager.CompareConflictAsync(item.Id);
        await Assert.ThrowsAnyAsync<Exception>(()=>test.Manager.ResolveConflictAsync(compared,false));Assert.Equal("local choice",await File.ReadAllTextAsync(path));Assert.Equal("local choice",await File.ReadAllTextAsync(external));
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",EntryPoint="CreateHardLinkW",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]private static extern bool CreateHardLink(string path,string existing,nint security);
    [Fact(Timeout=90000)]
    public async Task CanceledComparisonCannotAcquireTheNextDialogsOwnership()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var item=await Conflict(test);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var handler=test.Dav.Server.Handler;
        test.Dav.Server.Handler=async request=>{if(request.Method=="PROPFIND"&&Uri.UnescapeDataString(request.Target).Contains("文件.bin")){entered.TrySetResult();await release.Task;}return await handler(request);};
        using var vm=new TasksViewModel(test.Manager,new InlineDispatcher());await vm.SnapshotIdle;vm.SelectedJobId=item.Id;var comparing=vm.BeginConflictAsync();
        try{await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));vm.EndDialog();Assert.True(vm.BeginCopy());}
        finally{release.TrySetResult();}
        Assert.False(await comparing);Assert.False(vm.BeginCopy());vm.EndDialog();
    }
    [Theory(Timeout=90000)]
    [InlineData(false,false)][InlineData(true,false)][InlineData(false,true)]
    public async Task OneChoiceWaitsForUnrelatedUploadAndCanCancelWithoutAnIntent(bool useLocal,bool cancelWaiting)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=await Conflict(test);var compared=await test.Manager.CompareConflictAsync(original.Id);
        await File.WriteAllTextAsync(Path.Combine(test.Local,"other.txt"),"unrelated upload");await test.Manager.ScanUploadChangesAsync(test.Id);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.UploadCheckpointForTests=async (phase,item)=>{if(phase=="BeforePrepare"&&item.LocalPath=="other.txt"){entered.TrySetResult();await release.Task;}};
        using var cancel=new CancellationTokenSource();Task<string>? resolving=null;
        try
        {
            await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            resolving=test.Manager.ResolveConflictAsync(compared,useLocal,cancel.Token);await Task.Delay(80);Assert.False(resolving.IsCompleted);
            if(cancelWaiting){cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>resolving);Assert.Null(test.Repository.Upload(original.Id)!.Resolution);}
        }
        finally{release.TrySetResult();}
        if(cancelWaiting)return;
        await resolving!.WaitAsync(TimeSpan.FromSeconds(30));
        if(useLocal)await test.Wait("文件.bin",bytes:Encoding.UTF8.GetBytes("local choice"));
        Assert.Equal("Completed",test.Repository.Upload(original.Id)!.State);
        var puts=test.Dav.Server.Requests.Count(x=>x.Method=="PUT"&&Uri.UnescapeDataString(x.Target).Split('?')[0].EndsWith("/文件.bin",StringComparison.Ordinal));Assert.Equal(useLocal?1:0,puts);
    }
    [Theory(Timeout=90000)]
    [InlineData(false,false)][InlineData(false,true)][InlineData(true,false)]
    public async Task OwnIssueMetadataDoesNotRequireAnotherChoiceButExternalSameSizeEditDoes(bool externalEdit,bool useLocal)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=await Conflict(test);var compared=await test.Manager.CompareConflictAsync(original.Id);
        await test.Manager.PublishUploadIssuesAsync();Assert.Equal("Published",test.Repository.Upload(original.Id)!.Attention!.ShellState);
        if(externalEdit)
        {
            var path=Path.Combine(test.Local,"文件.bin");var write=File.GetLastWriteTimeUtc(path);await File.WriteAllTextAsync(path,"local edited");File.SetLastWriteTimeUtc(path,write);
            await Assert.ThrowsAsync<SourceConflictException>(()=>test.Manager.ResolveConflictAsync(compared,false));Assert.Equal("local edited",await File.ReadAllTextAsync(path));Assert.Null(test.Repository.Upload(original.Id)!.Resolution);
        }
        else{await test.Manager.ResolveConflictAsync(compared,useLocal);if(useLocal)await test.Wait("文件.bin",bytes:Encoding.UTF8.GetBytes("local choice"));Assert.Equal("Completed",test.Repository.Upload(original.Id)!.State);Assert.Equal(useLocal?"local choice":"remote choice",await File.ReadAllTextAsync(Path.Combine(test.Local,"文件.bin")));}
    }
    private sealed class InlineDispatcher:IUiDispatcher{public bool TryEnqueue(Action action){action();return true;}}
}
