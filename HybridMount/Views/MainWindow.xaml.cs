using HybridMount.Helpers;
using HybridMount.Pages;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace HybridMount.Views;

/// <summary>从 EPaperKit 窗口布局迁移的主导航，负责外观应用及轻量页面缓存。</summary>
public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Page> _pages = [];
    private AppearanceBackdrop? _appliedBackdrop;
    /// <summary>原生窗口句柄，用于系统文件选择器。</summary>
    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    /// <summary>创建标题栏、导航和符合当前偏好的背景。</summary>
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1080, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HybridMount.ico"));
        AppTitleBar.PaneToggleRequested += (_, _) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
        Navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string key) Navigate(key);
        };
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        ApplyAppearance();
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }
    /// <summary>切换页面；缓存限三个页面，退出时解除日志订阅。</summary>
    public void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch { "Logs" => new LogsPage(), "Settings" => new SettingsPage(), _ => new HomePage() };
            _pages[key] = page;
        }
        ContentFrame.Content = page;
        var selected = Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(item => Equals(item.Tag, key));
        if (selected is not null && !ReferenceEquals(Navigation.SelectedItem, selected)) Navigation.SelectedItem = selected;
    }
    /// <summary>立即应用已经成功保存的主题和材质；回退不覆盖用户选择。</summary>
    public void ApplyAppearance()
    {
        var preference = App.Current.Appearance.Current;
        RootGrid.RequestedTheme = preference.Theme switch
        {
            AppearanceTheme.Light => ElementTheme.Light,
            AppearanceTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        var fallback = false;
        if (_appliedBackdrop != preference.Backdrop)
        {
            SystemBackdrop = null;
            try
            {
                SystemBackdrop = preference.Backdrop switch
                {
                    AppearanceBackdrop.MicaBase => new MicaBackdrop { Kind = MicaKind.Base },
                    AppearanceBackdrop.MicaAltBaseAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
                    AppearanceBackdrop.AcrylicBase => new DesktopAcrylicBackdrop(),
                    AppearanceBackdrop.AcrylicThin when DesktopAcrylicController.IsSupported() => new ThinAcrylicBackdrop(),
                    _ => null
                };
                fallback = preference.Backdrop != AppearanceBackdrop.None && SystemBackdrop is null;
            }
            catch (Exception error)
            {
                SystemBackdrop = null;
                fallback = true;
                App.Current.Logger.Queue("外观", "Warning", $"材质不可用，使用系统背景：{error.Message}");
            }
            _appliedBackdrop = preference.Backdrop;
        }
        RootGrid.Style = preference.Backdrop == AppearanceBackdrop.None || fallback || SystemBackdrop is null
            ? (Style)RootGrid.Resources["NoneBackdropStyle"] : null;
        UpdateCaptionTheme();
    }
    private void UpdateCaptionTheme() => AppWindow.TitleBar.ButtonForegroundColor = RootGrid.ActualTheme == ElementTheme.Dark
        ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
    /// <summary>在主窗口显示日志通知，不影响已经持久化的记录。</summary>
    public void ShowNotification(LogNotificationEventArgs args)
    {
        NotificationBar.Title = args.Source;
        NotificationBar.Message = args.Message;
        NotificationBar.Severity = args.Severity;
        NotificationBar.IsOpen = true;
    }
    /// <summary>真正退出时解除页面订阅和合成背景。</summary>
    public void ReleaseResources()
    {
        foreach (var page in _pages.Values) if (page is IDisposable disposable) disposable.Dispose();
        _pages.Clear();
        SystemBackdrop = null;
    }
}
