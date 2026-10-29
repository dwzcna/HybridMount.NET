using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Pages;

/// <summary>首版首页，只展示已实现的基础功能入口。</summary>
public sealed partial class HomePage : Page
{
    /// <summary>创建首页并显示实际程序集版本。</summary>
    public HomePage() { InitializeComponent(); VersionText.Text = $"版本 {App.Version}"; }
    private void OpenMounts(object sender, RoutedEventArgs args) => App.Current.MainWindow?.Navigate("Mounts");
    private void OpenSettings(object sender, RoutedEventArgs args) => App.Current.MainWindow?.Navigate("Settings");
    private void OpenLogs(object sender, RoutedEventArgs args) => App.Current.MainWindow?.Navigate("Logs");
}
