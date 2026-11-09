namespace HybridMount.ViewModels;

/// <summary>首页唯一展示和导航命令状态；通过接口请求内部路由，不访问窗口或创建页面。</summary>
public sealed class HomeViewModel(string version,INavigationService navigation)
{
    public string VersionText=>"版本 "+version;
    public bool OpenMounts()=>navigation.Navigate(ApplicationPage.Mounts);
    public bool OpenSettings()=>navigation.Navigate(ApplicationPage.Settings);
    public bool OpenLogs()=>navigation.Navigate(ApplicationPage.Logs);
}
