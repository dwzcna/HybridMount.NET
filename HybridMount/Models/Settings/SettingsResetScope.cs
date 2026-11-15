using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HybridMount.Models.Settings;

/// <summary>只由当前运行模式生成的清理范围；路径不可编辑，不包含共享父目录、另一模式或程序目录。</summary>
internal sealed class SettingsResetScope
{
    internal bool Packaged {get;}
    internal string Mode=>Packaged?"Packaged":"Unpackaged";
    internal string RegistryPath=>RuntimeModePaths.RegistryPath(Packaged);
    internal string ProductScope=>"HybridMount.NET."+Mode;
    internal IReadOnlyList<string> Directories {get;}
    internal string MountDataDirectory {get;}
    private SettingsResetScope(bool packaged,string[] directories,string mountDirectory){Packaged=packaged;Directories=Array.AsReadOnly(directories);MountDataDirectory=mountDirectory;}
    /// <summary>由系统LocalAppData和已核验包族名推导白名单；测试可传隔离LocalAppData，不接收任意删除目录。</summary>
    internal static SettingsResetScope Create(bool packaged,string localAppData,string? packageFamily=null)
    {
        if(!Path.IsPathFullyQualified(localAppData)||localAppData.StartsWith(@"\\",StringComparison.Ordinal))throw new IOException(global::HybridMount.Models.Localization.ResourceMessage.Create("CannotVerifyThePackageDataFolder").Render());
        var physical=Path.Combine(Path.GetFullPath(localAppData),"HybridMount.NET",packaged?"Packaged":"Unpackaged");
        if(!packaged)return new(false,[physical],Path.Combine(physical,"Mounting"));
        if(string.IsNullOrWhiteSpace(packageFamily)||packageFamily is "." or ".."||packageFamily.IndexOfAny(Path.GetInvalidFileNameChars())>=0||packageFamily.Contains('/')||packageFamily.Contains('\\'))throw new IOException(global::HybridMount.Models.Localization.ResourceMessage.Create("CannotVerifyThePackageDataFolder").Render());
        var package=Path.Combine(Path.GetFullPath(localAppData),"Packages",packageFamily);
        // 删除本应用的数据目录本身；包族父目录及Windows维护的包运行状态不属于应用设置。
        return new(true,[physical,Path.Combine(package,"LocalState"),Path.Combine(package,"LocalCache"),Path.Combine(package,"RoamingState"),Path.Combine(package,"TempState")],Path.Combine(package,"LocalState","Mounting"));
    }
    internal bool SameAs(SettingsResetScope other)=>Packaged==other.Packaged&&Directories.SequenceEqual(other.Directories,StringComparer.OrdinalIgnoreCase);
    internal static bool Overlaps(string first,string second)
    {
        first=Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));second=Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(first,second,StringComparison.OrdinalIgnoreCase)||first.StartsWith(Path.EndsInDirectorySeparator(second)?second:second+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||second.StartsWith(Path.EndsInDirectorySeparator(first)?first:first+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
}
internal sealed record SettingsResetDirectoryProof(string Path,string? Identity);
internal sealed record SettingsResetPlan(SettingsResetScope Scope,IReadOnlyList<SettingsResetDirectoryProof> Directories);
/// <summary>预检的正常阻止结果没有删除授权；真实查询失败仍由异常报告。</summary>
internal sealed record SettingsResetInspection(SettingsResetPlan? Plan,string? BlockedReason=null);
/// <summary>先检查注册阻止条件，再捕获删除范围；被阻止时不扫描应用数据。</summary>
internal static class SettingsResetPreflight
{
    internal static SettingsResetInspection Inspect(SettingsResetScope scope,Func<string?> blocker,
        Func<IReadOnlyList<SettingsResetDirectoryProof>> capture,Action verifyRecovery)
    {
        if(blocker() is {} reason)return new(null,reason);
        var proofs=capture();verifyRecovery();return new(new(scope,proofs));
    }
}
