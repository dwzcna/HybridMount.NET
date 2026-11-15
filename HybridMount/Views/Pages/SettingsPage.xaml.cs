using HybridMount.Models.UI;
using HybridMount;
using HybridMount.Controls.Mounting;
using HybridMount.Models.Diagnostics;
using HybridMount.Models.Localization;
using HybridMount.Models.Mounting;
using HybridMount.UI;
using HybridMount.UI.Localization;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Views.Pages;

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
        catch(Exception error){if(!_disposed)ViewModel.ResetMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Raw(error.Message),PageMessageSeverity.Error);}
        finally{_explorerRecovery=null;}
    }
    private CancellationTokenSource? _resetCancel;private ContentDialog? _resetDialog;private bool _disposed;
    public SettingsViewModel ViewModel {get;}=new(App.Current.Appearance,App.Current.Startup,App.Current.Preferences,App.Current.RuntimeAvailable?App.Current.Mounts:null,App.Current.Logger,cloud:App.Current.CloudOperations);
    private readonly PageScrollBookmark _viewport=new();
    private readonly global::HybridMount.UI.DeferredComboBoxSelection _densitySelection;
    public SettingsPage(){try{InitializeComponent();_densitySelection=new(DensityPicker,value=>ViewModel.DensitySelection=value);_localization=global::HybridMount.UI.UserInterface.ObserveLanguage(this,()=>Bindings.Update());NavigationCacheMode=NavigationCacheMode.Required;}catch{ViewModel.Dispose();throw;}Loaded+=PageLoaded;}
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
            ViewModel.ResetMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Create("CheckingMountsAndDataFoldersForTheCurrentDeployment"));
            await ViewModel.DrainSettingsOperationsAsync();cancel.Token.ThrowIfCancellationRequested();
            var inspection=await App.Current.InspectSettingsResetAsync(cancel.Token);cancel.Token.ThrowIfCancellationRequested();
            if(inspection.Plan is not {} plan)
            {
                ViewModel.ResetMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Raw(inspection.BlockedReason+"\n"+
                    global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ExplorerEntryResetHelp", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!),PageMessageSeverity.Warning);
                return;
            }
            if(_disposed||App.Current.IsExiting)return;
            var content=new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
            global::HybridMount.UI.UserInterface.Bind(content,"Text",()=>global::HybridMount.Models.Localization.ResourceMessage.Create("ThisPermanentlyDeletesAllSettingsLogsAndCachesForTheCurrentDeployment", plan.Scope.Mode, plan.Scope.RegistryPath, string.Join("\n",plan.Scope.Directories)));
            var dialog=global::HybridMount.UI.UserInterface.Configure(new ContentDialog{XamlRoot=XamlRoot,Content=new ScrollViewer{MaxHeight=420,Content=content},DefaultButton=ContentDialogButton.Close},
                ("Title",global::HybridMount.Models.Localization.ResourceMessage.Create("ClearAllAppSettings")),("PrimaryButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("ClearAndExit")),("CloseButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("Cancel")));
            _resetDialog=dialog;var answer=await UserInterface.ShowAsync(dialog);_resetDialog=null;
            cancel.Token.ThrowIfCancellationRequested();if(answer!=ContentDialogResult.Primary||_disposed||App.Current.IsExiting){ViewModel.ResetMessage.IsOpen=false;return;}
            ViewModel.ResetMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Create("StoppingBackgroundServicesAndClearingSettingsForTheCurrentDeployment"));
            await App.Current.ResetSettingsAndExitAsync(plan,cancel.Token);
        }
        catch(OperationCanceledException)when(cancel.IsCancellationRequested||App.Current.IsExiting){if(!_disposed)ViewModel.ResetMessage.IsOpen=false;}
        catch(Exception error){if(!_disposed&&!App.Current.IsExiting)ViewModel.ResetMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Raw(error.Message),PageMessageSeverity.Error);}
        finally{_resetDialog=null;if(ReferenceEquals(_resetCancel,cancel))_resetCancel=null;ViewModel.EndReset();}
    }
    /// <summary>确认前可取消，确认后移交应用生命周期；离开设置页不影响已接纳的逐根任务。</summary>
    private async void RemoveAllMounts(object sender,RoutedEventArgs args)
    {
        if(_disposed||App.Current.IsExiting||!ViewModel.CanRemoveAll||!ViewModel.TryBeginReset())return;
        using var cancel=new CancellationTokenSource();_resetCancel=cancel;
        try
        {
            await ViewModel.DrainSettingsOperationsAsync();
            var plan=await ViewModel.InspectAllMountRemovalsAsync(cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if(plan.Mounts.Count==0){ViewModel.ResetMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveAllEmpty"));return;}
            var summary=new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
            global::HybridMount.UI.UserInterface.Bind(summary,"Text",()=>global::HybridMount.Models.Localization.ResourceMessage.Raw(string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemoveAllConfirmation", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, plan.Mounts.Count)+"\n\n"+
                string.Join("\n",plan.Mounts.Select(item=>item.Name+"\n"+item.LocalPath))));
            var dialog=UserInterface.Configure(new ContentDialog{XamlRoot=XamlRoot,Content=summary,DefaultButton=ContentDialogButton.Close},
                ("Title",global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveAllTitle")),
                ("PrimaryButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveAllKeepCached")),
                ("SecondaryButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveAllDiscard")),
                ("CloseButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("Cancel")));
            _resetDialog=dialog;var answer=await UserInterface.ShowAsync(dialog);_resetDialog=null;
            cancel.Token.ThrowIfCancellationRequested();if(answer==ContentDialogResult.None||_disposed||App.Current.IsExiting)return;
            // 接纳后不再使用页面令牌；应用退出会经MountManager的停止链取消并排空。
            _resetCancel=null;
            ViewModel.ResetMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveAllRunning"));
            var result=await ViewModel.RemoveAllMountsAsync(plan,answer==ContentDialogResult.Primary?
                HybridMount.Models.Mounting.MountRemovalMode.PreserveCachedFiles:HybridMount.Models.Mounting.MountRemovalMode.ClearContents);
            if(!_disposed&&!App.Current.IsExiting)
                ViewModel.ResetMessage.Show(string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemoveAllSummary", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, result.Succeeded, result.Failed, result.Skipped)+"\n"+
                    string.Join("\n",result.Items.Where(item=>item.State!="Completed").Select(item=>item.Path+" — "+item.Error)),
                    result.Failed>0||result.Skipped>0?PageMessageSeverity.Warning:PageMessageSeverity.Success);
        }
        catch(OperationCanceledException)when(cancel.IsCancellationRequested||App.Current.IsExiting){}
        catch(Exception error){if(!_disposed&&!App.Current.IsExiting)ViewModel.ResetMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Raw(error.Message),PageMessageSeverity.Error);}
        finally{_resetDialog=null;if(ReferenceEquals(_resetCancel,cancel))_resetCancel=null;ViewModel.EndReset();}
    }
    private void ShowRemovalTasks(object sender,RoutedEventArgs args)=>App.Current.TaskFocus.ShowAll();
    private void SaveCache(object sender,RoutedEventArgs args)=>ViewModel.SaveCacheLimit();
    private async void TrimCache(object sender,RoutedEventArgs args)=>await ViewModel.TrimCacheAsync();
    private async void OpenStartupSettings(object sender,RoutedEventArgs args)
    {
        try{await global::Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:startupapps"));}
        catch(Exception error){App.Current.Logger.Log("启动","打开系统设置失败；"+HybridMount.Models.Diagnostics.SafeDiagnostic.ExceptionSummary(error),HybridMount.Models.UI.PageMessageSeverity.Error);}
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_densitySelection.Dispose();_explorerRecovery?.Dispose();_resetCancel?.Cancel();_resetDialog?.Hide();_localization?.Dispose();_localization=null;Loaded-=PageLoaded;_viewport.Stop();ViewModel.Dispose();}
    public async ValueTask DisposeAsync(){Dispose();await ViewModel.DisposeAsync();}
}
