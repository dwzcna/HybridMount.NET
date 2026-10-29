using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Pages;

/// <summary>外观、托盘和登录启动设置页面。</summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>绑定应用范围的偏好，页面切换不产生额外状态源。</summary>
    public SettingsViewModel ViewModel => App.Current.Settings;
    /// <summary>初始化后进入页面时核对 Windows 实际状态。</summary>
    public SettingsPage()
    {
        InitializeComponent();
        CacheLimitField.Value=App.Current.Mounts.CacheLimitBytes/(double)(1024L*1024*1024);
        VersionText.Text = $"HybridMount.NET {App.Version}";
        Loaded += async (_, _) => { ViewModel.Appearance.Reload(); await ViewModel.RefreshStartupAsync(); };
    }
    private void SaveCache(object sender,RoutedEventArgs args)
    {
        try
        {
            var value=CacheLimitField.Value;if(double.IsNaN(value)||value<0)throw new ArgumentException("请输入有效缓存上限。");
            App.Current.Mounts.CacheLimitBytes=checked((long)(value*1024*1024*1024));CacheStatus.Text="缓存上限已保存。";
        }
        catch(Exception error){CacheStatus.Text=error.Message;}
    }
    private async void TrimCache(object sender,RoutedEventArgs args)
    {
        var button=(Button)sender;button.IsEnabled=false;
        try { var freed=await Task.Run(()=>App.Current.Mounts.EnforceCacheLimitAsync());CacheStatus.Text=$"本次回收 {freed/(1024d*1024):F1} MiB，当前占用约 {App.Current.Mounts.CacheUsageBytes/(1024d*1024):F1} MiB。"; }
        catch(Exception error){CacheStatus.Text=error.Message;}
        finally{button.IsEnabled=true;}
    }
    private async void OpenStartupSettings(object sender, RoutedEventArgs args)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps")); }
        catch (Exception error) { App.Current.Logger.Log("启动", error.Message, InfoBarSeverity.Error); }
    }
}
