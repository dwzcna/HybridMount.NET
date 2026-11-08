using HybridMount.Helpers;
using Microsoft.UI.Xaml.Navigation;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Pages;

/// <summary>设置页仅适配控件和系统设置链接，所有偏好／草稿与忙碌状态由唯一 ViewModel 提供。</summary>
public sealed partial class SettingsPage : Page,IDisposable,IAsyncDisposable
{
    public SettingsViewModel ViewModel {get;}=new(App.Current.Appearance,App.Current.Startup,App.Current.Preferences,App.Current.Mounts,App.Current.Logger);
    private readonly PageScrollBookmark _viewport=new();
    public SettingsPage(){try{InitializeComponent();NavigationCacheMode=NavigationCacheMode.Required;}catch{ViewModel.Dispose();throw;}Loaded+=PageLoaded;}
    protected override void OnNavigatedTo(NavigationEventArgs args){base.OnNavigatedTo(args);_viewport.Enter(SettingsScroll);}
    protected override void OnNavigatedFrom(NavigationEventArgs args){_viewport.Leave(SettingsScroll);base.OnNavigatedFrom(args);}
    private async void PageLoaded(object sender,RoutedEventArgs args){_viewport.Restore(SettingsScroll);ViewModel.ReloadAppearance();await ViewModel.RefreshStartupAsync();}
    private void SaveCache(object sender,RoutedEventArgs args)=>ViewModel.SaveCacheLimit();
    private async void TrimCache(object sender,RoutedEventArgs args)=>await ViewModel.TrimCacheAsync();
    private async void OpenStartupSettings(object sender,RoutedEventArgs args)
    {
        try{await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"));}
        catch(Exception error){App.Current.Logger.Log("启动","打开系统设置失败；"+HybridMount.Models.Diagnostics.SafeDiagnostic.ExceptionSummary(error),InfoBarSeverity.Error);}
    }
    public void Dispose(){Loaded-=PageLoaded;_viewport.Stop();ViewModel.Dispose();}
    public async ValueTask DisposeAsync(){Dispose();await ViewModel.DisposeAsync();}
}
