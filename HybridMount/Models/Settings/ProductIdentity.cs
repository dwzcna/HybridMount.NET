namespace HybridMount.Models.Settings;

/// <summary>当前产品的精确包身份和模式范围；旧包不共享实例、根前缀或配置。</summary>
internal static class ProductIdentity
{
    internal const string PackageName = "RyleD.HybridMount.NET";
    internal const string Publisher = "CN=B14891C7-3483-4FD8-AC49-EA066227CC77";
    internal const string PublisherDisplayName = "RyleD";
    internal const string PreviousPackageName = "dwzcn.HybridMount.NET";
    internal static string Scope(bool packaged) => packaged ? PackageName + ".Packaged" : "HybridMount.NET.Unpackaged";
    internal static string PhysicalDirectoryName(bool packaged) => packaged ? PackageName : "HybridMount.NET";
    /// <summary>名称来自操作系统的安装清单；仅识别冲突，不读取、迁移或删除旧包数据。</summary>
    internal static bool HasPreviousPackage(IEnumerable<string> installedNames) =>
        installedNames.Any(name => string.Equals(name, PreviousPackageName, StringComparison.OrdinalIgnoreCase));
}
