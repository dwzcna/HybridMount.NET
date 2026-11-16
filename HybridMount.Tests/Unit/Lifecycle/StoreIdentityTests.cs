using HybridMount.Infrastructure.Settings;
using HybridMount.Infrastructure.Windows;
using HybridMount.Models.Settings;
using HybridMount.BuildTools;

namespace HybridMount.Tests.Unit.Lifecycle;

/// <summary>新包不能转发旧实例或接管旧配置；测试全部使用注入清单，不安装或卸载系统包。</summary>
public sealed class StoreIdentityTests
{
    [Theory]
    [InlineData("dwzcn.HybridMount.NET")]
    [InlineData("DWZCN.HYBRIDMOUNT.NET")]
    public void Previous_package_stops_startup_before_instance_acquisition(string previous)
    {
        Assert.Throws<PackageStartupConflictException>(() => PackageStartupGuard.Validate(true,
            ProductIdentity.PackageName, ProductIdentity.Publisher, () => [previous, ProductIdentity.PackageName]));
    }
    [Fact]
    public void Current_identity_and_unrelated_packages_can_start()
    {
        PackageStartupGuard.Validate(true, ProductIdentity.PackageName, ProductIdentity.Publisher,
            () => [ProductIdentity.PackageName, "Other.Product", ProductIdentity.PreviousPackageName + ".Probe"]);
    }
    [Fact]
    public void Identity_query_failure_does_not_start_or_enumerate_as_empty()
    {
        var reads = 0;
        Assert.Throws<IOException>(() => PackageStartupGuard.Validate(true, "Other.Product", ProductIdentity.Publisher, () => { reads++; return []; }));
        Assert.Throws<IOException>(() => PackageStartupGuard.Validate(true, ProductIdentity.PackageName, "CN=Other", () => { reads++; return []; }));
        Assert.Equal(0, reads);
        Assert.Throws<UnauthorizedAccessException>(() => PackageStartupGuard.Validate(true, ProductIdentity.PackageName,
            ProductIdentity.Publisher, () => throw new UnauthorizedAccessException()));
    }
    [Fact]
    public void Unpackaged_mode_does_not_query_packages_and_keeps_its_own_scope()
    {
        PackageStartupGuard.Validate(false, "", "", () => throw new InvalidOperationException());
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
