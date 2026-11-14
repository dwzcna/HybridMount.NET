namespace HybridMount.Services;

/// <summary>产品数据与身份；不读取旧包数据、不迁移旧配置，也不接管另一模式挂载。</summary>
public static class ApplicationPaths
{
    private static string DeploymentScopeFor(bool packaged) => packaged ? "Packaged" : "Unpackaged";
    public static string DeploymentScope => DeploymentScopeFor(WinRegisterService.IsPackaged());
    public static string ProductScope => "HybridMount.NET." + DeploymentScope;
    public static string RegistryPath => RegistryPathFor(WinRegisterService.IsPackaged());
    public static string RegistryPathFor(bool packaged) => @"Software\HybridMount.NET\" + DeploymentScopeFor(packaged);
    public static string MountDefinitionsRegistryPathFor(bool packaged) => RegistryPathFor(packaged) + @"\Mounts\" + DeploymentScopeFor(packaged);
    /// <summary>物理缓存根供纯原生宿主直读；包清单仅为缩略图子树排除虚拟化。</summary>
    public static string PhysicalDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HybridMount.NET", DeploymentScope);
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
