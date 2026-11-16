using HybridMount.Models.Settings;
using HybridMount.Infrastructure.Windows;
namespace HybridMount.Infrastructure.Settings;

/// <summary>产品数据与身份；不读取旧包数据、不迁移旧配置，也不接管另一模式挂载。</summary>
public static class ApplicationPaths
{
    private static string DeploymentScopeFor(bool packaged) => packaged ? "Packaged" : "Unpackaged";
    public static string DeploymentScope => DeploymentScopeFor(WinRegisterService.IsPackaged());
    public static string ProductScope => ProductIdentity.Scope(WinRegisterService.IsPackaged());
    public static string RegistryPath => RegistryPathFor(WinRegisterService.IsPackaged());
    public static string RegistryPathFor(bool packaged) => RuntimeModePaths.RegistryPath(packaged);
    public static string MountDefinitionsRegistryPathFor(bool packaged) => RegistryPathFor(packaged) + @"\Mounts\" + DeploymentScopeFor(packaged);
    /// <summary>仅当前产品的物理诊断范围；打包版缩略图使用实际包LocalState。</summary>
    public static string PhysicalDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.PhysicalDirectoryName(WinRegisterService.IsPackaged()), DeploymentScope);
    public static string DataDirectory
    {
        get
        {
#if !HYBRIDMOUNT_HEADLESS_TEST
            if (WinRegisterService.IsPackaged()) return global::Windows.Storage.ApplicationData.Current.LocalFolder.Path;
#endif
            return PhysicalDataDirectory;
        }
    }
    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");
}
