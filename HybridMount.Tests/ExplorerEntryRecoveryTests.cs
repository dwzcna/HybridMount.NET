using System.Security.Principal;
using HybridMount.Models.Mounting.Recovery;
using Microsoft.Win32;

namespace HybridMount.Tests;

/// <summary>全部注册写入在内存完成；仅恢复日志落独立测试目录，不注册任何系统同步根。</summary>
public sealed class ExplorerEntryRecoveryTests
{
    private static readonly string Sid = WindowsIdentity.GetCurrent().User!.Value;
    [Theory]
    [InlineData("Packaged",true)]
    [InlineData("Unpackaged",true)]
    [InlineData("Other",false)]
    public void OnlyExactKnownProductIdentityIsRecognized(string mode,bool expected)
    {
        var id=$"HybridMount.NativePreview.{mode}!{Sid}!{Guid.NewGuid():N}";
        Assert.Equal(expected,ExplorerEntryRegistration.TryIdentity(id,Sid,out _));
        Assert.False(ExplorerEntryRegistration.TryIdentity(id,Sid+"-other",out _));
        Assert.False(ExplorerEntryRegistration.TryIdentity(id.Replace("HybridMount.","Foreign.HybridMount."),Sid,out _));
    }
    [Theory]
    [InlineData("Packaged",true,true)]
    [InlineData("Unpackaged",true,false)]
    [InlineData("Packaged",false,true)]
    [InlineData("Unpackaged",false,false)]
    public void DiscoveryIsReadOnlyAndMissingDirectoryOrRootRegistrationIsSupported(string mode,bool exists,bool manager)
    {
        using var f=new Fixture(); var raw=f.Add(mode,manager:manager);f.Paths.Value=new(exists,exists?"identity":"");
        var candidate=Assert.Single(f.Service().Discover(default));
        Assert.True(candidate.CanRemove,candidate.Error);Assert.Equal(mode,candidate.Key.Mode);Assert.Equal(exists,candidate.Evidence!.Exists);
        Assert.Equal(0,f.Registry.Deletes);Assert.Equal(0,f.Notifications);Assert.False(Directory.Exists(f.Journals));Assert.Equal(raw.Key,candidate.Key);
    }
    [Fact]
    public void SameNameAndPathDoNotCollapseDistinctIdsOrNamespaces()
    {
        using var f=new Fixture();var a=f.Add("Packaged");var b=f.Add("Unpackaged");
        var extra=f.Add("Packaged",id:a.Key.SyncRootId,manager:false);
        var found=f.Service().Discover(default);
        Assert.Equal(3,found.Length);Assert.Equal(3,found.Select(x=>x.Key.StableId).Distinct().Count());
        Assert.Contains(found,x=>x.Key==b.Key);Assert.Contains(found,x=>x.Key==extra.Key);
    }
    [Theory]
    [InlineData("Packaged")]
    [InlineData("Unpackaged")]
    public void EitherModesManagedIdOrOverlappingPathIsProtected(string mode)
    {
        using var f=new Fixture();var raw=f.Add(mode);var id=Guid.ParseExact(raw.Key.SyncRootId.Split('!')[2],"N");
        f.Managed.Add(new(id,"real mount",@"C:\another",false));
        Assert.False(Assert.Single(f.Service().Discover(default)).CanRemove);
        f.Managed.Clear();f.Managed.Add(new(Guid.NewGuid(),"real mount",@"C:\entry-test\child",false));
        Assert.False(Assert.Single(f.Service().Discover(default)).CanRemove);Assert.Equal(0,f.Registry.Deletes);
    }
    [Theory]
    [InlineData("native root")]
    [InlineData("access denied")]
    [InlineData("disk offline")]
    public void NativeRootAndUncertainPathsAreProtected(string failure)
    {
        using var f=new Fixture();f.Add();f.Paths.Error=new IOException(failure);
        var row=Assert.Single(f.Service().Discover(default));Assert.False(row.CanRemove);Assert.Contains(failure,row.Error!);Assert.Equal(0,f.Registry.Deletes);
    }
    [Fact]
    public void ExpandedEnvironmentTargetsCannotMasqueradeAsMissingLiteralFolders()
    {
        using var f=new Fixture();var raw=f.Add(manager:false);var keyPath=RecoveryShellRegistration.Classes+raw.Key.NamespaceId;
        var key=f.Registry.Data[(RegistryHive.CurrentUser,keyPath)];
        var bag=key.Tree.Children["Instance"].Children["InitPropertyBag"];
        key.Tree.Children["Instance"].Children["InitPropertyBag"]=bag with{Values=[new("TargetFolderPath",RegistryValueKind.ExpandString,[@"C:\%USERNAME%\files"])]};
        var row=Assert.Single(f.Service().Discover(default));Assert.False(row.CanRemove);Assert.Equal(0,f.Registry.Deletes);
    }
    [Fact]
    public void MissingClassAndRootPathAreDisplayedButCannotBeRemoved()
    {
        using var f=new Fixture();var raw=f.Add(manager:false);
        f.Registry.Data.Remove((RegistryHive.CurrentUser,RecoveryShellRegistration.Classes+raw.Key.NamespaceId));
        var row=Assert.Single(f.Service().Discover(default));Assert.False(row.CanRemove);Assert.Null(row.LocalPath);
    }
    [Fact]
    public void WrongOwnerOrMismatchedTargetAndSharedClassAreProtected()
    {
        using var f=new Fixture();var raw=f.Add();
        var path=RecoveryShellRegistration.Classes+raw.Key.NamespaceId;
        var key=f.Registry.Data[(RegistryHive.CurrentUser,path)];
        f.Registry.Data[(RegistryHive.CurrentUser,path)]=key with{Owner="other"};
        Assert.False(Assert.Single(f.Service().Discover(default)).CanRemove);
        f.Registry.Data[(RegistryHive.CurrentUser,path)]=key;
        f.Add("Unpackaged",ns:raw.Key.NamespaceId,withNamespace:false,withClass:false);
        Assert.All(f.Service().Discover(default),x=>Assert.False(x.CanRemove));
    }
    [Fact]
    public void RemoveDeletesOnlyPairedRegistrationAndLeavesFilesAndUnrelatedKeys()
    {
        using var f=new Fixture();var a=f.Add();var b=f.Add("Unpackaged");
        var contents=Path.Combine(f.Area,"keep.txt");File.WriteAllText(contents,"unchanged");
        var row=f.Service().Discover(default).Single(x=>x.Key==a.Key);
        var result=f.Service().Remove(row,default);
        Assert.True(result.Completed,result.Error);Assert.Equal("unchanged",File.ReadAllText(contents));Assert.Equal(1,f.Notifications);
        Assert.Empty(f.Registration.Remaining(row,row.Local!));Assert.Contains(f.Service().Discover(default),x=>x.Key==b.Key);
        Assert.Contains("Completed",File.ReadAllText(Assert.Single(Directory.GetFiles(f.Journals,"*.json"))));
    }
    [Fact]
    public void RegisteredNamespaceIdentityAndWholeSnapshotAreRecheckedBeforeDelete()
    {
        using var f=new Fixture();var raw=f.Add();var row=Assert.Single(f.Service().Discover(default));
        f.Registry.Data[(RegistryHive.CurrentUser,RecoveryShellRegistration.Namespace+raw.Key.NamespaceId)]=
            f.Registry.Data[(RegistryHive.CurrentUser,RecoveryShellRegistration.Namespace+raw.Key.NamespaceId)] with{Tree=Tree([Text("","another-product")])};
        Assert.False(f.Service().Remove(row,default).Completed);Assert.Equal(0,f.Registry.Deletes);
    }
    [Fact]
    public void NewConfigurationAndChangedDirectoryIdentityStopRemoval()
    {
        using var f=new Fixture();f.Add();var row=Assert.Single(f.Service().Discover(default));
        f.Paths.Value=new(true,"new-identity");Assert.False(f.Service().Remove(row,default).Completed);Assert.Equal(0,f.Registry.Deletes);
        f.Paths.Value=row.Evidence!;f.Managed.Add(new(Guid.NewGuid(),"live",@"C:\entry-test",false));
        Assert.False(f.Service().Remove(row,default).Completed);Assert.Equal(0,f.Registry.Deletes);
    }
    [Fact]
    public void PartialRemovalRetainsOriginalProofAndRetryCompletesWithoutNewRegistration()
    {
        using var f=new Fixture();var raw=f.Add();var row=Assert.Single(f.Service().Discover(default));
        f.Registry.FailPath=RecoveryShellRegistration.Classes+raw.Key.NamespaceId;
        Assert.False(f.Service().Remove(row,default).Completed);Assert.Equal(2,f.Registry.Deletes);
        var retry=Assert.Single(f.Service().Discover(default));
        Assert.True(f.Service().Remove(retry,default).Completed);Assert.Equal(3,f.Registry.Deletes);Assert.Empty(f.Service().Discover(default));
    }
    [Fact]
    public void SharedClassAppearingAfterPartialCleanupIsNotDeleted()
    {
        using var f=new Fixture();var raw=f.Add();var row=Assert.Single(f.Service().Discover(default));
        f.Registry.FailPath=RecoveryShellRegistration.Classes+raw.Key.NamespaceId;
        Assert.False(f.Service().Remove(row,default).Completed);
        f.Add("Unpackaged",ns:raw.Key.NamespaceId,withNamespace:false,withClass:false);
        var result=f.Service().Remove(row,default);Assert.False(result.Completed);
        Assert.NotNull(f.Registry.Read(RegistryHive.CurrentUser,RecoveryShellRegistration.Classes+raw.Key.NamespaceId));
    }
    [Fact]
    public void ForbiddenProofCannotDeleteOutsideWhitelistedRegistration()
    {
        using var f=new Fixture();f.Add();var row=Assert.Single(f.Service().Discover(default));
        var injected=new RecoveryRegistryBackup(RegistryHive.CurrentUser,@"Software\Unrelated",Sid,Tree([]));
        var proof=row.Local! with{Keys=row.Local!.Keys.Append(injected).ToArray()};
        Assert.Throws<IOException>(()=>f.Registration.Remove(row,proof,()=>{},default));Assert.Equal(0,f.Registry.Deletes);
    }
    [Fact]
    public void CancellationStopsBeforeAnyDeleteAndCanBeRetried()
    {
        using var f=new Fixture();f.Add();var row=Assert.Single(f.Service().Discover(default));using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>f.Service().Remove(row,cancel.Token));Assert.Equal(0,f.Registry.Deletes);
        Assert.True(f.Service().Remove(row,default).Completed);
    }
    [Fact]
    public void ManagedConfigurationReadFailureDoesNotYieldSelectableEntries()
    {
        using var f=new Fixture();f.Add();
        var service=new ExplorerEntryRecovery(f.Journals,()=>throw new UnauthorizedAccessException(),f.Access,f.Paths,_=>{});
        Assert.Throws<UnauthorizedAccessException>(()=>service.Discover(default));Assert.Equal(0,f.Registry.Deletes);
    }
    [Fact]
    public void HiddenValuesAreRemovedIndividuallyAndUnrelatedValuesRemain()
    {
        using var f=new Fixture();var raw=f.Add();var key=RecoveryShellRegistration.HiddenPaths[0];
        f.Registry.Data[(RegistryHive.CurrentUser,key)]=new(RegistryHive.CurrentUser,key,Sid,
            Tree([new(raw.Key.NamespaceId!,RegistryValueKind.DWord,["1"]),new("unrelated",RegistryValueKind.DWord,["0"])]));
        var row=Assert.Single(f.Service().Discover(default));Assert.True(f.Service().Remove(row,default).Completed);
        Assert.Equal("unrelated",Assert.Single(f.Registry.Read(RegistryHive.CurrentUser,key)!.Tree.Values).Name);
    }
    [Fact]
    public void PendingEntryRecoveryPreventsSettingsResetUntilVerifiedComplete()
    {
        using var f=new Fixture();var raw=f.Add();var row=Assert.Single(f.Service().Discover(default));
        f.Registry.FailPath=RecoveryShellRegistration.Classes+raw.Key.NamespaceId;
        Assert.False(f.Service().Remove(row,default).Completed);
        Assert.Throws<IOException>(()=>ExplorerEntryRecovery.EnsureNoPending(f.Journals));
        Assert.True(f.Service().Remove(row,default).Completed);ExplorerEntryRecovery.EnsureNoPending(f.Journals);
    }
    [Fact]
    public void ExistingLegacyRecoveryDoesNotGainCrossModeIdentityPermission()
    {
        var mode=ApplicationPaths.DeploymentScope=="Packaged"?"Unpackaged":"Packaged";
        Assert.False(OrphanedMountRecovery.TryParseId($"HybridMount.NativePreview.{mode}!{Sid}!{Guid.NewGuid():N}",Sid,out _));
    }
    [Fact]
    public void PrivateWireRoundtripRetainsBothViewProofsAndEvidence()
    {
        using var f=new Fixture();f.Add();var entry=Assert.Single(f.Service().Discover(default));entry=entry with{External=entry.Local};
        var message=new HybridMount.Services.Shell.ShellMaintenanceRequest("nonce","explorerRemove",Entry:entry,EntryProof:entry.External);
        var json=System.Text.Json.JsonSerializer.Serialize(message);var copy=System.Text.Json.JsonSerializer.Deserialize<HybridMount.Services.Shell.ShellMaintenanceRequest>(json)!;
        Assert.Equal(entry.Key,copy.Entry!.Key);Assert.Equal(entry.Evidence,copy.Entry.Evidence);Assert.NotNull(copy.Entry.External);
    }
    private static RecoveryRegistryValue Text(string name,string text)=>new(name,RegistryValueKind.String,[text]);
    private static RecoveryRegistryTree Tree(RecoveryRegistryValue[] values,Dictionary<string,RecoveryRegistryTree>? children=null)=>new(values,children??[]);
    private sealed class MemoryRegistry:IExplorerEntryRegistry
    {
        internal Dictionary<(RegistryHive,string),RecoveryRegistryBackup> Data=[];
        internal string? FailPath;internal int Deletes;
        public string[] Names(RegistryHive hive,string path)=>Data.Keys.Where(x=>x.Item1==hive&&x.Item2.StartsWith(path+"\\",StringComparison.Ordinal))
            .Select(x=>x.Item2[(path.Length+1)..].Split('\\')[0]).Distinct().ToArray();
        public RecoveryRegistryBackup? Read(RegistryHive hive,string path)=>Data.GetValueOrDefault((hive,path));
        public void Delete(RecoveryRegistryBackup expected)
        {
            if(FailPath==expected.Path){FailPath=null;throw new IOException("injected failure");}
            if(Read(expected.Hive,expected.Path) is not {} actual)return;
            if(!ExplorerEntryRegistry.Same(expected,actual))throw new IOException("changed");
            Data.Remove((expected.Hive,expected.Path));Deletes++;
        }
        public void DeleteValue(RecoveryHiddenValue expected)
        {
            var key=Read(RegistryHive.CurrentUser,expected.Path)!;
            var found=key.Tree.Values.SingleOrDefault(x=>x.Name==expected.Value.Name);
            if(found is null)return;
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected.Value),System.Text.Json.JsonSerializer.Serialize(found));
            Data[(RegistryHive.CurrentUser,expected.Path)]=key with{Tree=key.Tree with{Values=key.Tree.Values.Where(x=>x.Name!=found.Name).ToArray()}};
        }
    }
    private sealed class Guard:IExplorerEntryPathGuard
    {
        internal ExplorerPathEvidence Value=new(true,"identity");internal Exception? Error;
        public ExplorerPathEvidence Inspect(string path)=>Error is {} failure?throw failure:Value;
    }
    private sealed class Access(ExplorerEntryRegistration engine):IExplorerEntryAccess
    {
        public ExplorerEntryCandidate[] Discover(CancellationToken token)=>engine.Discover(token);
        public void Remove(ExplorerEntryCandidate entry,Action validate,CancellationToken token)=>engine.Remove(entry,entry.Local!,validate,token);
        public string[] Remaining(ExplorerEntryCandidate entry)=>engine.Remaining(entry,entry.Local!);
    }
    private sealed class Fixture:IDisposable
    {
        internal string Area=Path.Combine(AppContext.BaseDirectory,"ExplorerEntryTests",Guid.NewGuid().ToString("N"));
        internal string Journals=>Path.Combine(Area,"Recovery");
        internal MemoryRegistry Registry=new();internal Guard Paths=new();internal List<MountDefinition> Managed=[];internal int Notifications;
        internal ExplorerEntryRegistration Registration;internal Access Access;
        internal Fixture(){Directory.CreateDirectory(Area);Registration=new(Registry,Sid);Access=new(Registration);}
        internal ExplorerEntryRecovery Service()=>new(Journals,()=>Managed,Access,Paths,_=>Notifications++);
        internal ExplorerEntryCandidate Add(string mode="Packaged",string? id=null,string? ns=null,bool manager=true,bool withNamespace=true,bool withClass=true)
        {
            id??=$"HybridMount.NativePreview.{mode}!{Sid}!{Guid.NewGuid():N}";ns??=Guid.NewGuid().ToString("B").ToUpperInvariant();
            void Add(string path,RecoveryRegistryTree tree)=>Registry.Data[(RegistryHive.CurrentUser,path)]=new(RegistryHive.CurrentUser,path,Sid,tree);
            if(manager)Add(RecoveryShellRegistration.Manager+"\\"+id,Tree([Text("NamespaceCLSID",ns),Text("DisplayNameResource","same name")],new(){{"UserSyncRoots",Tree([Text(Sid,@"C:\entry-test")])}}));
            if(withNamespace)Add(RecoveryShellRegistration.Namespace+ns,Tree([Text("",id)]));
            if(withClass)Add(RecoveryShellRegistration.Classes+ns,Tree([Text("","same name")],new(){{"Instance",Tree([],new(){{"InitPropertyBag",Tree([Text("TargetFolderPath",@"C:\entry-test")])}})}}));
            return new(new(mode,id,ns),"same name",@"C:\entry-test",null);
        }
        public void Dispose(){if(Directory.Exists(Area))Directory.Delete(Area,true);}
    }
}
