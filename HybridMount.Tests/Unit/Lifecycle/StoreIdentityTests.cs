using HybridMount.Infrastructure.Settings;
using HybridMount.Models.Settings;
using HybridMount.BuildTools;

namespace HybridMount.Tests.Unit.Lifecycle;

/// <summary>新包不能转发旧实例或接管旧配置；不扫描安装清单，不安装或卸载系统包。</summary>
public sealed class StoreIdentityTests
{
    [Fact]
    public void Runtime_modes_keep_independent_scopes_without_package_enumeration()
    {
        Assert.Equal("HybridMount.NET.Unpackaged", ProductIdentity.Scope(false));
        Assert.Equal("RyleD.HybridMount.NET.Packaged", ProductIdentity.Scope(true));
        Assert.NotEqual("HybridMount.NET.Packaged", ProductIdentity.Scope(true));
        Assert.Equal(@"Software\RyleD.HybridMount.NET\Packaged", ApplicationPaths.RegistryPathFor(true));
        Assert.Equal(@"Software\HybridMount.NET\Unpackaged", ApplicationPaths.RegistryPathFor(false));
        Assert.NotEqual(ApplicationPaths.RegistryPathFor(true), ApplicationPaths.RegistryPathFor(false));
    }
    [Fact]
    public void Reset_scope_excludes_previous_product_data()
    {
        var area = Path.Combine(Path.GetTempPath(), "HybridMount.Identity." + Guid.NewGuid().ToString("N"));
        var scope = SettingsResetScope.Create(true, area, "ActualFamilyFromSystem_abc");
        Assert.DoesNotContain(Path.Combine(area, "HybridMount.NET", "Packaged"), scope.Directories);
        Assert.Contains(Path.Combine(area, "Packages", "ActualFamilyFromSystem_abc", "LocalState"), scope.Directories);
        Assert.All(scope.Directories, path => Assert.StartsWith(area + Path.DirectorySeparatorChar, path));
    }
    [Theory]
    [InlineData("Store", "Sideload")]
    [InlineData("RyleD.HybridMount.NET", "dwzcn.HybridMount.NET")]
    public void Distribution_and_identity_invalidate_build_cache(string first, string second)
    {
        Assert.NotEqual(BuildFingerprint.Text(first), BuildFingerprint.Text(second));
    }
}
