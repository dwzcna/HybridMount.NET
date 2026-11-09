using HybridMount.Helpers.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using global::Windows.Graphics;

namespace HybridMount.Views;

/// <summary>主窗口连接标题栏、侧栏与 Frame，负责外观和页面导航状态。</summary>
public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;
    public MainWindowViewModel ViewModel {get;}
    private AppearanceBackdrop? _appliedBackdrop;
    /// <summary>原生窗口句柄，用于系统文件选择器。</summary>
    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    /// <summary>创建标题栏、导航和符合当前偏好的背景。</summary>
    public MainWindow()
    {
        ViewModel=new(App.Current.Navigation,App.Current.UiDispatcher,App.Current.IssueNotifications,App.Current.TaskFocus,error=>App.Current.Logger.QueueException("主窗口","Warning","窗口状态更新失败",error));
        InitializeComponent();
        _navigation=App.Current.Navigation;
        _navigation.RegisterFrame(ContentFrame,error=>App.Current.Logger.Queue("导航","Error","页面导航失败："+error.GetType().Name));
        ViewModel.Navigated+=SynchronizeSelection;ViewModel.NoticesChanged+=RefreshUploadIssueNotifications;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1080, 760));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HybridMount.ico"));
        AppTitleBar.PaneToggleRequested += (_, _) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;
        AppTitleBar.BackRequested += (_, _) => TryGoBack();
        Navigation.ItemInvoked += (_, args) =>
        {
            if(args.InvokedItemContainer is NavigationViewItem {Tag:ApplicationPage page}&&!ViewModel.Navigate(page))
                DispatcherQueue.TryEnqueue(()=>{if(ViewModel.CurrentPage is {} current)SynchronizeSelection(current);});
        };
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        ApplyAppearance();
        ViewModel.Navigate(ApplicationPage.Home);RefreshUploadIssueNotifications();
    }
    /// <summary>成功导航后更新侧栏和标题栏；程序性选中不会触发 ItemInvoked。</summary>
    private void SynchronizeSelection(ApplicationPage page)
    {
        var selected=Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(item=>item.Tag is ApplicationPage target&&target==page);
        if(selected is not null&&!ReferenceEquals(Navigation.SelectedItem,selected))Navigation.SelectedItem=selected;
        AppTitleBar.IsBackButtonEnabled=ViewModel.CanGoBack;
    }
    /// <summary>标题栏与快捷键共用返回入口；对话框或其他弹层打开时保持底层页面。</summary>
    private bool TryGoBack()=>RootGrid.XamlRoot is {} root&&!VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(popup=>popup.IsOpen)&&ViewModel.TryGoBack();
    private void OnBackAcceleratorInvoked(KeyboardAccelerator sender,KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled=true;TryGoBack();
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
                App.Current.Logger.QueueException("外观","Warning","材质不可用，使用系统背景",error);
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
        ViewModel.ShowNotification(args.Source,args.Message);
        NotificationBar.Severity = args.Severity;
    }
    /// <summary>每挂载一条可关闭的应用内提示；关闭只解除提示，点击使用独立定位服务。</summary>
    public void RefreshUploadIssueNotifications()
    {
        if(App.Current.IsExiting)return;IssueNotifications.Children.Clear();
        foreach(var notice in ViewModel.IssueNotices)
        {
            var button=new Button{Content="查看同步问题"};button.Click+=(_,_)=>ViewModel.OpenIssues(notice.MountId);
            var summary=notice.Count>0?$"{notice.Count} 个上传问题需要处理":"";
            var message=notice.Message is {} warning?(summary.Length>0?summary+"\n":"")+warning:summary;
            var bar=new InfoBar{Title=notice.MountName,Message=message,Severity=InfoBarSeverity.Warning,IsOpen=true,IsClosable=true,ActionButton=notice.Count>0?button:null};
            bar.Closed+=(_,_)=>ViewModel.DismissIssues(notice.MountId);IssueNotifications.Children.Add(bar);
        }
    }
    /// <summary>只在真正退出停止 Frame 缓存页；一级导航不会释放页面 VM。</summary>
    public void StopPages()=>_navigation.StopPages();
    public async Task ReleaseResourcesAsync()
    {
        ViewModel.Navigated-=SynchronizeSelection;ViewModel.NoticesChanged-=RefreshUploadIssueNotifications;ViewModel.Dispose();
        try{await _navigation.DisposeAsync();}
        finally{SystemBackdrop=null;}
    }
}
