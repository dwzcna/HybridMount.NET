using HybridMount.Helpers.UI;
using Microsoft.UI.Xaml.Navigation;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Pages;

/// <summary>首版首页，只展示已实现的基础功能入口。</summary>
public sealed partial class HomePage : Page,IDisposable
{
    private IDisposable? _localization;
    /// <summary>创建首页并显示实际程序集版本。</summary>
    public HomeViewModel ViewModel {get;}=new(App.Version,App.Current.Navigation);
    private readonly PageScrollBookmark _viewport=new();
    public HomePage(){InitializeComponent();_localization=global::HybridMount.Helpers.UI.Ui.ObserveLanguage(this,()=>Bindings.Update());NavigationCacheMode=NavigationCacheMode.Required;Loaded+=PageLoaded;}
    private void PageLoaded(object sender,RoutedEventArgs args)=>_viewport.Restore(HomeScroll);
    protected override void OnNavigatedTo(NavigationEventArgs args){base.OnNavigatedTo(args);_viewport.Enter(HomeScroll);}
    protected override void OnNavigatedFrom(NavigationEventArgs args){_viewport.Leave(HomeScroll);base.OnNavigatedFrom(args);}
    public void Dispose(){_localization?.Dispose();_localization=null;Loaded-=PageLoaded;_viewport.Stop();}
    private void OpenMounts(object sender, RoutedEventArgs args) => ViewModel.OpenMounts();
    private void OpenSettings(object sender, RoutedEventArgs args) => ViewModel.OpenSettings();
    private void OpenLogs(object sender, RoutedEventArgs args) => ViewModel.OpenLogs();
}
