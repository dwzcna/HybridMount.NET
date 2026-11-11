using HybridMount.Services;
namespace HybridMount.Tests;

/// <summary>仅在随机临时LocalAppData核验清理范围；不调用实际还原服务、注册表或正式挂载。</summary>
public sealed class SettingsResetScopeTests
{
    private sealed class Area:IDisposable
    {
        internal readonly string Local=Path.Combine(Path.GetTempPath(),"HybridMount.ResetTests."+Guid.NewGuid().ToString("N"));
        internal SettingsResetScope Scope=>SettingsResetScope.Create(false,Local);
        internal Area()=>Directory.CreateDirectory(Local);
        internal string Populate(){var root=Scope.Directories[0];Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"keep.dat"),"original");return root;}
        public void Dispose()
        {
            var full=Path.GetFullPath(Local);var parent=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
            if(!full.StartsWith(parent+"HybridMount.ResetTests.",StringComparison.OrdinalIgnoreCase))throw new IOException("测试范围变化。");
            if(Directory.Exists(full))Directory.Delete(full,true);
        }
    }
    [Fact]
    public void CurrentModeDeletionPreservesOtherModeAndFormalData()
    {
        using var area=new Area();var own=area.Populate();var other=SettingsResetScope.Create(true,area.Local,"Test.NativePreview_abc").Directories[0];var formal=Path.Combine(area.Local,"HybridMount.NET","formal");
        foreach(var directory in new[]{other,formal}){Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"keep.dat"),"original");}
        var plan=new SettingsResetPlan(area.Scope,SettingsResetDirectories.Capture(area.Scope,CancellationToken.None));SettingsResetDirectories.Validate(plan,CancellationToken.None);
        foreach(var proof in plan.Directories)SettingsResetDirectories.Delete(proof);
        Assert.False(Directory.Exists(own));Assert.Equal("original",File.ReadAllText(Path.Combine(other,"keep.dat")));Assert.Equal("original",File.ReadAllText(Path.Combine(formal,"keep.dat")));Assert.True(Directory.Exists(Path.GetDirectoryName(own)));
    }
    [Fact]
    public void PackagedScopeExcludesSharedParentsOtherModeAndRuntimeState()
    {
        using var area=new Area();var scope=SettingsResetScope.Create(true,area.Local,"Test.NativePreview_abc");var package=Path.Combine(area.Local,"Packages","Test.NativePreview_abc");
        Assert.Equal(5,scope.Directories.Count);Assert.DoesNotContain(package,scope.Directories);Assert.DoesNotContain(Path.Combine(package,"Settings"),scope.Directories);Assert.DoesNotContain(area.Scope.Directories[0],scope.Directories);Assert.False(scope.SameAs(area.Scope));
        Assert.All(scope.Directories.Skip(1),path=>Assert.StartsWith(package+Path.DirectorySeparatorChar,path));Assert.True(((ICollection<string>)scope.Directories).IsReadOnly);
    }
    [Theory]
    [InlineData("..")][InlineData("x/y")][InlineData(@"x\y")][InlineData("C:other")]
    public void PackageFamilyCannotEscapeGeneratedScope(string family)
    {using var area=new Area();Assert.Throws<IOException>(()=>SettingsResetScope.Create(true,area.Local,family));}
    [Fact]
    public void ReplacedRootAndUnconfirmedExtraDirectoryAreRejected()
    {
        using var area=new Area();var root=area.Populate();var plan=new SettingsResetPlan(area.Scope,SettingsResetDirectories.Capture(area.Scope,CancellationToken.None));Directory.Move(root,root+".old");Directory.CreateDirectory(root);
        Assert.Throws<IOException>(()=>SettingsResetDirectories.Validate(plan,CancellationToken.None));Assert.Throws<IOException>(()=>SettingsResetDirectories.Delete(plan.Directories[0]));Assert.Equal("original",File.ReadAllText(Path.Combine(root+".old","keep.dat")));
        var changed=plan with{Directories=Array.AsReadOnly(new[]{new SettingsResetDirectoryProof(area.Local,null)})};Assert.Throws<IOException>(()=>SettingsResetDirectories.Validate(changed,CancellationToken.None));
    }
    [Fact]
    public void CancellationAndSharingFailurePreserveRemainingData()
    {
        using var area=new Area();var root=area.Populate();var file=Path.Combine(root,"keep.dat");using var cancel=new CancellationTokenSource();cancel.Cancel();Assert.Throws<OperationCanceledException>(()=>SettingsResetDirectories.Capture(area.Scope,cancel.Token));
        var proof=Assert.Single(SettingsResetDirectories.Capture(area.Scope,CancellationToken.None));using(var locked=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None)){var error=Assert.Throws<System.ComponentModel.Win32Exception>(()=>SettingsResetDirectories.Delete(proof));Assert.Contains(error.NativeErrorCode,new[]{32,33});}Assert.Equal("original",File.ReadAllText(file));
    }
    [Fact]
    public void ReadOnlyCacheFileCanBeRemovedWithItsModeDirectory()
    {
        using var area=new Area();var root=area.Populate();var file=Path.Combine(root,"keep.dat");File.SetAttributes(file,FileAttributes.ReadOnly);
        try{var proof=Assert.Single(SettingsResetDirectories.Capture(area.Scope,CancellationToken.None));SettingsResetDirectories.Delete(proof);Assert.False(Directory.Exists(root));}
        finally{if(File.Exists(file))File.SetAttributes(file,FileAttributes.Normal);}
    }
    [Fact]
    public void LinkAddedAfterConfirmationIsRejectedWithoutTraversingTarget()
    {
        using var area=new Area();var root=area.Populate();var outside=Path.Combine(area.Local,"outside");Directory.CreateDirectory(outside);File.WriteAllText(Path.Combine(outside,"keep.dat"),"outside");
        var proof=Assert.Single(SettingsResetDirectories.Capture(area.Scope,CancellationToken.None));var link=Path.Combine(root,"link");Directory.CreateSymbolicLink(link,outside);
        try{Assert.Throws<IOException>(()=>SettingsResetDirectories.Capture(area.Scope,CancellationToken.None));Assert.Throws<IOException>(()=>SettingsResetDirectories.Delete(proof));Assert.Equal("outside",File.ReadAllText(Path.Combine(outside,"keep.dat")));Assert.True(Directory.Exists(link));}finally{Directory.Delete(link);}
    }
    [Fact]
    public void OverlapRequiresPathBoundaryAndIncludesAncestorRoots()
    {using var area=new Area();var root=area.Scope.Directories[0];Assert.True(SettingsResetScope.Overlaps(root,Path.GetDirectoryName(root)!));Assert.True(SettingsResetScope.Overlaps(root,Path.GetPathRoot(root)!));Assert.True(SettingsResetScope.Overlaps(root,Path.Combine(root,"child")));Assert.False(SettingsResetScope.Overlaps(root,root+"-other"));}
}
