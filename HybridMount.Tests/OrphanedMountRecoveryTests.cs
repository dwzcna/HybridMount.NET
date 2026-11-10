using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>隔离NTFS/CFAPI验证旧包遗留根，所有破坏性路径严格限于本测试GUID目录；不使用远端或正式定义。</summary>
[Collection("Native IPC")]
public sealed class OrphanedMountRecoveryTests
{
    [Theory]
    [InlineData("HybridMount.NET!S-1-5-21-1!00000000000000000000000000000001",true)]
    [InlineData("Other!S-1-5-21-1!00000000000000000000000000000001",false)]
    [InlineData("HybridMount.NET!S-1-5-21-2!00000000000000000000000000000001",false)]
    [InlineData("HybridMount.NET!S-1-5-21-1!00000000-0000-0000-0000-000000000001",false)]
    [InlineData("HybridMount.NET!S-1-5-21-1!00000000000000000000000000000000",false)]
    public void ExactProductOwnerAndNonEmptyGuidRequired(string value,bool valid)=>Assert.Equal(valid,OrphanedMountRecovery.TryParseId(value.Replace("HybridMount.NET!",ApplicationPaths.ProductScope+"!",StringComparison.Ordinal),"S-1-5-21-1",out _));

    [Fact(Timeout=60000)]
    public async Task DiscoversNativeRootButRejectsItsChildAndRegularDirectory()
    {
        await using var test=await Fixture.Create();var child=Path.Combine(test.Local,"child");Directory.CreateDirectory(child);
        test.Shell.Hints.Add(new(test.Candidate.SyncRootId,child));
        var discovered=test.Service().Discover(default);Assert.Equal(2,discovered.Count);Assert.Single(discovered,x=>x.CanRemove);
        Assert.Throws<IOException>(()=>test.Service().Inspect(child));Assert.Throws<HybridMount.Models.Mounting.Recovery.MissingOrphanedMountEvidenceException>(()=>test.Service().Inspect(test.Area));
        Assert.Throws<IOException>(()=>test.Service().Inspect(Path.GetPathRoot(test.Area)!));
    }
    [Fact(Timeout=60000)]
    public async Task ManagedIdsAndOverlappingPathsAreExcludedWithoutDeletion()
    {
        await using var test=await Fixture.Create();test.Managed.Add(test.Mount);
        Assert.Empty(test.Service().Discover(default));Assert.Throws<IOException>(()=>test.Service().Inspect(test.Local));
        test.Managed.Clear();test.Managed.Add(test.Mount with{LocalPath=Path.Combine(test.Local,"child"),Id=Guid.NewGuid()});
        Assert.Throws<IOException>(()=>test.Service().Inspect(test.Local));Assert.True(File.Exists(test.Cold));test.Managed.Clear();
    }
    [Fact(Timeout=60000)]
    public async Task ColdAndLocalFilesClearOfflineWithoutFollowingExternalLink()
    {
        await using var test=await Fixture.Create();var external=Path.Combine(test.Area,"outside");Directory.CreateDirectory(external);
        var sentinel=Path.Combine(external,"keep.txt");await File.WriteAllTextAsync(sentinel,"keep");Directory.CreateSymbolicLink(Path.Combine(test.Local,"link"),external);
        await File.WriteAllTextAsync(Path.Combine(test.Local,"local.txt"),"local modifications explicitly discarded");
        Assert.False(CloudFilesNative.HasFullContent(test.Cold,1024*1024));var result=await test.Service().ClearAsync(test.Candidate,default);
        Assert.True(result.Completed,string.Join(";",result.Errors));Assert.Equal(3,result.Deleted);Assert.Empty(Directory.EnumerateFileSystemEntries(test.Local));
        Assert.Equal("keep",await File.ReadAllTextAsync(sentinel));Assert.Null(CloudFilesNative.RootIdentity(test.Local,true));Assert.Equal(test.Mount.RootIdentity,MountPathGuard.FileIdentity(test.Local));
        Assert.DoesNotContain(test.Shell.Inner.List(),x=>x.Id==test.Candidate.SyncRootId);
    }
    [Fact(Timeout=60000)]
    public async Task LockedFileRetainsRegistrationAndExplicitRetryCompletes()
    {
        await using var test=await Fixture.Create();var busy=Path.Combine(test.Local,"busy.txt");await File.WriteAllTextAsync(busy,"busy");
        using(var handle=File.Open(busy,FileMode.Open,FileAccess.Read,FileShare.Read))
        {var result=await test.Service().ClearAsync(test.Candidate,default);Assert.False(result.Completed);Assert.Contains(result.Errors,x=>x.Contains("busy.txt",StringComparison.Ordinal));Assert.NotNull(MountPathGuard.ReadRoot(test.Local));}
        Assert.True((await test.Service().ClearAsync(test.Candidate,default)).Completed);
    }
    [Fact(Timeout=60000)]
    public async Task CancelPersistsProgressAndRestartDoesNotDeleteUntilExplicitRetry()
    {
        await using var test=await Fixture.Create();for(var i=0;i<5;i++)await File.WriteAllTextAsync(Path.Combine(test.Local,i+".txt"),"x");
        using var stop=new CancellationTokenSource();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>test.Service().ClearAsync(test.Candidate,stop.Token,line=>{if(line.Contains("已删除",StringComparison.Ordinal))stop.Cancel();}));
        var remaining=Directory.EnumerateFileSystemEntries(test.Local).Order().ToArray();Assert.NotEmpty(remaining);
        var restarted=test.Service();Assert.Single(restarted.Discover(default));Assert.Equal(remaining,Directory.EnumerateFileSystemEntries(test.Local).Order().ToArray());
        Assert.True((await restarted.ClearAsync(restarted.Inspect(test.Local),default)).Completed);
    }
    [Fact(Timeout=60000)]
    public async Task StaleIdentityAndPreCancelledOperationDeleteNothing()
    {
        await using var test=await Fixture.Create();await Assert.ThrowsAsync<IOException>(()=>test.Service().ClearAsync(test.Candidate with{RootIdentity="wrong"},default));
        using var stop=new CancellationTokenSource();stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>test.Service().ClearAsync(test.Candidate,stop.Token));Assert.True(File.Exists(test.Cold));
    }
    [Fact(Timeout=60000)]
    public async Task LostPackageWinRtUnregisterFallsBackToSupportedNativeApi()
    {
        await using var test=await Fixture.Create();test.Shell.FailUnregister=true;
        var result=await test.Service().ClearAsync(test.Candidate,default);Assert.True(result.Completed,string.Join(";",result.Errors));Assert.Equal(1,test.Shell.UnregisterCalls);Assert.Null(CloudFilesNative.RootIdentity(test.Local,true));
    }
    [Fact(Timeout=60000)]
    public async Task PartialUnregistrationRestartsOnlyOnSameEmptyIdentity()
    {
        await using var test=await Fixture.Create();test.Shell.FailCleanup=true;
        var first=await test.Service().ClearAsync(test.Candidate,default);Assert.False(first.Completed);Assert.Equal("NativeRemoved",first.Stage);Assert.Null(CloudFilesNative.RootIdentity(test.Local,true));
        var restarted=test.Service();var pending=Assert.Single(restarted.Discover(default));Assert.True(pending.CanRemove);Assert.False(pending.Registered);
        var replacement=Path.Combine(test.Local,"new.txt");await File.WriteAllTextAsync(replacement,"must not be deleted by old journal");Assert.Throws<IOException>(()=>restarted.Inspect(test.Local));
        File.Delete(replacement);test.Shell.FailCleanup=false;Assert.True((await test.Service().ClearAsync(pending,default)).Completed);
    }
    [Fact(Timeout=60000)]
    public async Task ActiveProviderPreventsOfflineTakeover()
    {
        await using var test=await Fixture.Create();var repository=new MountRepository(Path.Combine(test.Area,"live"),definitions:new MemoryMountStore());
        await using(var connected=new CloudMountSession(test.Mount,null,repository,test.Logger,blockHydration:true))
        {connected.Connect();var result=await test.Service().ClearAsync(test.Candidate,default);Assert.False(result.Completed);Assert.True(File.Exists(test.Cold));}
    }
    [Fact(Timeout=60000)]
    public async Task ConfigurationAppearingBeforeUnregistrationStopsCommit()
    {
        await using var test=await Fixture.Create();var service=test.Service();service.StageForTests=stage=>{if(stage=="Unregistering")test.Managed.Add(test.Mount);};
        var result=await service.ClearAsync(test.Candidate,default);Assert.False(result.Completed);Assert.NotNull(MountPathGuard.ReadRoot(test.Local));test.Managed.Clear();
    }
    [Fact(Timeout=60000)]
    public async Task RegistryChangeAfterBackupIsPreservedAndCanResumeAfterExactCorrection()
    {
        await using var test=await Fixture.Create();test.Shell.FailUnregister=true;
        var keyPath=@"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager\"+test.Candidate.SyncRootId;
        var service=test.Service();service.StageForTests=stage=>{if(stage=="Unregistering"){using var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath,true);Assert.NotNull(key);key.SetValue("RecoveryTestMutation",7);}};
        var result=await service.ClearAsync(test.Candidate,default);Assert.False(result.Completed);Assert.Equal("NativeRemoved",result.Stage);
        using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath,true)){Assert.NotNull(key);Assert.Equal(7,key.GetValue("RecoveryTestMutation"));key.DeleteValue("RecoveryTestMutation");}
        Assert.True((await test.Service().ClearAsync(test.Service().Inspect(test.Local),default)).Completed);
    }
    [Fact(Timeout=60000)]
    public async Task ApplicationStopCancelsRecoveryAndRejectsLateRequestsWithoutSource()
    {
        await using var test=await Fixture.Create();for(var i=0;i<4;i++)await File.WriteAllTextAsync(Path.Combine(test.Local,i+".txt"),"x");
        var factory=new NoSourceFactory();var repository=new MountRepository(Path.Combine(test.Area,"application"),definitions:new MemoryMountStore());
        await using(var manager=new MountManager(repository,test.Logger,factory))
        {
            var work=manager.ClearOrphanedMountAsync(test.Candidate,progress:line=>{if(line.Contains("已删除",StringComparison.Ordinal))manager.RequestStop();});
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);await manager.DisposeAsync();
            Assert.True(manager.IsStopping);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>manager.DiscoverOrphanedMountsAsync());Assert.Equal(0,factory.Calls);Assert.NotEmpty(Directory.EnumerateFileSystemEntries(test.Local));
        }
    }
    [Fact(Timeout=60000)]
    public async Task DuplicateApplicationRequestsAreSerializedWithoutReplayingDeletion()
    {
        await using var test=await Fixture.Create();var factory=new NoSourceFactory();var repository=new MountRepository(Path.Combine(test.Area,"application"),definitions:new MemoryMountStore());
        await using var manager=new MountManager(repository,test.Logger,factory);var first=manager.ClearOrphanedMountAsync(test.Candidate);var second=manager.ClearOrphanedMountAsync(test.Candidate);
        Assert.True((await first).Completed);await Assert.ThrowsAsync<HybridMount.Models.Mounting.Recovery.MissingOrphanedMountEvidenceException>(()=>second);Assert.Empty(Directory.EnumerateFileSystemEntries(test.Local));Assert.Equal(0,factory.Calls);
    }
    private sealed class NoSourceFactory:IFileSourceFactory
    {internal int Calls;public Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token){Calls++;throw new InvalidOperationException("Recovery must never create a remote source.");}}
    private sealed class MemoryMountStore:IMountDefinitionStore
    {public IReadOnlyList<MountDefinition> LoadDefinitions()=>[];public void SaveDefinition(MountDefinition mount)=>throw new NotSupportedException();public void DeleteDefinition(Guid id)=>throw new NotSupportedException();public long CacheLimitBytes{get;set;}}
    private sealed class Shell:IRecoveryShellRegistration
    {
        internal readonly RecoveryShellRegistration Inner=new();internal readonly List<RecoveryRegistration> Hints=[];internal bool FailUnregister,FailCleanup;internal int UnregisterCalls;
        public IReadOnlyList<RecoveryRegistration> List()=>Hints;
        public RecoveryShellProof? Capture(OrphanedMountCandidate root)=>Inner.Capture(root);
        public void Unregister(string id){UnregisterCalls++;if(FailUnregister)throw new COMException("simulated lost package",unchecked((int)0x80070490));Inner.Unregister(id);}
        public void Cleanup(OrphanedMountCandidate root,RecoveryShellProof? proof,Action verify){if(FailCleanup)throw new IOException("simulated shell failure");Inner.Cleanup(root,proof,verify);}
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal readonly string Area=Path.Combine(Path.GetTempPath(),"HybridMount.OrphanTests",Guid.NewGuid().ToString("N"));
        internal string Local=>Path.Combine(Area,"view");internal string Cold=>Path.Combine(Local,"cold.bin");
        internal LoggerService Logger=null!;internal MountDefinition Mount=null!;internal OrphanedMountCandidate Candidate=null!;
        internal readonly Shell Shell=new();internal readonly List<MountDefinition> Managed=[];
        internal OrphanedMountRecovery Service()=>new(Path.Combine(Area,"recovery"),()=>Managed,Logger,Shell);
        internal static async Task<Fixture> Create()
        {
            var test=new Fixture();Directory.CreateDirectory(test.Local);test.Logger=new LoggerService(Path.Combine(test.Area,"log.db"));
            test.Mount=new(Guid.NewGuid(),"孤立测试",test.Local,false,RootIdentity:MountPathGuard.FileIdentity(test.Local));
            try
            {
                await CloudRootRegistration.EnsureAsync(test.Mount,default);
                var bytes=JsonSerializer.SerializeToUtf8Bytes(new MountPathGuard.EntryMarker(test.Mount.Id,"cold"));var memory=Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes,0,memory,bytes.Length);var entries=new[]{new CloudFilesNative.PlaceholderCreateInfo{RelativeName="cold.bin",Metadata=new(){Basic=new(){FileAttributes=0x20},FileSize=1024*1024},Identity=memory,IdentityLength=(uint)bytes.Length,Flags=2}};
                    CloudFilesNative.Check(CloudFilesNative.CfCreatePlaceholders(test.Local,entries,1,0,out var processed));Assert.Equal(1u,processed);CloudFilesNative.Check(entries[0].Result);
                }
                finally{Marshal.FreeHGlobal(memory);}
                test.Candidate=test.Service().Inspect(test.Local);test.Shell.Hints.Add(new(test.Candidate.SyncRootId,test.Local));return test;
            }
            catch{await test.DisposeAsync();throw;}
        }
        public async ValueTask DisposeAsync()
        {
            Managed.Clear();Shell.FailCleanup=Shell.FailUnregister=false;
            try
            {
                if(MountPathGuard.ReadRoot(Local) is not null||Directory.EnumerateFiles(Path.Combine(Area,"recovery"),"*.json").Any())
                {var candidate=Service().Inspect(Local);await Service().ClearAsync(candidate,default);}
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){System.Diagnostics.Trace.WriteLine("保留隔离恢复测试现场："+error.Message);}
            await Logger.DisposeAsync();
            var intended=Path.Combine(Path.GetTempPath(),"HybridMount.OrphanTests");
            if(MountPathGuard.IsUnder(intended,Area)&&Area!=intended&&CloudFilesNative.RootIdentity(Local,true) is null&&!MountPathGuard.ContainsCloudPlaceholders(Local))Directory.Delete(Area,true);
        }
    }
}
