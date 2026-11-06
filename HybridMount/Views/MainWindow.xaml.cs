using HybridMount.Helpers;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace HybridMount.Views;

/// <summary>从 EPaperKit 窗口布局迁移的主导航，负责外观应用及轻量页面缓存。</summary>
public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;
    private bool _syncingSelection;
    private AppearanceBackdrop? _appliedBackdrop;
    /// <summary>原生窗口句柄，用于系统文件选择器。</summary>
    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    /// <summary>创建标题栏、导航和符合当前偏好的背景。</summary>
    public MainWindow()
    {
        InitializeComponent();
        _navigation=new NavigationService(ContentFrame,error=>App.Current.Logger.Queue("导航","Error","页面导航失败："+error.GetType().Name));
        App.Current.Navigation.Bind(_navigation);_navigation.Navigated+=SynchronizeSelection;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1080, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HybridMount.ico"));
        AppTitleBar.PaneToggleRequested += (_, _) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
        Navigation.SelectionChanged += (_, args) =>
        {
            if(_syncingSelection)return;
            if(args.SelectedItem is NavigationViewItem item&&item.Tag is string value&&Uri.TryCreate(value,UriKind.Absolute,out var target))
                if(!Navigate(target)&&_navigation.Current is {} current)SynchronizeSelection(current);
        };
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        ApplyAppearance();
        Navigate(ApplicationRoutes.Home);RefreshUploadIssueNotifications();
    }
    /// <summary>内部 URI 统一入口，实际导航由服务经 Frame.Navigate 执行。</summary>
    public bool Navigate(Uri target)=>_navigation.Navigate(target);
    private void SynchronizeSelection(Uri target)
    {
        if(_syncingSelection)return;_syncingSelection=true;
        try
        {
            var selected=Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(item=>Equals(item.Tag,target.OriginalString));
            if(selected is not null&&!ReferenceEquals(Navigation.SelectedItem,selected))Navigation.SelectedItem=selected;
        }
        finally{_syncingSelection=false;}
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
    /// <summary>每挂载一条可关闭的应用内提示；关闭只解除提示，点击使用独立定位服务。</summary>
    public void RefreshUploadIssueNotifications()
    {
        if(App.Current.IsExiting)return;IssueNotifications.Children.Clear();
        foreach(var notice in App.Current.IssueNotifications.Pending)
        {
            var button=new Button{Content="查看同步问题"};button.Click+=(_,_)=>App.Current.TaskFocus.ShowIssues(notice.MountId);
            var bar=new InfoBar{Title=notice.MountName,Message=$"{notice.Count} 个上传问题需要处理",Severity=InfoBarSeverity.Warning,IsOpen=true,IsClosable=true,ActionButton=button};
            bar.Closed+=(_,_)=>App.Current.IssueNotifications.Dismiss(notice.MountId);IssueNotifications.Children.Add(bar);
        }
    }
    /// <summary>只在真正退出停止 Frame 缓存页；一级导航不会释放页面 VM。</summary>
    public void StopPages()=>_navigation.StopPages();
    public async Task ReleaseResourcesAsync()
    {
        try{await _navigation.DisposeAsync();}
        finally{_navigation.Navigated-=SynchronizeSelection;App.Current.Navigation.Unbind(_navigation);SystemBackdrop=null;}
    }
}
