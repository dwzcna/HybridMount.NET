using HybridMount.Commons.UI;
using HybridMount.Models;
using HybridMount.UI.Localization;
namespace HybridMount.ViewModels;

/// <summary>首页唯一展示和导航命令状态；通过接口请求内部路由，不访问窗口或创建页面。</summary>
public sealed class HomeViewModel(string version, INavigationService navigation)
{
    public string VersionText => string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Version", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, version);
    public bool OpenMounts() => navigation.Navigate(ApplicationPage.Mounts);
    public bool OpenSettings() => navigation.Navigate(ApplicationPage.Settings);
    public bool OpenLogs() => navigation.Navigate(ApplicationPage.Logs);
}
