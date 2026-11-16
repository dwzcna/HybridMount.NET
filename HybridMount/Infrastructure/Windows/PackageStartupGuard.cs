using HybridMount.Models.Settings;
using System.Security.Principal;

namespace HybridMount.Infrastructure.Windows;

/// <summary>普通启动在任何实例锁、管道转发或挂载服务之前只读识别旧包；查询失败也不启动业务。</summary>
internal static class PackageStartupGuard
{
    internal static void Validate()
    {
        if (!WinRegisterService.IsPackaged()) return;
        var identity = global::Windows.ApplicationModel.Package.Current.Id;
        Validate(true, identity.Name, identity.Publisher, () =>
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("SID unavailable.");
            return new global::Windows.Management.Deployment.PackageManager().FindPackagesForUser(sid).Select(package => package.Id.Name);
        });
    }
    /// <summary>注入只读安装清单以隔离验收；身份不符时不得枚举旧包，更不能取得实例租约。</summary>
    internal static void Validate(bool packaged, string name, string publisher, Func<IEnumerable<string>> installedNames)
    {
        if (!packaged) return;
        if (name != ProductIdentity.PackageName || publisher != ProductIdentity.Publisher)
            throw new IOException("Unexpected package identity.");
        if (ProductIdentity.HasPreviousPackage(installedNames())) throw new PackageStartupConflictException();
    }
}
