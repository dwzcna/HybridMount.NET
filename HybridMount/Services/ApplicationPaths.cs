namespace HybridMount.Services;

/// <summary>实验分支的数据与身份；不读取或接管正式安装及另一部署模式的配置。</summary>
public static class ApplicationPaths
{
    private static string DeploymentScopeFor(bool packaged) => packaged ? "Packaged" : "Unpackaged";
    public static string DeploymentScope => DeploymentScopeFor(WinRegisterService.IsPackaged());
    public static string ProductScope => "HybridMount.NativePreview." + DeploymentScope;
    public static string RegistryPath => RegistryPathFor(WinRegisterService.IsPackaged());
    public static string RegistryPathFor(bool packaged) => @"Software\HybridMount.NET\NativePreview\" + DeploymentScopeFor(packaged);
    public static string MountDefinitionsRegistryPathFor(bool packaged) => RegistryPathFor(packaged) + @"\Mounts\" + DeploymentScopeFor(packaged);
    /// <summary>物理缓存根供纯原生宿主直读；包清单仅为缩略图子树排除虚拟化。</summary>
    public static string PhysicalDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HybridMount.NET", "NativePreview", DeploymentScope);
    public static string DataDirectory
    {
        get
        {
#if !HYBRIDMOUNT_HEADLESS_TEST
            if (WinRegisterService.IsPackaged()) return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
#endif
            return PhysicalDataDirectory;
        }
    }
    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");
}
