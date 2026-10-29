namespace HybridMount.Services;

/// <summary>集中定位当前产品的数据和日志目录，包内与开发运行分别使用自身目录。</summary>
public static class ApplicationPaths
{
    /// <summary>产品注册表设置根，不读取参考工程或旧命名的设置。</summary>
    public const string RegistryPath = @"Software\HybridMount.NET";
    /// <summary>当前产品可写数据目录。</summary>
    public static string DataDirectory
    {
        get
        {
#if !HYBRIDMOUNT_HEADLESS_TEST
            if (WinRegisterService.IsPackaged()) return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
#endif
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HybridMount.NET");
        }
    }
    /// <summary>日志目录；与远端文件或用户挂载内容分离。</summary>
    public static string LogsDirectory => Path.Combine(DataDirectory, "Logs");
}
