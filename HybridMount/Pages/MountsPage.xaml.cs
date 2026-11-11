using HybridMount.Helpers.UI;
using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using HybridMount.Views;
using HybridMount.Controls.Mounting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using global::Windows.Storage;
namespace HybridMount.Pages;

/// <summary>挂载卡片列表；页面只适配选择、滚动、对话框及Shell打开，业务命令由唯一模型拥有。</summary>
public sealed partial class MountsPage : Page,IDisposable
{
    private IDisposable? _localization;
    public MountsViewModel ViewModel {get;}=new(App.Current.Mounts,App.Current.UiDispatcher,App.Current.Logger);
    private bool _restoringSelection,_disposed;
    private MountBackupDialog? _backupDialog;
    private IMountConfigurationDialog? _configurationDialog;
    private ContentDialog? _diagnosticDialog,_recoveryDialog;
    private CancellationTokenSource? _recoveryCancel;
    private readonly PageScrollBookmark _viewport=new();
    public MountsPage()
    {
        try{InitializeComponent();_localization=global::HybridMount.Helpers.UI.Ui.ObserveLanguage(this,()=>Bindings.Update());NavigationCacheMode=NavigationCacheMode.Required;}catch{ViewModel.Dispose();throw;}
        MountList.SelectionChanged+=(_,_)=>
        {
            if(_restoringSelection)return;_viewport.Capture(MountList);
            ViewModel.SelectedMountId=(MountList.SelectedItem as MountListItem)?.Id;_viewport.Restore(MountList);_=ViewModel.ReadCapacityAsync(ViewModel.SelectedMountId);
        };
        ViewModel.Refreshing+=CaptureScroll;ViewModel.Refreshed+=RestoreSelection;ViewModel.StateChanged+=UpdatePageState;Loaded+=PageLoaded;UpdatePageState();
    }
    private void PageLoaded(object sender,RoutedEventArgs args)=>_viewport.Restore(MountList);
    protected override void OnNavigatedTo(NavigationEventArgs args){base.OnNavigatedTo(args);_viewport.Enter(MountList);RestoreSelection();}
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
        catch(Exception error){if(!ViewModel.IsStopping)ViewModel.Status=global::HybridMount.Services.Localization.UiText.Text("Text_BD0859D1C3CC",error.Message);}
    }
    private Task ShowDiagnosticsAsync(Guid id)=>ViewModel.RunDialogAsync(async()=>
    {
        if(ViewModel.FindMount(id) is not {} mount)return;
        var detail=await ViewModel.DiagnosticAsync(id);if(_disposed||ViewModel.IsStopping)return;
        var text=new TextBlock{Text=detail,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
        try
        {
            _diagnosticDialog=new(){XamlRoot=XamlRoot,Title=global::HybridMount.Services.Localization.UiText.Text("Text_57E4A215300D",mount.Definition.Name),CloseButtonText=global::HybridMount.Services.Localization.UiText.Text("Text_3FD47EDCE45B"),
                Content=new ScrollViewer{Content=text,MinWidth=340,MaxWidth=640,MaxHeight=520,HorizontalScrollMode=ScrollMode.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled}};
            await global::HybridMount.Helpers.UI.Ui.ShowAsync(_diagnosticDialog);
        }
        finally{_diagnosticDialog=null;}
    });
    private Task EditSettingsAsync(Guid id)=>ViewModel.FindMount(id) is {} mount?ShowConfigurationAsync(mount.Definition.Source!.Kind,mount.Definition):Task.CompletedTask;
    /// <summary>新增和编辑都使用协议专属 XAML；SQS 选项在 S3 配置内部折叠，不再弹出其他配置窗口。</summary>
    private Task ShowConfigurationAsync(FileSourceKind kind,MountDefinition? existing=null,bool httpsOnly=false)=>ViewModel.RunDialogAsync(async()=>
    {
        try
        {
            ContentDialog dialog=kind switch
            {
                FileSourceKind.WebDav=>new WebDAVConfigurationContentDialog(ViewModel,existing,httpsOnly),
                FileSourceKind.S3=>new S3ConfigurationContentDialog(ViewModel,existing),
                FileSourceKind.Synology=>new SynologyConfigurationContentDialog(ViewModel,existing),
                _=>throw new NotSupportedException(global::HybridMount.Services.Localization.UiText.Text("Text_7B80D5D2DE3D"))
            };
            _configurationDialog=(IMountConfigurationDialog)dialog;dialog.XamlRoot=XamlRoot;await global::HybridMount.Helpers.UI.Ui.ShowAsync(dialog);
            if(!ViewModel.IsStopping)
            {
                if(existing is null&&_configurationDialog.CreatedMount is {} created){ViewModel.SelectedMountId=created.Id;RestoreSelection();}
                if(_configurationDialog.ResultMessage.Length>0)ViewModel.PublishStatus(_configurationDialog.ResultMessage,_configurationDialog.ResultSeverity);
            }
        }
        finally{_configurationDialog?.Dispose();_configurationDialog=null;}
    });
    private async void CreateWebDavMount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.WebDav);
    private async void CreateWebDavHttpsMount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.WebDav,httpsOnly:true);
    private async void CreateS3Mount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.S3);
    private async void CreateSynologyMount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.Synology);
    /// <summary>导出对话框默认选择全部网络挂载；真正的凭据读取与密文写入由应用服务执行。</summary>
    private async void ExportConfigurations(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        try { _backupDialog=new MountBackupDialog{XamlRoot=XamlRoot};await global::HybridMount.Helpers.UI.Ui.ShowAsync(_backupDialog);if(_backupDialog.ResultMessage.Length>0)ViewModel.Status=_backupDialog.ResultMessage; }
        finally { _backupDialog=null; }
    });
    /// <summary>文件选择只接受加密配置；解密通过后先预览，保存不会自动连接。</summary>
    private async void ImportConfigurations(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        var picker=new global::Windows.Storage.Pickers.FileOpenPicker();picker.FileTypeFilter.Add(".hymcfg");picker.FileTypeFilter.Add(".hmconfig");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
        var file=await picker.PickSingleFileAsync();if(file is null||App.Current.IsExiting)return;
        try
        {
            _backupDialog=new MountBackupDialog(file.Path){XamlRoot=XamlRoot};await global::HybridMount.Helpers.UI.Ui.ShowAsync(_backupDialog);
            if(_backupDialog.ImportedIds.Count>0){ViewModel.SelectedMountId=_backupDialog.ImportedIds[^1];RestoreSelection();}
            if(_backupDialog.ResultMessage.Length>0)ViewModel.Status=_backupDialog.ResultMessage;
        }
        finally { _backupDialog=null; }
    });
    /// <summary>多选只捕获快照；确认后应用串行处理，取消时对话框等待当前项及临时连接收尾。</summary>
    private async void RecoverOrphanedMounts(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        using var cancel=new CancellationTokenSource();_recoveryCancel=cancel;
        var list=new ListView{ItemsSource=ViewModel.RecoveryRoots,ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:loc=\"using:HybridMount.Helpers.UI\"><TextBlock loc:Ui.Source=\"{Binding}\" loc:Ui.Path=\"Display\" TextWrapping=\"Wrap\"/></DataTemplate>"),SelectionMode=ListViewSelectionMode.Multiple,IsMultiSelectCheckBoxEnabled=true,MinWidth=360,MaxWidth=620,MaxHeight=320};
        var feedback=new InfoBar{IsClosable=true,Severity=InfoBarSeverity.Informational,IsOpen=false};
        var count=new TextBlock();var choose=global::HybridMount.Helpers.UI.Ui.Configure(new Button{},("Content",global::HybridMount.Services.Localization.UiText.Message("Text_9D163C698599")));
        var selectAll=global::HybridMount.Helpers.UI.Ui.Configure(new Button{},("Content",global::HybridMount.Services.Localization.UiText.Message("Text_BE146CE5330F")));var clearAll=global::HybridMount.Helpers.UI.Ui.Configure(new Button{},("Content",global::HybridMount.Services.Localization.UiText.Message("Text_F4D4BAE588C4")));
        var choosing=false;var changing=false;var selecting=true;
        void UpdateSelection()
        {
            if(_disposed||ViewModel.IsStopping||!selecting)return;
            if(!changing)
            {
                changing=true;try{foreach(var invalid in list.SelectedItems.OfType<OrphanedMountCandidate>().Where(x=>!x.CanRemove).ToArray())list.SelectedItems.Remove(invalid);}finally{changing=false;}
            }
            global::HybridMount.Helpers.UI.Ui.Set(count,"Text",global::HybridMount.Services.Localization.UiText.Message("Text_5A5C8318EF64",list.SelectedItems.Count));
            if(_recoveryDialog is {} dialog){dialog.IsPrimaryButtonEnabled=!choosing&&list.SelectedItems.Count>0;global::HybridMount.Helpers.UI.Ui.Set(dialog,"PrimaryButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_FDEB8FA90307",list.SelectedItems.Count));}
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
                await ViewModel.AddRecoveryRootAsync(folder.Path,cancel.Token);
                if(!ViewModel.IsStopping&&ViewModel.SelectedRecoveryRoot is {} added)
                {
                    if(!list.SelectedItems.Contains(added))list.SelectedItems.Add(added);list.ScrollIntoView(added);feedback.IsOpen=false;
                }
            }
            catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
            catch(Exception error){if(!ViewModel.IsStopping){feedback.Message=error.Message;feedback.IsOpen=true;}}
            finally{choosing=false;if(!ViewModel.IsStopping){choose.IsEnabled=selectAll.IsEnabled=clearAll.IsEnabled=true;UpdateSelection();}}
        }
        list.SelectionChanged+=SelectionChanged;list.ContainerContentChanging+=PrepareRow;choose.Click+=Choose;selectAll.Click+=SelectAll;clearAll.Click+=ClearAll;
        try
        {
            await ViewModel.DiscoverRecoveryRootsAsync(cancel.Token);if(ViewModel.IsStopping)return;
            var buttons=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};buttons.Children.Add(selectAll);buttons.Children.Add(clearAll);
            var panel=new StackPanel{Spacing=12};panel.Children.Add(buttons);panel.Children.Add(count);panel.Children.Add(list);panel.Children.Add(choose);panel.Children.Add(feedback);
            if(ViewModel.RecoveryRoots.Count==0){feedback.Message=global::HybridMount.Services.Localization.UiText.Text("Text_8D99B9A09314");feedback.IsOpen=true;}
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title=global::HybridMount.Services.Localization.UiText.Text("Text_0F25BC7459AB"),Content=panel,PrimaryButtonText=global::HybridMount.Services.Localization.UiText.Text("Text_DE1A9ED63D75"),CloseButtonText=global::HybridMount.Services.Localization.UiText.Text("Text_3FD47EDCE45B"),DefaultButton=ContentDialogButton.Close,IsPrimaryButtonEnabled=false};
            UpdateSelection();
            var selected=await global::HybridMount.Helpers.UI.Ui.ShowAsync(_recoveryDialog);selecting=false;
            if(selected!=ContentDialogResult.Primary||ViewModel.IsStopping)return;
            var captured=HybridMount.Models.Mounting.Recovery.OrphanedMountBatch.Capture(list.SelectedItems.OfType<OrphanedMountCandidate>());
            var details=string.Join("\n\n",captured.Select(root=>root.LocalPath+"\n"+RecoveryActionDescription(root)));
            if(captured.Any(x=>!x.ShellOnly))details+=global::HybridMount.Services.Localization.UiText.Text("Text_03790D861CB9");
            var confirmation=new TextBlock{Text=details,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true,MaxWidth=620};
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title=global::HybridMount.Services.Localization.UiText.Text("Text_776A4AD39EDC",captured.Length),Content=new ScrollViewer{Content=confirmation,MaxHeight=420},PrimaryButtonText=global::HybridMount.Services.Localization.UiText.Text("Text_C05166172020"),CloseButtonText=global::HybridMount.Services.Localization.UiText.Text("Text_2CD0F3BE8738"),DefaultButton=ContentDialogButton.Close};
            if(await global::HybridMount.Helpers.UI.Ui.ShowAsync(_recoveryDialog)!=ContentDialogResult.Primary||ViewModel.IsStopping)return;
            var progress=global::HybridMount.Helpers.UI.Ui.Configure(new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true,MaxWidth=620},("Text",global::HybridMount.Services.Localization.UiText.Message("Text_357694508CEF")));
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title=global::HybridMount.Services.Localization.UiText.Text("Text_0D2F94A171FB"),Content=new ScrollViewer{Content=progress,MaxHeight=420},CloseButtonText=global::HybridMount.Services.Localization.UiText.Text("Text_2CD0F3BE8738"),DefaultButton=ContentDialogButton.Close};
            var working=true;long lastProgress=0;
            void CancelClicked(ContentDialog _,ContentDialogButtonClickEventArgs click)
            {if(working){click.Cancel=true;cancel.Cancel();if(_recoveryDialog is {} dialog)global::HybridMount.Helpers.UI.Ui.Set(dialog,"CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_E8E08B0F61DD"));}}
            void ProgressClosing(ContentDialog _,ContentDialogClosingEventArgs closing)
            {if(working&&!_disposed&&!ViewModel.IsStopping){closing.Cancel=true;cancel.Cancel();if(_recoveryDialog is {} dialog)global::HybridMount.Helpers.UI.Ui.Set(dialog,"CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_E8E08B0F61DD"));}}
            _recoveryDialog.Closing+=ProgressClosing;_recoveryDialog.CloseButtonClick+=CancelClicked;var shown=global::HybridMount.Helpers.UI.Ui.ShowAsync(_recoveryDialog).AsTask();
            try
            {
                var result=await ViewModel.ClearRecoveryRootsAsync(captured,cancel.Token,line=>
                {
                    var now=System.Diagnostics.Stopwatch.GetTimestamp();if(now-Interlocked.Read(ref lastProgress)<System.Diagnostics.Stopwatch.Frequency/10&&line.Stage!="正在重新核对目录身份…")return;
                    Interlocked.Exchange(ref lastProgress,now);
                    App.Current.UiDispatcher.TryEnqueue(()=>{if(!_disposed&&!ViewModel.IsStopping&&working)progress.Text=$"{line.Index}/{line.Total}\n{line.LocalPath}\n{line.Stage}";});
                });
                if(!ViewModel.IsStopping)progress.Text=result.Detail;
            }
            catch(OperationCanceledException){if(!ViewModel.IsStopping){global::HybridMount.Helpers.UI.Ui.Set(progress,"Text",global::HybridMount.Services.Localization.UiText.Message("Text_1DB537BACF63"));ViewModel.Status=progress.Text;}}
            catch(Exception error){if(!ViewModel.IsStopping){progress.Text=error.Message;ViewModel.ReportError(error.Message);}}
            finally{working=false;if(!ViewModel.IsStopping&&_recoveryDialog is {} dialog)global::HybridMount.Helpers.UI.Ui.Set(dialog,"CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_3FD47EDCE45B"));}
            await shown;_recoveryDialog.CloseButtonClick-=CancelClicked;_recoveryDialog.Closing-=ProgressClosing;
        }
        finally
        {
            selecting=false;list.SelectionChanged-=SelectionChanged;list.ContainerContentChanging-=PrepareRow;choose.Click-=Choose;selectAll.Click-=SelectAll;clearAll.Click-=ClearAll;
            cancel.Cancel();_recoveryCancel=null;_recoveryDialog=null;
        }
    });
    /// <summary>确认逐项标明内容影响，不把入口清理与清空根目录混为同一操作。</summary>
    private static string RecoveryActionDescription(OrphanedMountCandidate root)=>root.OrphanedDirectory?global::HybridMount.Services.Localization.UiText.Text("Text_1AE41157C2AC"):root.ShellOnly?global::HybridMount.Services.Localization.UiText.Text("Text_2242EED5CFDF"):global::HybridMount.Services.Localization.UiText.Text("Text_2D711D125254");
    private Task RemoveAsync(Guid id)=>ViewModel.RunForMountAsync(id,async selected=>await ViewModel.RunDialogAsync(async()=>
    {
        var mount=selected.Definition;
        var unregistered=mount.RegistrationStage==MountRegistrationStage.NotRegistered;
        var message=global::HybridMount.Services.Localization.UiText.Text("Text_745490C041CD",mount.LocalPath);
        if(unregistered)message=global::HybridMount.Services.Localization.UiText.Text("Text_CAFDD55E0A9F",mount.LocalPath);
        if(mount.RemovalPending)message+=global::HybridMount.Services.Localization.UiText.Text("Text_C32110B1817C",selected.Detail,(mount.RemovalStage==MountRemovalStage.Unregistering?global::HybridMount.Services.Localization.UiText.Text("Text_883B44EB98B9"):global::HybridMount.Services.Localization.UiText.Text("Text_134E74105193")));
        var dialog=global::HybridMount.Helpers.UI.Ui.Watch(new ContentDialog{XamlRoot=XamlRoot,Title=global::HybridMount.Services.Localization.UiText.Text("Text_FEE3C5717BB4"),Content=message,PrimaryButtonText=mount.RemovalPending&&mount.RemovalStage==MountRemovalStage.Unregistering?global::HybridMount.Services.Localization.UiText.Text("Text_482B9676C943"):unregistered?global::HybridMount.Services.Localization.UiText.Text("Text_E410A42CC339"):global::HybridMount.Services.Localization.UiText.Text("Text_D2E33E1E22E6"),SecondaryButtonText=mount.RemovalPending&&mount.RemovalStage==MountRemovalStage.Unregistering?global::HybridMount.Services.Localization.UiText.Text("Text_482B9676C943"):global::HybridMount.Services.Localization.UiText.Text("Text_4CA35F63DD99"),CloseButtonText=global::HybridMount.Services.Localization.UiText.Text("Text_2CD0F3BE8738"),DefaultButton=ContentDialogButton.Close,
            IsPrimaryButtonEnabled=!mount.RemovalPending||mount.RemovalMode==MountRemovalMode.PreserveFiles,
            IsSecondaryButtonEnabled=!mount.RemovalPending||mount.RemovalMode==MountRemovalMode.ClearContents},"Title","PrimaryButtonText","SecondaryButtonText","CloseButtonText");
        var result=await global::HybridMount.Helpers.UI.Ui.ShowAsync(dialog);
        if(result==ContentDialogResult.None)return;
        if(result==ContentDialogResult.Secondary)
        {
            var confirm=global::HybridMount.Helpers.UI.Ui.Configure(new ContentDialog{XamlRoot=XamlRoot,DefaultButton=ContentDialogButton.Close},("Title",global::HybridMount.Services.Localization.UiText.Message("Text_35065CF67F8C")),("Content",global::HybridMount.Services.Localization.UiText.Message("Text_91D4BB7F12AD",mount.LocalPath)),("PrimaryButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_4CA35F63DD99")),("CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_2CD0F3BE8738")));
            if(await global::HybridMount.Helpers.UI.Ui.ShowAsync(confirm)!=ContentDialogResult.Primary)return;
        }
        ViewModel.QueueRemoval(mount.Id,result==ContentDialogResult.Secondary);
    }));
    /// <summary>真正释放才取消对话框和视图订阅；导航离页不停止后台挂载。</summary>
    public void Dispose()
    {_localization?.Dispose();_localization=null;
        if(_disposed)return;_disposed=true;Loaded-=PageLoaded;_viewport.Stop();_diagnosticDialog?.Hide();_recoveryCancel?.Cancel();_recoveryDialog?.Hide();_configurationDialog?.CancelForShutdown();_backupDialog?.CancelForShutdown();
        ViewModel.Refreshing-=CaptureScroll;ViewModel.Refreshed-=RestoreSelection;ViewModel.StateChanged-=UpdatePageState;ViewModel.Dispose();
    }
}
