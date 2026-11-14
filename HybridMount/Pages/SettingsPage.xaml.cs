using HybridMount.Helpers.UI;
using Microsoft.UI.Xaml.Navigation;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Pages;

/// <summary>设置页仅适配控件和系统设置链接，所有偏好／草稿与忙碌状态由唯一 ViewModel 提供。</summary>
public sealed partial class SettingsPage : Page,IDisposable,IAsyncDisposable
{
    private IDisposable? _localization;
    private HybridMount.Controls.Mounting.ExplorerEntryDialog? _explorerRecovery;
    private async void SearchExplorerEntries(object sender,RoutedEventArgs args)
    {
        if(_disposed||App.Current.IsExiting||_explorerRecovery is not null)return;
        using var dialog=new HybridMount.Controls.Mounting.ExplorerEntryDialog(this,()=>_disposed||App.Current.IsExiting);
        _explorerRecovery=dialog;
        try{await dialog.ShowAsync();}
        catch(Exception error){if(!_disposed)ViewModel.ResetMessage.Show(global::HybridMount.Services.Localization.ResourceMessage.Raw(error.Message),PageMessageSeverity.Error);}
        finally{_explorerRecovery=null;}
    }
    private CancellationTokenSource? _resetCancel;private ContentDialog? _resetDialog;private bool _disposed;
    public SettingsViewModel ViewModel {get;}=new(App.Current.Appearance,App.Current.Startup,App.Current.Preferences,App.Current.RuntimeAvailable?App.Current.Mounts:null,App.Current.Logger,cloud:App.Current.CloudOperations);
    private readonly PageScrollBookmark _viewport=new();
    public SettingsPage(){try{InitializeComponent();_localization=global::HybridMount.Helpers.UI.Ui.ObserveLanguage(this,()=>Bindings.Update());NavigationCacheMode=NavigationCacheMode.Required;}catch{ViewModel.Dispose();throw;}Loaded+=PageLoaded;}
    protected override void OnNavigatedTo(NavigationEventArgs args){base.OnNavigatedTo(args);_viewport.Enter(SettingsScroll);}
    protected override void OnNavigatedFrom(NavigationEventArgs args){_explorerRecovery?.Dispose();_resetCancel?.Cancel();_resetDialog?.Hide();_viewport.Leave(SettingsScroll);base.OnNavigatedFrom(args);}
    private async void SaveCloudConcurrency(object sender,RoutedEventArgs args)=>await ViewModel.SaveCloudConcurrencyAsync();
    private async void PageLoaded(object sender,RoutedEventArgs args){_viewport.Restore(SettingsScroll);ViewModel.ReloadAppearance();await ViewModel.RefreshStartupAsync();}
    /// <summary>只读检查后一次明确确认当前模式的精确范围；取消、离页或退出不授权删除，接纳后交给应用顺序收尾。</summary>
    private async void ResetSettings(object sender,RoutedEventArgs args)
    {
        if(_disposed||App.Current.IsExiting||!ViewModel.TryBeginReset())return;
        using var cancel=new CancellationTokenSource();_resetCancel=cancel;
        try
        {
            ViewModel.ResetMessage.Show(global::HybridMount.Services.Localization.UiText.Text("Text_BFE711C7C723"));
            await ViewModel.DrainSettingsOperationsAsync();cancel.Token.ThrowIfCancellationRequested();
            var inspection=await App.Current.InspectSettingsResetAsync(cancel.Token);cancel.Token.ThrowIfCancellationRequested();
            if(inspection.Plan is not {} plan)
            {
                ViewModel.ResetMessage.Show(global::HybridMount.Services.Localization.ResourceMessage.Raw(inspection.BlockedReason+"\n"+
                    global::HybridMount.Services.Localization.UiText.Text("ExplorerEntryResetHelp")),PageMessageSeverity.Warning);
                return;
            }
            if(_disposed||App.Current.IsExiting)return;
            var content=new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true,MaxWidth=560};
            global::HybridMount.Helpers.UI.Ui.Bind(content,"Text",()=>global::HybridMount.Services.Localization.UiText.Message("Text_373F72608CC3",plan.Scope.Mode,plan.Scope.RegistryPath,string.Join("\n",plan.Scope.Directories)));
            var dialog=global::HybridMount.Helpers.UI.Ui.Configure(new ContentDialog{XamlRoot=XamlRoot,Content=new ScrollViewer{MaxHeight=420,Content=content},DefaultButton=ContentDialogButton.Close},
                ("Title",global::HybridMount.Services.Localization.UiText.Message("Text_0A1224F36CA9")),("PrimaryButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_5F19F9B45A5E")),("CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_2CD0F3BE8738")));
            _resetDialog=dialog;var answer=await dialog.ShowAsync();_resetDialog=null;
            cancel.Token.ThrowIfCancellationRequested();if(answer!=ContentDialogResult.Primary||_disposed||App.Current.IsExiting){ViewModel.ResetMessage.IsOpen=false;return;}
            ViewModel.ResetMessage.Show(global::HybridMount.Services.Localization.UiText.Text("Text_A963767D29BA"));
            await App.Current.ResetSettingsAndExitAsync(plan,cancel.Token);
        }
        catch(OperationCanceledException)when(cancel.IsCancellationRequested||App.Current.IsExiting){if(!_disposed)ViewModel.ResetMessage.IsOpen=false;}
        catch(Exception error){if(!_disposed&&!App.Current.IsExiting)ViewModel.ResetMessage.Show(global::HybridMount.Services.Localization.ResourceMessage.Raw(error.Message),PageMessageSeverity.Error);}
        finally{_resetDialog=null;if(ReferenceEquals(_resetCancel,cancel))_resetCancel=null;ViewModel.EndReset();}
    }
    private void SaveCache(object sender,RoutedEventArgs args)=>ViewModel.SaveCacheLimit();
    private async void TrimCache(object sender,RoutedEventArgs args)=>await ViewModel.TrimCacheAsync();
    private async void OpenStartupSettings(object sender,RoutedEventArgs args)
    {
        try{await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"));}
        catch(Exception error){App.Current.Logger.Log("启动","打开系统设置失败；"+HybridMount.Models.Diagnostics.SafeDiagnostic.ExceptionSummary(error),InfoBarSeverity.Error);}
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_explorerRecovery?.Dispose();_resetCancel?.Cancel();_resetDialog?.Hide();_localization?.Dispose();_localization=null;Loaded-=PageLoaded;_viewport.Stop();ViewModel.Dispose();}
    public async ValueTask DisposeAsync(){Dispose();await ViewModel.DisposeAsync();}
}
