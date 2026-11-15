using HybridMount;
using HybridMount.Commons.UI;
using HybridMount.Controls;
using HybridMount.Infrastructure.Recovery;
using HybridMount.Models.Configuration;
using HybridMount.Models.Localization;
using HybridMount.Models.Mounting;
using HybridMount.UI;
using HybridMount.UI.Dispatch;
using HybridMount.UI.Localization;
using HybridMount.ViewModels;
using HybridMount.Views.Dialogs;
using HybridMount.Views.Dialogs.Mounting;
using HybridMount.Views.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using global::Windows.Storage;
namespace HybridMount.Views.Pages;

/// <summary>挂载卡片列表；页面只适配选择、滚动、对话框及Shell打开，业务命令由唯一模型拥有。</summary>
public sealed partial class MountsPage : Page,IDisposable
{
    private IDisposable? _localization;
    public MountsViewModel ViewModel {get;}=new(App.Current.Mounts,App.Current.UIDispatcher,App.Current.Logger);
    private bool _restoringSelection,_disposed;
    private MountBackupDialog? _backupDialog;
    private IMountConfigurationDialog? _configurationDialog;
    private ContentDialog? _diagnosticDialog,_recoveryDialog;
    private CancellationTokenSource? _recoveryCancel;
    private readonly PageScrollBookmark _viewport=new();
    public MountsPage()
    {
        try{InitializeComponent();_localization=global::HybridMount.UI.UserInterface.ObserveLanguage(this,()=>Bindings.Update());NavigationCacheMode=NavigationCacheMode.Required;}catch{ViewModel.Dispose();throw;}
        MountList.SelectionChanged+=(_,_)=>
        {
            if(_restoringSelection)return;_viewport.Capture(MountList);
            ViewModel.SelectedMountId=(MountList.SelectedItem as MountListItemViewModel)?.Id;_viewport.Restore(MountList);_=ViewModel.ReadCapacityAsync(ViewModel.SelectedMountId);
        };
        ViewModel.Refreshing+=CaptureScroll;ViewModel.Refreshed+=RestoreSelection;ViewModel.StateChanged+=UpdatePageState;Loaded+=PageLoaded;UpdatePageState();
    }
    private void PageLoaded(object sender,RoutedEventArgs args)=>_viewport.Restore(MountList);
    protected override void OnNavigatedTo(NavigationEventArgs args){base.OnNavigatedTo(args);_viewport.Enter(MountList);RestoreSelection();if(App.Current.TaskFocus.TakeExportProtection() is {} protection)DispatcherQueue.TryEnqueue(async()=>{if(!_disposed)await ShowExportConfigurationsAsync(protection);});if(App.Current.TaskFocus.TakeConfiguration() is {} mount)DispatcherQueue.TryEnqueue(async()=>{if(!_disposed)await EditSettingsAsync(mount);});}
    protected override void OnNavigatedFrom(NavigationEventArgs args){_viewport.Leave(MountList);base.OnNavigatedFrom(args);}
    private void CaptureScroll(){_restoringSelection=true;_viewport.Capture(MountList);}
    private void RestoreSelection()
    {
        if(_disposed)return;_restoringSelection=true;
        try{MountList.SelectedItem=ViewModel.Items.FirstOrDefault(x=>x.Id==ViewModel.SelectedMountId);}finally{_restoringSelection=false;}
        UpdatePageState();_viewport.Restore(MountList);
    }
    private void UpdatePageState(){if(_disposed)return;AddButton.IsEnabled=ExportButton.IsEnabled=ImportButton.IsEnabled=RecoveryButton.IsEnabled=!ViewModel.DialogBusy&&!ViewModel.IsStopping;}
    /// <summary>按钮携带本行身份；点击未选中行的打开或诊断不会改写当前选择。</summary>
    private async void CardAction(object? sender,MountCardActionEventArgs args)
    {
        if(_disposed||ViewModel.IsStopping)return;
        try
        {
            switch(args.Action)
            {
                case "connection":await ViewModel.ChangeConnectionAsync(args.MountId);break;
                case "settings":await EditSettingsAsync(args.MountId);break;
                case "scan":await ViewModel.ScanChangesAsync(args.MountId);break;
                case "refresh":case "keepOnDevice":case "freeSpace":ViewModel.QueueMount(args.MountId,args.Action);break;
                case "capacity":await ViewModel.RecheckCapacityAsync(args.MountId);break;
                case "remove":await RemoveAsync(args.MountId);break;
                case "diagnostics":if(ViewModel.FindMount(args.MountId)?.UploadIssueCount>0)App.Current.TaskFocus.ShowIssues(args.MountId);else await ShowDiagnosticsAsync(args.MountId);break;
                case "fullDiagnostics":await ShowDiagnosticsAsync(args.MountId);break;
                case "thumbnails":if(args.Value is {} thumbnails)await ViewModel.SetThumbnailAsync(args.MountId,thumbnails);break;
                case "remoteDetection":if(args.Value is {} detection)await ViewModel.SetRemoteDetectionEnabledAsync(args.MountId,detection);break;
                case "open":await ViewModel.RunDialogAsync(async()=>{if(ViewModel.FindMount(args.MountId) is not {} mount)return;var folder=await StorageFolder.GetFolderFromPathAsync(mount.Definition.LocalPath);if(!ViewModel.IsStopping)await global::Windows.System.Launcher.LaunchFolderAsync(folder);});break;
            }
        }
        catch(Exception error){if(!ViewModel.IsStopping)ViewModel.Status=string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("MountOperationFailed", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, error.Message);}
    }
    private Task ShowDiagnosticsAsync(Guid id)=>ViewModel.RunDialogAsync(async()=>
    {
        if(ViewModel.FindMount(id) is not {} mount)return;
        var detail=await ViewModel.DiagnosticAsync(id);if(_disposed||ViewModel.IsStopping)return;
        var text=new TextBlock{Text=detail,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
        try
        {
            _diagnosticDialog=new(){XamlRoot=XamlRoot,Title=string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("FullDiagnostics", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, mount.Definition.Name),CloseButtonText=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Close", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
                Content=new ScrollViewer{Content=text,MaxHeight=520,HorizontalScrollMode=ScrollMode.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled}};
            await global::HybridMount.UI.UserInterface.ShowAsync(_diagnosticDialog);
        }
        finally{_diagnosticDialog=null;}
    });
    private Task EditSettingsAsync(Guid id)=>ViewModel.FindMount(id) is {} mount?ShowConfigurationAsync(mount.Definition.Source!.Kind,mount.Definition):Task.CompletedTask;
    /// <summary>新增和编辑都使用协议专属 XAML；SQS 选项在 S3 配置内部折叠，不再弹出其他配置窗口。</summary>
    private Task ShowConfigurationAsync(FileSourceKind kind,MountDefinition? existing=null)=>ViewModel.RunDialogAsync(()=>ShowConfigurationCoreAsync(kind,existing));
    private async Task ShowConfigurationCoreAsync(FileSourceKind kind,MountDefinition? existing=null,MountBackupItem? imported=null)
    {
        try
        {
            ContentDialog dialog=kind switch
            {
                FileSourceKind.WebDav=>new WebDAVConfigurationContentDialog(ViewModel,existing,imported),
                FileSourceKind.S3=>new S3ConfigurationContentDialog(ViewModel,existing,imported),
                FileSourceKind.Synology=>new SynologyConfigurationContentDialog(ViewModel,existing,imported),
                _=>throw new NotSupportedException(global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NoConfigurationDialogIsAvailableForThisProtocolYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!)
            };
            _configurationDialog=(IMountConfigurationDialog)dialog;dialog.XamlRoot=XamlRoot;await global::HybridMount.UI.UserInterface.ShowAsync(dialog);
            if(!ViewModel.IsStopping)
            {
                if(_configurationDialog.AcceptedTaskId is {} taskId)App.Current.TaskFocus.ShowTask(taskId);
                if(existing is null&&_configurationDialog.CreatedMount is {} created){ViewModel.SelectedMountId=created.Id;RestoreSelection();}
                if(_configurationDialog.ResultMessage.Length>0)ViewModel.PublishStatus(_configurationDialog.ResultMessage,_configurationDialog.ResultSeverity);
            }
        }
        finally{_configurationDialog?.Dispose();_configurationDialog=null;}
    }
    private async void CreateWebDavMount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.WebDav);
    private async void CreateS3Mount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.S3);
    private async void CreateSynologyMount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.Synology);
    /// <summary>导出对话框默认选择全部网络挂载；真正的凭据读取与可选加密写入由应用服务执行。</summary>
    private async void ExportConfigurations(object sender,RoutedEventArgs args)=>await ShowExportConfigurationsAsync();
    private Task ShowExportConfigurationsAsync(bool protection=false)=>ViewModel.RunDialogAsync(async()=>
    {
        try { _backupDialog=new MountBackupDialog(protectionInitially:protection){XamlRoot=XamlRoot};await global::HybridMount.UI.UserInterface.ShowAsync(_backupDialog);if(!ViewModel.IsStopping&&_backupDialog.AcceptedTaskId is {} taskId)App.Current.TaskFocus.ShowTask(taskId);if(_backupDialog.ResultMessage.Length>0)ViewModel.Status=_backupDialog.ResultMessage; }
        finally { _backupDialog=null; }
    });
    /// <summary>文件选择只接受 .hmxbk；自动识别保护模式后预览，保存不会自动连接。</summary>
    private async void ImportConfigurations(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        var picker=new global::Windows.Storage.Pickers.FileOpenPicker();picker.FileTypeFilter.Add(".hmxbk");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
        var file=await picker.PickSingleFileAsync();if(file is null||App.Current.IsExiting)return;
        MountBackupItem? imported=null;
        try
        {
            _backupDialog=new MountBackupDialog(file.Path){XamlRoot=XamlRoot};await global::HybridMount.UI.UserInterface.ShowAsync(_backupDialog);
            imported=_backupDialog.TakeSelectedConfiguration();
            if(_backupDialog.ResultMessage.Length>0)ViewModel.Status=_backupDialog.ResultMessage;
        }
        finally { _backupDialog=null; }
        if(imported is not null&&!ViewModel.IsStopping)await ShowConfigurationCoreAsync(imported.Source.Kind,imported:imported);
    });
    /// <summary>多选只捕获快照；确认后应用串行处理，取消时对话框等待当前项及临时连接收尾。</summary>
    private async void RecoverOrphanedMounts(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        using var cancel=new CancellationTokenSource();_recoveryCancel=cancel;
        var list=new ListView{ItemsSource=ViewModel.RecoveryRoots,ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:loc=\"using:HybridMount.UI\"><TextBlock loc:UserInterface.Source=\"{Binding}\" loc:UserInterface.Path=\"Display\" TextWrapping=\"Wrap\"/></DataTemplate>"),SelectionMode=ListViewSelectionMode.Multiple,IsMultiSelectCheckBoxEnabled=true,MaxHeight=320};
        var feedback=new InfoBar{Style=(Style)Application.Current.Resources["InstantNotificationStyle"],IsClosable=true,Severity=InfoBarSeverity.Informational,IsOpen=false};
        var count=new TextBlock();var choose=global::HybridMount.UI.UserInterface.Configure(new Button{Style=(Style)Application.Current.Resources["ActionButtonStyle"]},("Content",global::HybridMount.Models.Localization.ResourceMessage.Create("SelectOldMountFolder")));
        var selectAll=global::HybridMount.UI.UserInterface.Configure(new Button{Style=(Style)Application.Current.Resources["ActionButtonStyle"]},("Content",global::HybridMount.Models.Localization.ResourceMessage.Create("SelectAllEligibleItems")));var clearAll=global::HybridMount.UI.UserInterface.Configure(new Button{Style=(Style)Application.Current.Resources["ActionButtonStyle"]},("Content",global::HybridMount.Models.Localization.ResourceMessage.Create("DeselectAll")));
        var choosing=false;var changing=false;var selecting=true;var explorerRequested=false;
        var explorer=global::HybridMount.UI.UserInterface.Configure(new Button{Style=(Style)Application.Current.Resources["SubtleButtonStyle"]},
            ("Content",global::HybridMount.Models.Localization.ResourceMessage.Create("ExplorerEntrySearch")));
        void ExplorerRequested(object _,RoutedEventArgs __){explorerRequested=true;_recoveryDialog?.Hide();}
        void UpdateSelection()
        {
            if(_disposed||ViewModel.IsStopping||!selecting)return;
            if(!changing)
            {
                changing=true;try{foreach(var invalid in list.SelectedItems.OfType<OrphanedMountCandidate>().Where(x=>!x.CanRemove).ToArray())list.SelectedItems.Remove(invalid);}finally{changing=false;}
            }
            global::HybridMount.UI.UserInterface.Set(count,"Text",global::HybridMount.Models.Localization.ResourceMessage.Create("ItemsSelected", list.SelectedItems.Count));
            if(_recoveryDialog is {} dialog){dialog.IsPrimaryButtonEnabled=!choosing&&list.SelectedItems.Count>0;global::HybridMount.UI.UserInterface.Set(dialog,"PrimaryButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("ProcessSelectedDetails", list.SelectedItems.Count));}
        }
        void SelectionChanged(object _,SelectionChangedEventArgs __)=>UpdateSelection();
        void PrepareRow(ListViewBase _,ContainerContentChangingEventArgs change)
        {change.ItemContainer.IsEnabled=change.InRecycleQueue||change.Item is OrphanedMountCandidate{CanRemove:true};}
        void SelectAll(object _,RoutedEventArgs __){foreach(var item in ViewModel.RecoveryRoots.Where(x=>x.CanRemove))if(!list.SelectedItems.Contains(item))list.SelectedItems.Add(item);}
        void ClearAll(object _,RoutedEventArgs __)=>list.SelectedItems.Clear();
        async void Choose(object _,RoutedEventArgs __)
        {
            choosing=true;choose.IsEnabled=selectAll.IsEnabled=clearAll.IsEnabled=false;UpdateSelection();
            try
            {
                var picker=new global::Windows.Storage.Pickers.FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
                var folder=await picker.PickSingleFolderAsync();if(folder is null||cancel.IsCancellationRequested||ViewModel.IsStopping)return;
                var inspected=await ViewModel.AddRecoveryRootAsync(folder.Path,cancel.Token);
                if(inspected.Managed is not null){feedback.Severity=InfoBarSeverity.Warning;global::HybridMount.UI.UserInterface.Set(feedback, "Message", inspected.Message);feedback.IsOpen=true;return;}
                if(!ViewModel.IsStopping&&ViewModel.SelectedRecoveryRoot is {} added)
                {
                    if(!list.SelectedItems.Contains(added))list.SelectedItems.Add(added);list.ScrollIntoView(added);feedback.IsOpen=false;
                }
            }
            catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
            catch(Exception error){if(!ViewModel.IsStopping){global::HybridMount.UI.UserInterface.Set(feedback, "Message", error.Message);feedback.IsOpen=true;}}
            finally{choosing=false;if(!ViewModel.IsStopping){choose.IsEnabled=selectAll.IsEnabled=clearAll.IsEnabled=true;UpdateSelection();}}
        }
        explorer.Click+=ExplorerRequested;list.SelectionChanged+=SelectionChanged;list.ContainerContentChanging+=PrepareRow;choose.Click+=Choose;selectAll.Click+=SelectAll;clearAll.Click+=ClearAll;
        try
        {
            try{await ViewModel.DiscoverRecoveryRootsAsync(cancel.Token);}
            catch(OperationCanceledException)when(cancel.IsCancellationRequested){return;}
            catch(Exception error){feedback.Severity=InfoBarSeverity.Warning;global::HybridMount.UI.UserInterface.Set(feedback, "Message", error.Message);feedback.IsOpen=true;}
            if(ViewModel.IsStopping)return;
            var buttons=new ActionPanel{Style=(Style)Application.Current.Resources["ActionPanelStyle"]};buttons.Children.Add(selectAll);buttons.Children.Add(clearAll);
            var panel=new StackPanel{Spacing=12};panel.Children.Add(buttons);panel.Children.Add(count);panel.Children.Add(list);panel.Children.Add(choose);panel.Children.Add(explorer);panel.Children.Add(feedback);
            if(ViewModel.RecoveryRoots.Count==0&&!feedback.IsOpen){global::HybridMount.UI.UserInterface.Set(feedback, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("NoVerifiableLegacyMountsWereFoundSelectAnOldMountFolderTo"));feedback.IsOpen=true;}
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("LegacyMounts", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,Content=panel,PrimaryButtonText=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ProcessSelected", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,CloseButtonText=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Close", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,DefaultButton=ContentDialogButton.Close,IsPrimaryButtonEnabled=false};
            UpdateSelection();
            var selected=await global::HybridMount.UI.UserInterface.ShowAsync(_recoveryDialog);selecting=false;
            if(explorerRequested&&!ViewModel.IsStopping){await RecoverExplorerEntriesAsync();return;}
            if(selected!=ContentDialogResult.Primary||ViewModel.IsStopping)return;
            var captured=HybridMount.Infrastructure.Recovery.OrphanedMountBatch.Capture(list.SelectedItems.OfType<OrphanedMountCandidate>());
            var details=string.Join("\n\n",captured.Select(root=>root.LocalPath+"\n"+RecoveryActionDescription(root)));
            if(captured.Any(x=>!x.ShellOnly))details+=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("AllLocalContentInTheListedItemsMarkedForClearingIncludingLocal", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
            var confirmation=new TextBlock{Text=details,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title=string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ConfirmProcessingLegacyItems", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, captured.Length),Content=new ScrollViewer{Content=confirmation,MaxHeight=420},PrimaryButtonText=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ConfirmProcessing", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,CloseButtonText=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Cancel", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,DefaultButton=ContentDialogButton.Close};
            if(await global::HybridMount.UI.UserInterface.ShowAsync(_recoveryDialog)!=ContentDialogResult.Primary||ViewModel.IsStopping)return;
            var progress=global::HybridMount.UI.UserInterface.Configure(new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true},("Text",global::HybridMount.Models.Localization.ResourceMessage.Create("RecheckingFolderIdentity")));
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ProcessLegacyMounts", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,Content=new ScrollViewer{Content=progress,MaxHeight=420},CloseButtonText=global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Cancel", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,DefaultButton=ContentDialogButton.Close};
            var working=true;long lastProgress=0;
            void CancelClicked(ContentDialog _,ContentDialogButtonClickEventArgs click)
            {if(working){click.Cancel=true;cancel.Cancel();if(_recoveryDialog is {} dialog)global::HybridMount.UI.UserInterface.Set(dialog,"CloseButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("CancellationInProgress"));}}
            void ProgressClosing(ContentDialog _,ContentDialogClosingEventArgs closing)
            {if(working&&!_disposed&&!ViewModel.IsStopping){closing.Cancel=true;cancel.Cancel();if(_recoveryDialog is {} dialog)global::HybridMount.UI.UserInterface.Set(dialog,"CloseButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("CancellationInProgress"));}}
            _recoveryDialog.Closing+=ProgressClosing;_recoveryDialog.CloseButtonClick+=CancelClicked;var shown=global::HybridMount.UI.UserInterface.ShowAsync(_recoveryDialog);
            try
            {
                var result=await ViewModel.ClearRecoveryRootsAsync(captured,cancel.Token,line=>
                {
                    var now=System.Diagnostics.Stopwatch.GetTimestamp();if(now-Interlocked.Read(ref lastProgress)<System.Diagnostics.Stopwatch.Frequency/10&&line.Stage!="正在重新核对目录身份…")return;
                    Interlocked.Exchange(ref lastProgress,now);
                    App.Current.UIDispatcher.TryEnqueue(()=>{if(!_disposed&&!ViewModel.IsStopping&&working)progress.Text=$"{line.Index}/{line.Total}\n{line.LocalPath}\n{line.Stage}";});
                });
                if(!ViewModel.IsStopping)progress.Text=result.Detail;
            }
            catch(OperationCanceledException){if(!ViewModel.IsStopping){global::HybridMount.UI.UserInterface.Set(progress,"Text",global::HybridMount.Models.Localization.ResourceMessage.Create("CanceledCompletedDeletionsCannotBeUndone"));ViewModel.Status=progress.Text;}}
            catch(Exception error){if(!ViewModel.IsStopping){progress.Text=error.Message;ViewModel.ReportError(error.Message);}}
            finally{working=false;if(!ViewModel.IsStopping&&_recoveryDialog is {} dialog)global::HybridMount.UI.UserInterface.Set(dialog,"CloseButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("Close"));}
            await shown;_recoveryDialog.CloseButtonClick-=CancelClicked;_recoveryDialog.Closing-=ProgressClosing;
        }
        finally
        {
            selecting=false;explorer.Click-=ExplorerRequested;list.SelectionChanged-=SelectionChanged;list.ContainerContentChanging-=PrepareRow;choose.Click-=Choose;selectAll.Click-=SelectAll;clearAll.Click-=ClearAll;
            cancel.Cancel();_recoveryCancel=null;_recoveryDialog=null;
        }
    });
    /// <summary>确认逐项标明内容影响，不把入口清理与清空根目录混为同一操作。</summary>
    private static string RecoveryActionDescription(OrphanedMountCandidate root)=>root.OrphanedDirectory?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TheOriginalProviderCannotBeVerifiedATemporaryLocalProviderWillTake", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:root.ShellOnly?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemoveOnlyTheCurrentUserSLegacyExplorerEntryKeepTheFolder", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ClearAllLocalContentThenUnregisterOfflineKeepTheEmptyRootFolder", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    private Task RemoveAsync(Guid id)=>ViewModel.RunForMountAsync(id,async selected=>await ViewModel.RunDialogAsync(async()=>
    {
        var mount=selected.Definition;
        if(mount.RemovalPending&&mount.RemovalMode==MountRemovalMode.PreserveCachedFiles)
        {
            var resume=global::HybridMount.UI.UserInterface.Configure(new ContentDialog{XamlRoot=XamlRoot,DefaultButton=ContentDialogButton.Close},
                ("Title",global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveCachedOperation")),
                ("Content",global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveCachedResume", mount.LocalPath, selected.Detail)),
                ("PrimaryButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveAllKeepCached")),
                ("CloseButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("Cancel")));
            if(await global::HybridMount.UI.UserInterface.ShowAsync(resume)==ContentDialogResult.Primary)ViewModel.QueueRemoval(mount.Id,false,cached:true);
            return;
        }
        var unregistered=mount.RegistrationStage==MountRegistrationStage.NotRegistered;
        string ConfirmationContent()
        {
            var message=string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("DownloadAndRemoveDownloadCompletelyThenKeepAsARegularLocalFolder", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, mount.LocalPath);
            if(unregistered)message=string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemoveMountKeepLocalFilesWithoutConnectingToTheRemoteSourceClear", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, mount.LocalPath);
            if(mount.RemovalPending)message+=string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ParagraphWithSuffix", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, selected.Detail, (mount.RemovalStage==MountRemovalStage.Unregistering?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ContentProcessingIsAlreadyCompleteOnlyUnregistrationAndExplorerEntryCleanupWill", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ContinueWithTheSameRemovalMethod", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!));
            return message;
        }
        var dialog=global::HybridMount.UI.UserInterface.Configure(new ContentDialog{XamlRoot=XamlRoot,DefaultButton=ContentDialogButton.Close,            IsPrimaryButtonEnabled=!mount.RemovalPending||mount.RemovalMode==MountRemovalMode.PreserveFiles,            IsSecondaryButtonEnabled=!mount.RemovalPending||mount.RemovalMode==MountRemovalMode.ClearContents}, ("Title", global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveMount")), ("PrimaryButtonText", mount.RemovalPending&&mount.RemovalStage==MountRemovalStage.Unregistering ? global::HybridMount.Models.Localization.ResourceMessage.Create("ContinueRemovalCleanup") : unregistered ? global::HybridMount.Models.Localization.ResourceMessage.Create("RemoveMountKeepLocalFiles") : global::HybridMount.Models.Localization.ResourceMessage.Create("DownloadAndRemove")), ("SecondaryButtonText", mount.RemovalPending&&mount.RemovalStage==MountRemovalStage.Unregistering ? global::HybridMount.Models.Localization.ResourceMessage.Create("ContinueRemovalCleanup") : global::HybridMount.Models.Localization.ResourceMessage.Create("ClearAndRemove")), ("CloseButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("Cancel")));
        global::HybridMount.UI.UserInterface.Bind(dialog,"Content",()=>global::HybridMount.Models.Localization.ResourceMessage.Raw(ConfirmationContent()));
        var result=await global::HybridMount.UI.UserInterface.ShowAsync(dialog);
        if(result==ContentDialogResult.None)return;
        if(result==ContentDialogResult.Secondary)
        {
            var confirm=global::HybridMount.UI.UserInterface.Configure(new ContentDialog{XamlRoot=XamlRoot,DefaultButton=ContentDialogButton.Close},("Title",global::HybridMount.Models.Localization.ResourceMessage.Create("ConfirmClearAndRemove")),("Content",global::HybridMount.Models.Localization.ResourceMessage.Create("AllLocalContentInTheFolderIncludingEditedAndNewFilesWill", mount.LocalPath)),("PrimaryButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("ClearAndRemove")),("CloseButtonText",global::HybridMount.Models.Localization.ResourceMessage.Create("Cancel")));
            if(await global::HybridMount.UI.UserInterface.ShowAsync(confirm)!=ContentDialogResult.Primary)return;
        }
        ViewModel.QueueRemoval(mount.Id,result==ContentDialogResult.Secondary);
    }));
    /// <summary>真正释放才取消对话框和视图订阅；导航离页不停止后台挂载。</summary>
    public void Dispose()
    {_localization?.Dispose();_localization=null;
        if(_disposed)return;_disposed=true;Loaded-=PageLoaded;_viewport.Stop();_diagnosticDialog?.Hide();_explorerRecovery?.Dispose();_recoveryCancel?.Cancel();_recoveryDialog?.Hide();_configurationDialog?.CancelForShutdown();_backupDialog?.CancelForShutdown();
        ViewModel.Refreshing-=CaptureScroll;ViewModel.Refreshed-=RestoreSelection;ViewModel.StateChanged-=UpdatePageState;ViewModel.Dispose();
    }
}
