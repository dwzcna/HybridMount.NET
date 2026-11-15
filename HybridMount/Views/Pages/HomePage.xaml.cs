using HybridMount.UI;
using HybridMount.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
namespace HybridMount.Views.Pages;

/// <summary>首页只有布局和导航适配；页面／窗口不可见时停止UI发布，应用级统计继续。</summary>
public sealed partial class HomePage : Page, IDisposable
{
    private IDisposable? _localization;
    private readonly PageScrollBookmark _viewport = new();
    private AppWindow? _window;
    private bool _entered, _loaded, _disposed;
    public HomeViewModel ViewModel { get; } = new(App.Version, App.Current.Navigation, App.Current.TaskOverview,
        App.Current.TaskFocus, App.Current.UIDispatcher);
    public HomePage()
    {
        InitializeComponent();
        _localization = UserInterface.ObserveLanguage(this, () => { ViewModel.RefreshLanguage(); Bindings.Update(); });
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += PageLoaded; Unloaded += PageUnloaded;
    }
    private void PageLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed) return;
        _loaded = true; _viewport.Restore(HomeScroll);
        _window = App.Current.MainWindow?.AppWindow;
        if (_window is not null) _window.Changed += WindowChanged;
        RefreshVisibility(); ReflowCards();
    }
    private void PageUnloaded(object sender, RoutedEventArgs args)
    {
        _loaded = false;
        if (_window is not null) _window.Changed -= WindowChanged;
        _window = null; RefreshVisibility();
    }
    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    { if (args.DidVisibilityChange) RefreshVisibility(); }
    private void RefreshVisibility() => ViewModel.SetVisible(_entered && _loaded && (_window?.IsVisible ?? true));
    protected override void OnNavigatedTo(NavigationEventArgs args)
    { base.OnNavigatedTo(args); _entered = true; _viewport.Enter(HomeScroll); RefreshVisibility(); }
    protected override void OnNavigatedFrom(NavigationEventArgs args)
    { _entered = false; RefreshVisibility(); _viewport.Leave(HomeScroll); base.OnNavigatedFrom(args); }
    private void EntryCardsSizeChanged(object sender, SizeChangedEventArgs args) => ReflowCards();
    /// <summary>以实际内容宽度判定640 DIP断点，不依赖窗口物理像素、语言或侧栏状态。</summary>
    private void ReflowCards()
    {
        var narrow = EntryCards.ActualWidth < 640;
        EntryCards.ColumnSpacing = narrow ? 0 : 16;
        EntryCards.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(LogsCard, narrow ? 0 : 1); Grid.SetRow(LogsCard, narrow ? 1 : 0);
        Grid.SetRow(MountCard, narrow ? 2 : 1);
        if (narrow && EntryCards.RowDefinitions.Count == 2) EntryCards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        else if (!narrow && EntryCards.RowDefinitions.Count == 3) EntryCards.RowDefinitions.RemoveAt(2);
    }
    private void OpenMounts(object sender, RoutedEventArgs args) => ViewModel.OpenMounts();
    private void OpenSettings(object sender, RoutedEventArgs args) => ViewModel.OpenSettings();
    private void OpenLogs(object sender, RoutedEventArgs args) => ViewModel.OpenLogs();
    private void OpenTasks(object sender, RoutedEventArgs args) => ViewModel.OpenTasks();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ViewModel.Dispose(); _localization?.Dispose(); _localization = null;
        Loaded -= PageLoaded; Unloaded -= PageUnloaded;
        if (_window is not null) _window.Changed -= WindowChanged;
        _window = null; _viewport.Stop();
    }
}
