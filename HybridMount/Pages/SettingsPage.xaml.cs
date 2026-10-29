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
        VersionText.Text = $"HybridMount.NET {App.Version}";
        Loaded += async (_, _) => { ViewModel.Appearance.Reload(); await ViewModel.RefreshStartupAsync(); };
    }
    private async void OpenStartupSettings(object sender, RoutedEventArgs args)
    {
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps")); }
        catch (Exception error) { App.Current.Logger.Log("启动", error.Message, InfoBarSeverity.Error); }
    }
}
