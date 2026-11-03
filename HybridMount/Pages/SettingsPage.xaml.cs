using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Pages;

/// <summary>设置页仅适配控件和系统设置链接，所有偏好／草稿与忙碌状态由唯一 ViewModel 提供。</summary>
public sealed partial class SettingsPage : Page,IDisposable,IAsyncDisposable
{
    public SettingsViewModel ViewModel {get;}=new(App.Current.Appearance,App.Current.Startup,App.Current.Preferences,App.Current.Mounts,App.Current.Logger);
    public SettingsPage(){InitializeComponent();Loaded+=PageLoaded;}
    private async void PageLoaded(object sender,RoutedEventArgs args){ViewModel.ReloadAppearance();await ViewModel.RefreshStartupAsync();}
    private void SaveCache(object sender,RoutedEventArgs args)=>ViewModel.SaveCacheLimit();
    private async void TrimCache(object sender,RoutedEventArgs args)=>await ViewModel.TrimCacheAsync();
    private async void OpenStartupSettings(object sender,RoutedEventArgs args)
    {
        try{await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"));}
        catch(Exception error){App.Current.Logger.Log("启动",error.Message,InfoBarSeverity.Error);}
    }
    public void Dispose(){Loaded-=PageLoaded;ViewModel.Dispose();}
    public async ValueTask DisposeAsync(){Dispose();await ViewModel.DisposeAsync();}
}
