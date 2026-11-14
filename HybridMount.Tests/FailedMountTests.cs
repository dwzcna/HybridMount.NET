using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.Win32;

namespace HybridMount.Tests;

/// <summary>注册前失败、旧配置迁移和连接／移除竞争回归；仅使用隔离 NTFS 根。</summary>
public sealed class FailedMountTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task FailedBeforeRegistrationRemovesOfflineAndKeepsHistory(bool clear,bool legacy)
    {
        await using var test=new Fixture();var mount=await test.FailAddAsync();
        Assert.Equal(MountRegistrationStage.NotRegistered,Assert.Single(test.Manager.Mounts).Definition.RegistrationStage);
        await File.WriteAllTextAsync(Path.Combine(test.Local,"local.txt"),"keep unless clear");
        if(legacy)test.Repository.SaveDefinition(mount with{RegistrationStage=MountRegistrationStage.Unknown});
        await test.RestartAsync();Assert.True(test.Manager.CanEditRemoteRoot(mount.Id));
        Assert.True(await test.Manager.CanEditRemoteRootAsync(mount.Id));
        var result=await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,clear?"clearAndRemove":"remove"));
        Assert.True(result.State=="Completed",result.Error);Assert.Empty(test.Manager.Mounts);
        Assert.Equal(1,test.Factory.Calls);Assert.True(Directory.Exists(test.Local));
        Assert.Equal(!clear,File.Exists(Path.Combine(test.Local,"local.txt")));
        Assert.Empty(test.Repository.Entries(mount.Id));Assert.Empty(test.Repository.LoadDefinitions());
        Assert.Contains(test.Repository.LoadOperations(),r=>r.Id==result.Id);
        Assert.False(File.Exists(Path.Combine(test.Repository.DataDirectory,"Credentials",mount.Id.ToString("N")+".bin")));
    }
    [Fact]
    public async Task NeverRegisteredCanEditRemoteRootAndFailedReconnectKeepsNewSettings()
    {
        await using var test=new Fixture();var mount=await test.FailAddAsync();
        var options=new FileSourceConfiguration(FileSourceKind.WebDav,"https://changed.example/dav/");
        await Assert.ThrowsAsync<IOException>(()=>test.Manager.UpdateCredentialsAsync(mount.Id,new("new-user","new-secret"),settings:options));
        Assert.Equal(options,Assert.Single(test.Repository.LoadDefinitions()).Source);
        Assert.Equal("new-user",test.Manager.GetCredentials(mount.Id).UserName);
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,"remove"))).State);
    }
    [Fact]
    public async Task AsyncRemoteRootCheckRejectsMissingMountAndCancellation()
    {
        await using var test=new Fixture();
        await Assert.ThrowsAsync<IOException>(()=>test.Manager.CanEditRemoteRootAsync(Guid.NewGuid()));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>test.Manager.CanEditRemoteRootAsync(Guid.NewGuid(),cancel.Token));
    }
    [Fact]
    public async Task InvalidEditedInputPreservesSavedSettingsAndCredentials()
    {
        await using var test=new Fixture();var mount=await test.FailAddAsync();
        var before=test.Manager.GetCredentials(mount.Id);
        await Assert.ThrowsAsync<ArgumentException>(()=>test.Manager.UpdateCredentialsAsync(mount.Id,new("bad:account","changed"),settings:mount.Source! with{Endpoint="https://changed.example/dav/"}));
        Assert.Equal(before,test.Manager.GetCredentials(mount.Id));Assert.Equal(mount.Source,Assert.Single(test.Repository.LoadDefinitions()).Source);
        Assert.Equal(1,test.Factory.Calls);
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,"remove"))).State);
    }
    [Fact(Timeout=60000)]
    public async Task RemovalCancelsNonCooperativeConnectionAndLateResultCannotRegister()
    {
        await using var test=new Fixture();var mount=await test.FailAddAsync();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken connecting=default;
        test.Factory.Connect=async token=>
        {
            connecting=token;started.SetResult();await release.Task;
            return new MockFileSource(Path.Combine(test.Area,"late-source"));
        };
        var connection=test.Manager.ConnectAsync(mount.Id);await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedConnection=test.Manager.ConnectAsync(mount.Id);
        var removal=test.Manager.Queue(mount.Id,"clearAndRemove");
        Assert.True(connecting.IsCancellationRequested);release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>connection);
        Assert.NotNull(await Record.ExceptionAsync(()=>queuedConnection));
        var result=await WebDavTests.Finished(test.Manager,removal);Assert.True(result.State=="Completed",result.Error);
        Assert.Empty(test.Manager.Mounts);Assert.Null(CloudFilesNative.RootIdentity(test.Local,strict:true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(test.Local));
    }
    [Fact]
    public async Task UnregisteredLockedClearRetainsModeAndExplicitRestartRetry()
    {
        await using var test=new Fixture();var mount=await test.FailAddAsync();
        var path=Path.Combine(test.Local,"locked.txt");await File.WriteAllTextAsync(path,"local");
        using(var handle=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var failure=await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,"clearAndRemove"));
            Assert.Equal("PartialFailure",failure.State);Assert.Contains(failure.Results,r=>r.Status=="Failed");
        }
        await test.RestartAsync();Assert.True(File.Exists(path));
        Assert.Throws<IOException>(()=>test.Manager.Queue(mount.Id,"remove"));
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,"clearAndRemove"))).State);
    }
    [Fact(Timeout=60000)]
    public async Task ForeignRootRejectedAndNativeUnregisterOutcomeHandled()
    {
        await using var test=new Fixture();
        await test.Manager.DisposeAsync();
        await using var owner=new MountManager(test.Repository,test.Logger,TestSourceFactory.Instance);
        var mount=await owner.AddAsync("owned",test.Local,false,source:new(FileSourceKind.WebDav,"https://original.example/dav/"));
        Assert.Equal(MountRegistrationStage.Registered,Assert.Single(owner.Mounts).Definition.RegistrationStage);
        Assert.False(owner.CanEditRemoteRoot(mount.Id));
        Assert.False(await owner.CanEditRemoteRootAsync(mount.Id));
        await Assert.ThrowsAsync<IOException>(()=>owner.UpdateCredentialsAsync(mount.Id,new(),settings:mount.Source! with{Endpoint="https://changed.example/dav/"}));
        Assert.Equal(mount.Source,Assert.Single(owner.Mounts).Definition.Source);
        var foreign=mount with{Id=Guid.NewGuid(),RegistrationStage=MountRegistrationStage.Unknown};
        var foreignRepo=new MountRepository(Path.Combine(test.Area,"foreign-data"),test.RegistryPath+"-foreign");foreignRepo.SaveDefinition(foreign);
        await using(var outsider=new MountManager(foreignRepo,test.Logger,test.Factory))
        {
            foreach(var operation in new[]{"remove","clearAndRemove"})
                Assert.Equal("Failed",(await WebDavTests.Finished(outsider,outsider.Queue(foreign.Id,operation))).State);
            Assert.Single(outsider.Mounts);
        }
        Registry.CurrentUser.DeleteSubKeyTree(test.RegistryPath+"-foreign",false);
        // 生产注销不允许在非空目录上重新注册；先经产品清空并完成真实注销，再注入残留配置模拟崩溃边界。
        Assert.Equal("Completed",(await WebDavTests.Finished(owner,owner.Queue(mount.Id,"clearAndRemove"))).State);
        Assert.Null(CloudFilesNative.RootIdentity(test.Local,true));
        test.Repository.SaveDefinition(mount with{RemovalPending=true,RemovalMode=MountRemovalMode.ClearContents,RemovalStage=MountRemovalStage.Unregistering});
        await using var orphan=new MountManager(test.Repository,test.Logger,test.Factory);
        Assert.False(orphan.CanEditRemoteRoot(mount.Id));
        Assert.False(await orphan.CanEditRemoteRootAsync(mount.Id));
        var result=await WebDavTests.Finished(orphan,orphan.Queue(mount.Id,"clearAndRemove"));
        Assert.True(result.State=="Completed",result.Error);
    }
    [Fact]
    public async Task UnregisteredClearDeletesJunctionOnlyAndKeepsOutsideTarget()
    {
        await using var test=new Fixture();var mount=await test.FailAddAsync();
        var outside=Path.Combine(test.Area,"outside");Directory.CreateDirectory(outside);
        var sentinel=Path.Combine(outside,"keep.txt");await File.WriteAllTextAsync(sentinel,"outside");
        ClearRemovalTests.CreateJunction(Path.Combine(test.Local,"link"),outside);
        var result=await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,"clearAndRemove"));
        Assert.True(result.State=="Completed",result.Error);Assert.Equal("outside",await File.ReadAllTextAsync(sentinel));
        Assert.Empty(Directory.EnumerateFileSystemEntries(test.Local));Assert.Equal(1,test.Factory.Calls);
    }
    [Fact(Timeout=60000)]
    public async Task RootReplacedDuringProbeCannotBeRegistered()
    {
        await using var test=new Fixture();var mount=await test.FailAddAsync();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Factory.Connect=async _=>{started.SetResult();await release.Task;return new MockFileSource(Path.Combine(test.Area,"late-source"));};
        var connect=test.Manager.ConnectAsync(mount.Id);await started.Task;
        var backup=Path.Combine(test.Area,"previous-view");
        Assert.True(MountPathGuard.IsUnder(test.Area,test.Local)&&MountPathGuard.IsUnder(test.Area,backup));
        Directory.Move(test.Local,backup);Directory.CreateDirectory(test.Local);
        try
        {
            release.SetResult();var rejected=await Record.ExceptionAsync(()=>connect);
            Assert.True(rejected is IOException||rejected is System.Runtime.InteropServices.COMException {HResult:unchecked((int)0x80070005)},rejected?.ToString());
            Assert.Null(CloudFilesNative.RootIdentity(test.Local,strict:true));
            Assert.Equal("Failed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,"clearAndRemove"))).State);
            Assert.Single(test.Repository.LoadDefinitions());
        }
        finally
        {
            Directory.Delete(test.Local);Directory.Move(backup,test.Local);
            Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,"remove"))).State);
        }
    }
    [Fact]
    public async Task UnknownRegistrationWithMaterializedIndexRequiresRecovery()
    {
        await using var test=new Fixture();var mount=await test.FailAddAsync();
        await File.WriteAllTextAsync(Path.Combine(test.Local,"keep.txt"),"preserved");
        test.Repository.SaveDefinition(mount with{RegistrationStage=MountRegistrationStage.Unknown});
        test.Repository.SaveEntry(mount.Id,new("known","remote","keep.txt","old",false,Materialized:true));
        await test.RestartAsync();Assert.Equal("需要恢复",Assert.Single(test.Manager.Mounts).Status);
        foreach(var mode in new[]{"remove","clearAndRemove"})
            Assert.Equal("Failed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,mode))).State);
        Assert.Equal("preserved",await File.ReadAllTextAsync(Path.Combine(test.Local,"keep.txt")));
        Assert.Equal(1,test.Factory.Calls);
        test.Repository.DeleteEntries(mount.Id);test.Repository.SaveDefinition(mount with{RegistrationStage=MountRegistrationStage.Unknown});
        await test.RestartAsync();
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(mount.Id,"remove"))).State);
    }
    private sealed class FailureFactory : IFileSourceFactory
    {
        internal int Calls;
        internal Func<CancellationToken,Task<IFileSource>>? Connect;
        public Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)
        { Calls++;return Connect is null?Task.FromException<IFileSource>(new IOException("read-only authentication failed")):Connect(token); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal string Area { get; }=Path.Combine(Path.GetTempPath(),"HybridMount.FailedTests",Guid.NewGuid().ToString("N"));
        internal string Local=>Path.Combine(Area,"view");
        internal string RegistryPath { get; }=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        internal MountRepository Repository { get; }
        internal LoggerService Logger { get; }
        internal FailureFactory Factory { get; }=new();
        internal MountManager Manager { get; private set; }
        internal Fixture()
        {
            Directory.CreateDirectory(Local);Repository=new MountRepository(Path.Combine(Area,"data"),RegistryPath);
            Logger=new LoggerService(Path.Combine(Area,"log.db"));Manager=new MountManager(Repository,Logger,Factory);
        }
        internal async Task<MountDefinition> FailAddAsync()
        {
            await Assert.ThrowsAsync<IOException>(()=>Manager.AddAsync("failed",Local,false,source:new(FileSourceKind.WebDav,"https://example.test/dav/"),credentials:new("user","secret")));
            return Assert.Single(Manager.Mounts).Definition;
        }
        internal async Task RestartAsync()
        { await Manager.DisposeAsync();Manager=new MountManager(Repository,Logger,Factory);await Manager.StartAsync(); }
        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();await Logger.DisposeAsync();
            var expected=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"HybridMount.FailedTests"));
            if(MountPathGuard.IsUnder(expected,Area)&&Area!=expected&&!MountPathGuard.ContainsCloudPlaceholders(Local)&&CloudFilesNative.RootIdentity(Local,strict:true) is null)
            { Registry.CurrentUser.DeleteSubKeyTree(RegistryPath,false);Directory.Delete(Area,true); }
        }
    }
}
