using System.Diagnostics;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>使用独立NTFS根和回环DAV验证排除、发送竞争及原生属性；不会访问用户挂载。</summary>
public sealed class FileExclusionFlowTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact(Timeout=60000)]
    public async Task NewDefaultsSkipFileTasksButNotAncestorDirectoryNames()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);
        Assert.Equal(FileExclusionRules.Defaults(),test.Repository.LoadDefinitions().Single().ExclusionRules);
        foreach(var name in new[]{"~a.xlsx","a$b.txt","a.BAK","-文件.txt"})await File.WriteAllTextAsync(Path.Combine(test.Local,name),"excluded bytes");
        var folder=Path.Combine(test.Local,"~目录");Directory.CreateDirectory(folder);await File.WriteAllTextAsync(Path.Combine(folder,"normal.txt"),"upload me");
        await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.DoesNotContain(test.Repository.Uploads(test.Id),x=>!x.IsDirectory&&FileExclusionRules.Matches(FileExclusionRules.Defaults(),x.LocalPath));
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Wait("~目录/normal.txt");
        Assert.DoesNotContain(test.Dav.Objects.Keys,x=>x is "~a.xlsx" or "a$b.txt" or "a.BAK" or "-文件.txt");
        foreach(var name in new[]{"~a.xlsx","a$b.txt","a.BAK","-文件.txt"})Assert.Equal("excluded bytes",await File.ReadAllTextAsync(Path.Combine(test.Local,name)));
        var skipped=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[Path.Combine(test.Local,"~a.xlsx")]));
        Assert.Contains(skipped.Results,x=>x.Status=="Skipped"&&x.Message.Contains("已排除"));
    }
    [Fact(Timeout=60000)]
    public async Task QueuedPauseRejectsRetryAndCopyAndResumesWithoutNewGeneration()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"pause.txt");await File.WriteAllTextAsync(path,"local only");
        await test.Manager.ScanUploadChangesAsync(test.Id);var queued=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="pause.txt");
        await test.Manager.SetFileExclusionRulesAsync(test.Id,[new(FileExclusionMatchKind.Prefix,"pause")]);
        var excluded=test.Repository.Upload(queued.Id)!;Assert.Equal("Excluded",excluded.State);Assert.Equal("Queued",excluded.ExclusionResumeState);Assert.Equal(queued.Generation,excluded.Generation);
        Assert.False(test.Manager.RetryUpload(queued.Id));Assert.Throws<IOException>(()=>test.Manager.UploadAsCopy(queued.Id,"copy.txt"));
        Assert.False(test.Dav.Objects.ContainsKey("pause.txt"));await test.Manager.SetFileExclusionRulesAsync(test.Id,[]);
        Assert.Equal("Queued",test.Repository.Upload(queued.Id)!.State);Assert.Equal(queued.Generation,test.Repository.Upload(queued.Id)!.Generation);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Wait("pause.txt");
        Assert.Single(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("pause.txt",StringComparison.Ordinal));
    }
    [Theory(Timeout=60000)] [InlineData("BeforePrepare")] [InlineData("Prepared")] [InlineData("BeforeSend")]
    public async Task SavedRuleInvalidatesOldPreparationBeforeTargetWrite(string checkpoint)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.UploadCheckpointForTests=async(stage,item)=>{if(item.LocalPath=="race.txt"&&stage==checkpoint){held.TrySetResult();await release.Task;}};
        try
        {
            await File.WriteAllTextAsync(Path.Combine(test.Local,"race.txt"),"never send");await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));await test.Manager.SetFileExclusionRulesAsync(test.Id,[new(FileExclusionMatchKind.Prefix,"race")]);
            Assert.Equal("Excluded",test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="race.txt").State);
        }
        finally{release.TrySetResult();}
        await test.Manager.DisconnectAsync(test.Id);await test.Manager.ConnectAsync(test.Id);await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("race.txt",StringComparison.Ordinal));
        Assert.Equal("Excluded",test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="race.txt").State);
    }
    [Fact(Timeout=60000)]
    public async Task ExclusionMetadataDoesNotRestoreClearedTaskHistory()
    {
        await using var test=await UploadFlowTests.Fixture.Create();await File.WriteAllTextAsync(Path.Combine(test.Local,"history.txt"),"finished");var done=await test.Wait("history.txt");
        await test.Manager.DisconnectAsync(test.Id);await test.Manager.ConnectAsync(test.Id);await test.Manager.ClearCompletedOperationsAsync();
        Assert.DoesNotContain(test.Manager.Operations,x=>x.Id==done.Id);
        await test.Manager.SetFileExclusionRulesAsync(test.Id,[new(FileExclusionMatchKind.Prefix,"history")]);await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.DoesNotContain(test.Manager.Operations,x=>x.Id==done.Id);await test.Manager.SetFileExclusionRulesAsync(test.Id,[]);
        Assert.DoesNotContain(test.Manager.Operations,x=>x.Id==done.Id);Assert.Equal(done.Generation,test.Repository.Upload(done.Id)!.Generation);
    }
    [Fact(Timeout=60000)]
    public async Task CommittedUploadFinishesOnceAndKeepsNewExclusion()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.UploadCheckpointForTests=async(stage,item)=>{if(item.LocalPath=="sent.txt"&&stage=="RemoteCommitted"){held.TrySetResult();await release.Task;}};
        try
        {
            await File.WriteAllTextAsync(Path.Combine(test.Local,"sent.txt"),"sent once");await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));await test.Manager.SetFileExclusionRulesAsync(test.Id,[new(FileExclusionMatchKind.Prefix,"sent")]);
            Assert.Equal("RemoteCommitted",test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="sent.txt").State);
        }
        finally{release.TrySetResult();}
        await test.Wait("sent.txt");await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.Single(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("sent.txt",StringComparison.Ordinal));
        Assert.Equal(3u,CloudFilesNative.Inspect(Path.Combine(test.Local,"sent.txt"))!.Value.Info.PinState);
    }
    [Theory(Timeout=60000)] [InlineData(0u)] [InlineData(1u)] [InlineData(2u)]
    public async Task PlaceholderMarkPreservesIdentityInSyncAndReadabilityAndRestoresPin(uint originalPin)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");
        if(originalPin==1)await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));
        if(originalPin==2){using var attributes=CloudFilesNative.CreateFile(path,0x180,7,0,3,0x02200000,0);CloudFilesNative.Check(CloudFilesNative.SetPinIntent(attributes,2,0,0));}
        var before=CloudFilesNative.Inspect(path)!.Value;var stamp=LocalFileState.Read(path);var requests=test.Dav.Server.Requests.Count;
        await test.Manager.SetFileExclusionRulesAsync(test.Id,[new(FileExclusionMatchKind.Suffix,".bin")]);
        var marked=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(3u,marked.Info.PinState);Assert.Equal(before.Identity,marked.Identity);Assert.Equal(before.Info.InSyncState,marked.Info.InSyncState);
        Assert.Equal(stamp.Identity,LocalFileState.Read(path).Identity);Assert.Equal(requests,test.Dav.Server.Requests.Count);
        var custom=CloudCustomStateReader.Read(path);Assert.True(custom.Ids.Contains("2"),test.Manager.FileExclusionStatus(test.Id));Assert.Equal(CloudItemStateProperty.ExclusionIcon,custom.Icons[custom.Ids.ToList().IndexOf("2")]);
        var job=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));Assert.Contains(job.Results,x=>x.Status=="Skipped"&&x.Message.Contains("已排除"));
        var free=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"freeSpace",[path]));Assert.Contains(free.Results,x=>x.Status=="Skipped");
        using(var attributes=CloudFilesNative.CreateFile(path,0x180,7,0,3,0x02200000,0))CloudFilesNative.Check(CloudFilesNative.SetPinIntent(attributes,1,0,0));
        var wait=Stopwatch.StartNew();while(CloudFilesNative.Inspect(path)!.Value.Info.PinState!=3&&wait.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(50);
        Assert.Equal(3u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Equal(requests,test.Dav.Server.Requests.Count);
        // PinState可先于图标写入完成；通过根锁等本次属性事务收尾后验证可读性，不与保护句柄释放竞争。
        await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.Equal(test.Dav.Objects["文件.bin"].Bytes,await File.ReadAllBytesAsync(path));Assert.Equal(3u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
        Assert.Empty(test.Repository.Uploads(test.Id));await test.Manager.SetFileExclusionRulesAsync(test.Id,[]);
        Assert.Equal(before.Info.PinState,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Empty(test.Repository.Uploads(test.Id));
    }

    [Fact(Timeout=60000)]
    public async Task OrdinaryFileHasExcludedIconWithoutChangingIdentityOrContent()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"ordinary-probe.txt");await File.WriteAllTextAsync(path,"ordinary bytes");
        var definition=test.Repository.LoadDefinitions().Single();var before=LocalFileState.Read(path);var store=new FileExclusionMarkerStore(Path.Combine(test.Repository.DataDirectory,"ordinary-proof"),test.Id);
        try{store.Reconcile(definition,"ordinary-probe.txt",true);}catch(Exception error){output.WriteLine(error.ToString());}
        var info=CloudFilesNative.Inspect(path);output.WriteLine($"普通文件：Pin={info?.Info.PinState.ToString()??"非占位"}；{store.Describe()}");
        Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);Assert.Null(info);Assert.Equal("ordinary bytes",await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("HRESULT=",store.Describe());Assert.Contains("待完成 0",store.Describe());
        Assert.Equal(9u,CloudShellState.Read(path).ProviderState);
        var state=CloudCustomStateReader.Read(path);Assert.Equal(new[]{"2"},state.Ids);Assert.Equal(new[]{CloudItemStateProperty.ExclusionText},state.Values);Assert.Equal(new[]{CloudItemStateProperty.ExclusionIcon},state.Icons);
        store.Reconcile(definition,"ordinary-probe.txt",false);Assert.False(store.HasMarks);Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);Assert.Empty(CloudCustomStateReader.Read(path).Ids);
    }
    [Fact(Timeout=60000)]
    public async Task UnknownWriteStillReconcilesReadOnlyWhileExcluded()
    {
        await using var test=await UploadFlowTests.Fixture.Create();var handler=test.Dav.Server.Handler;var dropped=false;
        test.Dav.Server.Handler=async request=>{var response=await handler(request);if(!dropped&&request.Method=="PUT"&&request.Target.EndsWith("unknown.txt",StringComparison.Ordinal)){dropped=true;throw new IOException("模拟响应丢失");}return response;};
        await File.WriteAllTextAsync(Path.Combine(test.Local,"unknown.txt"),"written once");var unknown=await test.Wait("unknown.txt","OutcomeUnknown");
        await test.Manager.SetFileExclusionRulesAsync(test.Id,[new(FileExclusionMatchKind.Prefix,"unknown")]);Assert.True(test.Manager.RetryUpload(unknown.Id));await test.Wait("unknown.txt");
        Assert.Single(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("unknown.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=60000)]
    public async Task S3DefaultExclusionAndExplicitResumeUseSameQueueGate()
    {
        await using var source=new S3Fixture();var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(source.Area,"exclusion-data"),registry);await using var logger=new LoggerService(Path.Combine(source.Area,"exclusion-log.db"));await using var manager=new MountManager(repository,logger);
        var local=Path.Combine(source.Area,"exclusion-root");Directory.CreateDirectory(local);MountDefinition? mount=null;
        try
        {
            mount=await manager.AddAsync("S3 排除",local,false,source:source.Options,credentials:S3Fixture.Credentials);await manager.SetSchedulingForTestsAsync(mount.Id,false);
            await File.WriteAllTextAsync(Path.Combine(local,"ignored.BAK"),"local backup");await manager.ScanUploadChangesAsync(mount.Id);
            Assert.DoesNotContain(repository.Uploads(mount.Id),x=>x.LocalPath=="ignored.BAK");
            Assert.DoesNotContain(source.Server.Requests,x=>x.Method=="PUT"&&x.Target.Contains("ignored.BAK",StringComparison.Ordinal));
            await manager.SetFileExclusionRulesAsync(mount.Id,[]);await manager.SetSchedulingForTestsAsync(mount.Id,true);
            var deadline=Stopwatch.StartNew();while(repository.Uploads(mount.Id).FirstOrDefault(x=>x.LocalPath=="ignored.BAK")?.State!="Completed"&&deadline.Elapsed<TimeSpan.FromSeconds(20))await Task.Delay(80);
            Assert.Equal("Completed",repository.Uploads(mount.Id).Single(x=>x.LocalPath=="ignored.BAK").State);
            Assert.Single(source.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("ignored.BAK",StringComparison.Ordinal));
        }
        finally
        {
            if(mount is not null)await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }

    [Fact(Timeout=60000)]
    public async Task ExclusionAndIssueIconsPreserveEachOtherAndDoNotAdvanceGeneration()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
        var path=Path.Combine(test.Local,"dual-state.txt");await File.WriteAllTextAsync(path,"local bytes");await test.Manager.ScanUploadChangesAsync(test.Id);
        var row=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="dual-state.txt");test.Repository.UpdateUpload(row.Id,x=>x with{State="Failed",Error="keep original failure"});
        await test.Manager.PublishUploadIssuesAsync();Assert.Equal(new[]{"1"},CloudCustomStateReader.Read(path).Ids);var requests=test.Dav.Server.Requests.Count;
        await test.Manager.SetFileExclusionRulesAsync(test.Id,[new(FileExclusionMatchKind.Prefix,"dual-state")]);var combined=CloudCustomStateReader.Read(path);
        Assert.Equal(new[]{"1","2"},combined.Ids);Assert.Equal(2,combined.Values.Count);Assert.Equal(2,combined.Icons.Count);
        Assert.Equal(row.Generation,test.Repository.Upload(row.Id)!.Generation);Assert.Equal("Failed",test.Repository.Upload(row.Id)!.State);
        test.Repository.UpdateUpload(row.Id,x=>x with{State="Canceled",CancelRequested=true});await test.Manager.PublishUploadIssuesAsync();
        var cleared=CloudCustomStateReader.Read(path);Assert.Equal(new[]{"2"},cleared.Ids);Assert.Equal(new[]{CloudItemStateProperty.ExclusionText},cleared.Values);
        await test.Manager.SetFileExclusionRulesAsync(test.Id,[]);Assert.Empty(CloudCustomStateReader.Read(path).Ids);
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Equal("Canceled",test.Repository.Upload(row.Id)!.State);Assert.Equal(row.Generation,test.Repository.Upload(row.Id)!.Generation);
        Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal("local bytes",await File.ReadAllTextAsync(path));
    }
    [Fact(Timeout=60000)]
    public async Task RestartRepairsAppliedJournalWithoutIconAndRestoreClearsAllVectors()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
        var definition=test.Repository.LoadDefinitions().Single();var path=Path.Combine(test.Local,"文件.bin");var native=CloudFilesNative.Inspect(path)!.Value;
        var area=Path.Combine(test.Repository.DataDirectory,"icon-recovery");var store=new FileExclusionMarkerStore(area,test.Id);store.Reconcile(definition,"文件.bin",true);
        var original=CloudCustomStateReader.Read(path);Assert.True(original.Ids.Contains("2"),store.Describe());
        await Windows.Storage.Provider.StorageProviderItemProperties.SetAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(path),[]);
        Assert.Empty(CloudCustomStateReader.Read(path).Ids);var requests=test.Dav.Server.Requests.Count;
        store=new(area,test.Id);Assert.True(store.Reconcile(definition,"文件.bin",true).Changed,store.Describe());var repaired=CloudCustomStateReader.Read(path);
        Assert.Equal(new[]{"2"},repaired.Ids);Assert.Equal(new[]{CloudItemStateProperty.ExclusionIcon},repaired.Icons);
        var stamp=LocalFileState.Read(path);Assert.False(store.Reconcile(definition,"文件.bin",true).Changed);Assert.Equal(stamp.Key,LocalFileState.Read(path).Key);
        Assert.Equal(native.Identity,CloudFilesNative.Inspect(path)!.Value.Identity);Assert.Equal(native.Info.InSyncState,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);
        store.Reconcile(definition,"文件.bin",false);var cleared=CloudCustomStateReader.Read(path);Assert.Empty(cleared.Ids);Assert.Empty(cleared.Values);Assert.Empty(cleared.Icons);
        Assert.Equal(native.Info.PinState,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Equal(requests,test.Dav.Server.Requests.Count);
    }
    [Fact(Timeout=60000)]
    public async Task AlreadyExcludedPinIsPreservedWhenOwnRuleIsRemoved()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
        var definition=test.Repository.LoadDefinitions().Single();var path=Path.Combine(test.Local,"文件.bin");
        using(var handle=CloudFilesNative.CreateFile(path,0x180,7,0,3,0x02200000,0))CloudFilesNative.Check(CloudFilesNative.SetPinIntent(handle,3,0,0));
        var store=new FileExclusionMarkerStore(Path.Combine(test.Repository.DataDirectory,"prior-excluded"),test.Id);store.Reconcile(definition,"文件.bin",true);
        Assert.Equal(new[]{"2"},CloudCustomStateReader.Read(path).Ids);new FileExclusionMarkerStore(Path.Combine(test.Repository.DataDirectory,"prior-excluded"),test.Id).Reconcile(definition,"文件.bin",false);
        Assert.Equal(3u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Empty(CloudCustomStateReader.Read(path).Ids);
    }
    [Fact(Timeout=60000)]
    public async Task RenameOutOfRuleRemovesIconAndQueuesExactlyOneGeneration()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
        var path=Path.Combine(test.Local,"rename.bak");await File.WriteAllTextAsync(path,"excluded bytes");await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.Equal(new[]{"2"},CloudCustomStateReader.Read(path).Ids);Assert.DoesNotContain(test.Repository.Uploads(test.Id),x=>x.LocalPath=="rename.bak");
        var renamed=Path.Combine(test.Local,"renamed.txt");File.Move(path,renamed);await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.True(CloudCustomStateReader.Read(renamed).Ids.Count==0,test.Manager.FileExclusionStatus(test.Id));var row=Assert.Single(test.Repository.Uploads(test.Id),x=>x.LocalPath=="renamed.txt");Assert.Equal(1,row.Generation);
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Equal(row.Generation,test.Repository.Upload(row.Id)!.Generation);Assert.Equal("excluded bytes",await File.ReadAllTextAsync(renamed));
    }
    [Fact(Timeout=60000)]
    public async Task MissingOldPathCannotPruneRenamedFileIdentity()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
        var definition=test.Repository.LoadDefinitions().Single();var path=Path.Combine(test.Local,"prune-probe.txt");await File.WriteAllTextAsync(path,"unchanged bytes");
        var store=new FileExclusionMarkerStore(Path.Combine(test.Repository.DataDirectory,"prune-by-id"),test.Id);store.Reconcile(definition,"prune-probe.txt",true);
        var renamed=Path.Combine(test.Local,"after-rename.txt");File.Move(path,renamed);store.Prune(definition);Assert.True(store.HasMarks);
        store.Reconcile(definition,"after-rename.txt",false);Assert.False(store.HasMarks);Assert.Empty(CloudCustomStateReader.Read(renamed).Ids);Assert.Equal("unchanged bytes",await File.ReadAllTextAsync(renamed));
    }
    [Fact(Timeout=60000)]
    public async Task CorruptMarkerJournalCannotStopUnrelatedUploadsOrEnableExcludedFiles()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.DisposeAsync();
        var directory=Path.Combine(test.Repository.DataDirectory,"ExclusionMarkers");Directory.CreateDirectory(directory);var journal=Path.Combine(directory,test.Id.ToString("N")+".json");await File.WriteAllTextAsync(journal,"{broken");
        test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);
        await File.WriteAllTextAsync(Path.Combine(test.Local,"~excluded.txt"),"never send");await File.WriteAllTextAsync(Path.Combine(test.Local,"normal-after-corruption.txt"),"send normally");
        await test.Wait("normal-after-corruption.txt");await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.DoesNotContain(test.Dav.Objects.Keys,x=>x=="~excluded.txt");Assert.Contains("损坏",test.Manager.FileExclusionStatus(test.Id));Assert.Equal("{broken",await File.ReadAllTextAsync(journal));
    }
    [Fact(Timeout=60000)]
    public async Task NativeFailureBlocksUploadAndRestartCanRestoreOwnedMark()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var definition=test.Repository.LoadDefinitions().Single();var path=Path.Combine(test.Local,"文件.bin");var before=CloudFilesNative.Inspect(path)!.Value;
        var store=new FileExclusionMarkerStore(test.Repository.DataDirectory,test.Id){SetPinForTests=(_,_)=>unchecked((int)0x80070005)};
        Assert.ThrowsAny<Exception>(()=>store.Reconcile(definition,"文件.bin",true));Assert.Contains("80070005",store.Describe());Assert.Equal(before.Info.PinState,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
        var otherDirectory=Path.Combine(test.Repository.DataDirectory,"marker-restart");store=new(otherDirectory,test.Id);store.Reconcile(definition,"文件.bin",true);
        Assert.Equal(3u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);new FileExclusionMarkerStore(otherDirectory,test.Id).Reconcile(definition,"文件.bin",false);
        Assert.Equal(before.Info.PinState,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Empty(test.Repository.Uploads(test.Id));
    }
}
