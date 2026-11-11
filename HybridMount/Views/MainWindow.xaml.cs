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
    private IDisposable? _localization;private LanguageWindowObserver? _languageWindow;
    private readonly NavigationService _navigation;
    public MainWindowViewModel ViewModel {get;}
    private AppearanceBackdrop? _appliedBackdrop;
    private ResourceDictionary? _compactResources;private bool _released;
    private Microsoft.UI.System.ThemeSettings? _themeSettings;
    private readonly List<PageNotificationRegistration> _pageMessages=[];
    /// <summary>原生窗口句柄，用于系统文件选择器。</summary>
    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    /// <summary>创建标题栏、导航和符合当前偏好的背景。</summary>
    public MainWindow()
    {
        ViewModel=new(App.Current.Navigation,App.Current.UiDispatcher,App.Current.IssueNotifications,App.Current.TaskFocus,error=>App.Current.Logger.QueueException("主窗口","Warning","窗口状态更新失败",error));
        InitializeComponent();NotificationBar.Closed+=MainNotificationClosed;_localization=global::HybridMount.Helpers.UI.Ui.ObserveLanguage(RootGrid,()=>Bindings.Update());
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
        App.Current.Preferences.LayoutChanged+=ApplyDensity;ObserveThemeSettings();ApplyDensity();
        ApplyAppearance();
        ViewModel.Navigate(ApplicationPage.Home);RefreshUploadIssueNotifications();_languageWindow=new(this,App.Current.Languages);
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
    /// <summary>高对比度交由Windows决定标题按钮颜色；普通主题保留明暗文字色。</summary>
    private void UpdateCaptionTheme()=>AppWindow.TitleBar.ButtonForegroundColor=_themeSettings?.HighContrast!=false?null:RootGrid.ActualTheme==ElementTheme.Dark?Microsoft.UI.Colors.White:Microsoft.UI.Colors.Black;
    /// <summary>使用绑定桌面窗口的主题API；系统监听不可用只记录警告，标题按钮保留系统颜色且不阻断启动。</summary>
    private void ObserveThemeSettings()
    {
        try
        {
            var settings=Microsoft.UI.System.ThemeSettings.CreateForWindowId(AppWindow.Id);
            settings.Changed+=ThemeSettingsChanged;_themeSettings=settings;
        }
        catch(Exception error){App.Current.Logger.QueueException("外观","Warning","系统主题监听不可用，标题按钮使用系统颜色",error);}
    }
    /// <summary>系统回调只调度UI更新，退出后拒绝迟到事件。</summary>
    private void ThemeSettingsChanged(Microsoft.UI.System.ThemeSettings sender,object args)=>DispatcherQueue.TryEnqueue(()=>{if(!_released)UpdateCaptionTheme();});
    /// <summary>切换系统提供的紧凑资源，无控件重建；页面自身仍通过Loaded订阅刷新边距。</summary>
    private void ApplyDensity()
    {
        if(_released)return;if(!DispatcherQueue.HasThreadAccess){DispatcherQueue.TryEnqueue(ApplyDensity);return;}
        if(App.Current.Preferences.CompactLayout&&_compactResources is null){_compactResources=new ResourceDictionary{Source=new Uri("ms-appx:///Microsoft.UI.Xaml/DensityStyles/Compact.xaml")};RootGrid.Resources.MergedDictionaries.Add(_compactResources);}
        else if(!App.Current.Preferences.CompactLayout&&_compactResources is {} previous){RootGrid.Resources.MergedDictionaries.Remove(previous);_compactResources=null;}
    }
    /// <summary>无打开消息时折叠整个提醒容器，避免空白区域遮挡页面或拦截滚轮。</summary>
    private void RefreshNotificationHost()=>NotificationHost.Visibility=NotificationBar.IsOpen||PageNotifications.Children.OfType<InfoBar>().Any(bar=>bar.IsOpen)||IssueNotifications.Children.OfType<InfoBar>().Any(bar=>bar.IsOpen)?Visibility.Visible:Visibility.Collapsed;
    private void MainNotificationClosed(InfoBar sender,InfoBarClosedEventArgs args)=>RefreshNotificationHost();
    /// <summary>UI线程注册一个独立页面消息；关闭仅更新该渠道，不取消业务，不为导航重新发布。</summary>
    internal IDisposable? RegisterPageMessage(PageStatusMessage message)
    {
        if(_released||App.Current.IsExiting)return null;
        var registration=new PageNotificationRegistration(this,message);_pageMessages.Add(registration);return registration;
    }
    /// <summary>持有一次可视订阅；释放、卸载及应用退出均幂等移除，拒绝迟到派发。</summary>
    private sealed class PageNotificationRegistration:IDisposable
    {
        private readonly MainWindow _owner;private readonly PageStatusMessage _source;private readonly InfoBar _bar;private bool _disposed,_refreshing;
        internal PageNotificationRegistration(MainWindow owner,PageStatusMessage source)
        {
            _owner=owner;_source=source;_bar=new InfoBar{Style=(Style)Application.Current.Resources["InstantNotificationStyle"]};
            source.PropertyChanged+=Changed;_bar.Closed+=Closed;owner.PageNotifications.Children.Add(_bar);Refresh();
        }
        private void Changed(object? sender,System.ComponentModel.PropertyChangedEventArgs args)
        {if(_disposed)return;if(_owner.DispatcherQueue.HasThreadAccess)Refresh();else _owner.DispatcherQueue.TryEnqueue(Refresh);}
        private void Refresh()
        {
            if(_disposed||_owner._released||App.Current.IsExiting)return;
            _refreshing=true;try{_bar.Message=_source.Text;_bar.Severity=PageMessagePresentation.Severity(_source.Severity);_bar.IsOpen=_source.IsOpen;_bar.Visibility=_source.IsOpen?Visibility.Visible:Visibility.Collapsed;}finally{_refreshing=false;_owner.RefreshNotificationHost();}
        }
        private void Closed(InfoBar sender,InfoBarClosedEventArgs args){if(!_disposed&&!_refreshing){_source.IsOpen=false;Refresh();}}
        public void Dispose(){if(_disposed)return;_disposed=true;_source.PropertyChanged-=Changed;_bar.Closed-=Closed;_owner.PageNotifications.Children.Remove(_bar);_owner._pageMessages.Remove(this);_owner.RefreshNotificationHost();}
    }
    /// <summary>在主窗口显示日志通知，不影响已经持久化的记录。</summary>
    public void ShowNotification(LogNotificationEventArgs args)
    {
        ViewModel.ShowNotification(args.Source,args.Message);
        NotificationBar.Severity = args.Severity;RefreshNotificationHost();
    }
    /// <summary>每挂载一条可关闭的应用内提示；关闭只解除提示，点击使用独立定位服务。</summary>
    public void RefreshUploadIssueNotifications()
    {
        if(App.Current.IsExiting)return;IssueNotifications.Children.Clear();
        foreach(var notice in ViewModel.IssueNotices)
        {
            var button=global::HybridMount.Helpers.UI.Ui.Configure(new Button{},("Content",global::HybridMount.Services.Localization.UiText.Message("Text_7ED1A2EB244B")));button.Click+=(_,_)=>ViewModel.OpenIssues(notice.MountId);
            var summary=notice.Count>0?global::HybridMount.Services.Localization.UiText.Text("Text_DC25DC95ACCE",notice.Count):"";
            var message=notice.Message is {} warning?(summary.Length>0?summary+"\n":"")+warning:summary;
            var bar=new InfoBar{Style=(Style)Application.Current.Resources["InstantNotificationStyle"],Title=notice.MountName,Message=message,Severity=InfoBarSeverity.Warning,IsOpen=true,IsClosable=true,ActionButton=notice.Count>0?button:null};
            global::HybridMount.Helpers.UI.Ui.Bind(bar,"Message",()=>global::HybridMount.Services.Localization.ResourceMessage.Raw((notice.Count>0?global::HybridMount.Services.Localization.UiText.Text("Text_DC25DC95ACCE",notice.Count):"")+(notice.Message is {} detail?(notice.Count>0?"\n":"")+global::HybridMount.Services.Localization.UiText.Legacy(detail):"")));
            bar.Closed+=(_,_)=>{ViewModel.DismissIssues(notice.MountId);RefreshNotificationHost();};IssueNotifications.Children.Add(bar);
        }
        RefreshNotificationHost();
    }
    /// <summary>还原失败发生于写入者全部释放后；仅保留最后一个可关闭InfoBar，不重启服务或重新创建日志。</summary>
    internal async Task ShowSettingsResetFailureAsync(Exception error)
    {
        PageNotifications.Children.Clear();IssueNotifications.Children.Clear();NotificationBar.IsOpen=false;
        var completed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cause=error.InnerException??error;
        var hresult=cause is System.ComponentModel.Win32Exception native?unchecked((int)(0x80070000u|(uint)native.NativeErrorCode)):cause.HResult;
        var bar=new InfoBar{Style=(Style)Application.Current.Resources["InstantNotificationStyle"],Title=global::HybridMount.Services.Localization.UiText.Text("Text_5BBAD56EBA28"),Message=global::HybridMount.Services.Localization.UiText.Text("Text_317016430E4E",error.Message,hresult,cause.Message),Severity=InfoBarSeverity.Error,IsOpen=true};
        void Closed(InfoBar sender,InfoBarClosedEventArgs args)=>completed.TrySetResult();
        void Closing(Microsoft.UI.Windowing.AppWindow sender,Microsoft.UI.Windowing.AppWindowClosingEventArgs args){args.Cancel=true;completed.TrySetResult();}
        bar.Closed+=Closed;AppWindow.Closing+=Closing;PageNotifications.Children.Add(bar);RefreshNotificationHost();AppWindow.Show();Activate();
        try{await completed.Task;}finally{bar.Closed-=Closed;AppWindow.Closing-=Closing;PageNotifications.Children.Remove(bar);}
    }
    /// <summary>只在真正退出停止 Frame 缓存页；一级导航不会释放页面 VM。</summary>
    public void StopPages()=>_navigation.StopPages();
    public async Task ReleaseResourcesAsync()
    {
        if(_released)return;foreach(var registration in _pageMessages.ToArray())registration.Dispose();_released=true;NotificationBar.Closed-=MainNotificationClosed;_localization?.Dispose();_localization=null;_languageWindow?.Dispose();_languageWindow=null;App.Current.Preferences.LayoutChanged-=ApplyDensity;
        if(_themeSettings is {} settings)
        {
            _themeSettings=null;
            try{settings.Changed-=ThemeSettingsChanged;}
            catch(Exception error){App.Current.Logger.QueueException("外观","Warning","系统主题监听解除失败",error);}
        }
        ViewModel.Navigated-=SynchronizeSelection;ViewModel.NoticesChanged-=RefreshUploadIssueNotifications;ViewModel.Dispose();
        try{await _navigation.DisposeAsync();}
        finally{SystemBackdrop=null;}
    }
}
