using HybridMount.Models.Mounting;
using HybridMount.Models.Mounting.Recovery;
using HybridMount.Services.Mounting;
using Microsoft.Win32;

namespace HybridMount.Tests;

/// <summary>批次接纳、离线失败根、快照和配置门禁；全部数据属于本用例，无远端客户端。</summary>
public sealed class MountRemovalBatchTests
{
    [Fact(Timeout=120000)]
    public async Task FailedRootDoesNotStopNextRootAndRetainsRetryEvidence()
    {
        await using var test=new Fixture();var first=test.Add("a");var second=test.Add("b");
        var locked=Path.Combine(first.LocalPath,"locked.txt");File.WriteAllText(locked,"retain");
        var other=Path.Combine(second.LocalPath,"keep.txt");File.WriteAllText(other,"other");
        test.Start();var plan=await test.Manager.InspectAllMountRemovalsAsync();Assert.Equal(2,plan.Mounts.Count);
        var failedOnce=0;
        test.Manager.RemovalStageForTests=stage=>{if(stage=="ContentComplete"&&Interlocked.Increment(ref failedOnce)==1)throw new IOException("Injected first-root checkpoint failure");};

        {
            var result=await test.Manager.RemoveAllMountsAsync(plan,MountRemovalMode.PreserveCachedFiles);
            Assert.Equal(1,result.Succeeded);Assert.Equal(1,result.Failed);Assert.Equal(0,result.Skipped);
            Assert.All(result.Items,item=>Assert.NotNull(item.TaskId));Assert.False(test.Manager.RemovalBatchActive);
            Assert.Equal(first.Id,Assert.Single(test.Repository.LoadDefinitions()).Id);Assert.Equal("other",File.ReadAllText(other));
        }
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(first.Id,"removeCached"))).State);
        Assert.Equal("retain",File.ReadAllText(locked));Assert.Equal(0,test.Factory.Calls);
    }
    [Fact(Timeout=120000)]
    public async Task AdmissionBlocksDuplicateBatchNewMountImportAndResetThenContinuesAcceptedTask()
    {
        await using var test=new Fixture();var root=test.Add("a");test.Start();
        using var entered=new ManualResetEventSlim();using var resume=new ManualResetEventSlim();
        test.Manager.RemovalStageForTests=stage=>{if(stage=="ContentComplete"){entered.Set();if(!resume.Wait(TimeSpan.FromSeconds(20)))throw new TimeoutException();}};
        var plan=await test.Manager.InspectAllMountRemovalsAsync();var batch=test.Manager.RemoveAllMountsAsync(plan,MountRemovalMode.PreserveCachedFiles);
        try
        {
            Assert.True(await Task.Run(()=>entered.Wait(TimeSpan.FromSeconds(15))));Assert.True(test.Manager.RemovalBatchActive);
            await Assert.ThrowsAsync<IOException>(()=>test.Manager.RemoveAllMountsAsync(plan,MountRemovalMode.ClearContents));
            await Assert.ThrowsAsync<IOException>(()=>test.Manager.AddAsync("blocked",test.Area,false));
            await Assert.ThrowsAsync<IOException>(()=>test.Manager.ImportConfigurationsAsync([]));

            var item=test.Manager.Operations.Single(op=>op.Operation=="removeCached"&&op.MountId==root.Id);
            Assert.Equal(item.Id,test.Manager.Queue(root.Id,"removeCached"));
            Assert.Throws<IOException>(()=>test.Manager.Queue(root.Id,"clearAndRemove"));
        }
        finally{resume.Set();}
        Assert.Equal(1,(await batch).Succeeded);Assert.Empty(test.Repository.LoadDefinitions());
    }
    [Fact(Timeout=120000)]
    public async Task DifferentPendingModeIsSkippedAndOtherRootIsCleared()
    {
        await using var test=new Fixture();var first=test.Add("a");var second=test.Add("b");
        test.Repository.SaveDefinition(first with{RemovalPending=true,RemovalMode=MountRemovalMode.PreserveCachedFiles});
        var kept=Path.Combine(first.LocalPath,"keep.txt");File.WriteAllText(kept,"kept");File.WriteAllText(Path.Combine(second.LocalPath,"discard.txt"),"discard");
        test.Start();var plan=await test.Manager.InspectAllMountRemovalsAsync();
        var result=await test.Manager.RemoveAllMountsAsync(plan,MountRemovalMode.ClearContents);
        Assert.Equal(1,result.Skipped);Assert.Equal(1,result.Succeeded);Assert.True(File.Exists(kept));Assert.Empty(Directory.EnumerateFileSystemEntries(second.LocalPath));
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(first.Id,"removeCached"))).State);
    }
    [Fact(Timeout=120000)]
    public async Task ChangedSnapshotDoesNotRemoveAnyRoot()
    {
        await using var test=new Fixture();test.Add("a");test.Start();var plan=await test.Manager.InspectAllMountRemovalsAsync();
        var changed=plan with{Mounts=Array.AsReadOnly(plan.Mounts.Select(item=>item with{RootIdentity="different"}).ToArray())};
        await Assert.ThrowsAsync<IOException>(()=>test.Manager.RemoveAllMountsAsync(changed,MountRemovalMode.ClearContents));
        Assert.Single(test.Repository.LoadDefinitions());Assert.False(test.Manager.RemovalBatchActive);
        Assert.Equal(1,(await test.Manager.RemoveAllMountsAsync(plan,MountRemovalMode.PreserveCachedFiles)).Succeeded);
    }
    private sealed class NoSourceFactory:IFileSourceFactory
    {
        internal int Calls;
        public Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)
        {Calls++;throw new IOException("An offline removal must never create a network source.");}
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal string Area {get;}=Path.Combine(Path.GetTempPath(),"HybridMount.BatchRemovalTests",Guid.NewGuid().ToString("N"));
        private string Key {get;}=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        internal MountRepository Repository {get;}
        internal NoSourceFactory Factory {get;}=new();
        private LoggerService Logger {get;}
        internal MountManager Manager {get;private set;}=null!;
        internal Fixture(){Directory.CreateDirectory(Area);Repository=new(Path.Combine(Area,"data"),Key);Logger=new(Path.Combine(Area,"log.db"));}
        internal MountDefinition Add(string name)
        {
            var path=Directory.CreateDirectory(Path.Combine(Area,name)).FullName;
            var mount=new MountDefinition(Guid.NewGuid(),name,path,false,RootIdentity:MountPathGuard.FileIdentity(path),RegistrationStage:MountRegistrationStage.NotRegistered);
            Repository.SaveDefinition(mount);return mount;
        }
        internal void Start()=>Manager=new(Repository,Logger,Factory);
        public async ValueTask DisposeAsync()
        {
            if(Manager is not null)await Manager.DisposeAsync();await Logger.DisposeAsync();
            var expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"HybridMount.BatchRemovalTests"));
            if(Area!=expected&&MountPathGuard.IsUnder(expected,Area)&&Directory.GetDirectories(Area).Where(path=>Path.GetFileName(path)!="data").All(path=>!MountPathGuard.ContainsCloudPlaceholders(path)&&CloudFilesNative.RootIdentity(path,true) is null))
            {Registry.CurrentUser.DeleteSubKeyTree(Key,false);Directory.Delete(Area,true);}
        }
    }
}
