using HybridMount.Helpers;
using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using HybridMount.Views;
using HybridMount.Views.Mounting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
namespace HybridMount.Pages;

/// <summary>挂载卡片列表；页面只适配选择、滚动、对话框及Shell打开，业务命令由唯一模型拥有。</summary>
public sealed partial class MountsPage : Page,IDisposable
{
    public MountsViewModel ViewModel {get;}=new(App.Current.Mounts,App.Current.UiDispatcher,App.Current.Logger);
    private bool _restoringSelection,_disposed;
    private MountBackupDialog? _backupDialog;
    private IMountConfigurationDialog? _configurationDialog;
    private ContentDialog? _diagnosticDialog,_recoveryDialog;
    private CancellationTokenSource? _recoveryCancel;
    private readonly PageScrollBookmark _viewport=new();
    public MountsPage()
    {
        try{InitializeComponent();NavigationCacheMode=NavigationCacheMode.Required;}catch{ViewModel.Dispose();throw;}
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
                case "open":await ViewModel.RunDialogAsync(async()=>{if(ViewModel.FindMount(args.MountId) is not {} mount)return;var folder=await StorageFolder.GetFolderFromPathAsync(mount.Definition.LocalPath);if(!ViewModel.IsStopping)await Windows.System.Launcher.LaunchFolderAsync(folder);});break;
            }
        }
        catch(Exception error){if(!ViewModel.IsStopping)ViewModel.Status="挂载操作失败："+error.Message;}
    }
    private Task ShowDiagnosticsAsync(Guid id)=>ViewModel.RunDialogAsync(async()=>
    {
        if(ViewModel.FindMount(id) is not {} mount)return;
        var detail=await ViewModel.DiagnosticAsync(id);if(_disposed||ViewModel.IsStopping)return;
        var text=new TextBlock{Text=detail,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
        try
        {
            _diagnosticDialog=new(){XamlRoot=XamlRoot,Title="完整诊断 · "+mount.Definition.Name,CloseButtonText="关闭",
                Content=new ScrollViewer{Content=text,MinWidth=340,MaxWidth=640,MaxHeight=520,HorizontalScrollMode=ScrollMode.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled}};
            await _diagnosticDialog.ShowAsync();
        }
        finally{_diagnosticDialog=null;}
    });
    private Task EditSettingsAsync(Guid id)=>ViewModel.FindMount(id) is {} mount?ShowConfigurationAsync(mount.Definition.Source!.Kind,mount.Definition):Task.CompletedTask;
    /// <summary>新增和编辑都使用协议专属 XAML；SQS 选项在 S3 配置内部折叠，不再弹出其他配置窗口。</summary>
    private Task ShowConfigurationAsync(FileSourceKind kind,MountDefinition? existing=null)=>ViewModel.RunDialogAsync(async()=>
    {
        try
        {
            ContentDialog dialog=kind==FileSourceKind.WebDav?new WebDAVConfigurationContentDialog(ViewModel,existing):new S3ConfigurationContentDialog(ViewModel,existing);
            _configurationDialog=(IMountConfigurationDialog)dialog;dialog.XamlRoot=XamlRoot;await dialog.ShowAsync();
            if(!ViewModel.IsStopping)
            {
                if(existing is null&&_configurationDialog.CreatedMount is {} created){ViewModel.SelectedMountId=created.Id;RestoreSelection();}
                if(_configurationDialog.ResultMessage.Length>0)ViewModel.Status=_configurationDialog.ResultMessage;
            }
        }
        finally{_configurationDialog?.Dispose();_configurationDialog=null;}
    });
    private async void CreateWebDavMount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.WebDav);
    private async void CreateS3Mount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.S3);
    /// <summary>导出对话框默认选择全部网络挂载；真正的凭据读取与密文写入由应用服务执行。</summary>
    private async void ExportConfigurations(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        try { _backupDialog=new MountBackupDialog{XamlRoot=XamlRoot};await _backupDialog.ShowAsync();if(_backupDialog.ResultMessage.Length>0)ViewModel.Status=_backupDialog.ResultMessage; }
        finally { _backupDialog=null; }
    });
    /// <summary>文件选择只接受加密配置；解密通过后先预览，保存不会自动连接。</summary>
    private async void ImportConfigurations(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        var picker=new Windows.Storage.Pickers.FileOpenPicker();picker.FileTypeFilter.Add(".hmconfig");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
        var file=await picker.PickSingleFileAsync();if(file is null||App.Current.IsExiting)return;
        try
        {
            _backupDialog=new MountBackupDialog(file.Path){XamlRoot=XamlRoot};await _backupDialog.ShowAsync();
            if(_backupDialog.ImportedIds.Count>0){ViewModel.SelectedMountId=_backupDialog.ImportedIds[^1];RestoreSelection();}
            if(_backupDialog.ResultMessage.Length>0)ViewModel.Status=_backupDialog.ResultMessage;
        }
        finally { _backupDialog=null; }
    });
    /// <summary>页面只适配列表、目录选择和两次确认；清理及取消由应用拥有，关闭页面不会丢失已完成阶段。</summary>
    private async void RecoverOrphanedMounts(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        using var cancel=new CancellationTokenSource();_recoveryCancel=cancel;
        try
        {
            await ViewModel.DiscoverRecoveryRootsAsync(cancel.Token);if(ViewModel.IsStopping)return;
            var list=new ListView{ItemsSource=ViewModel.RecoveryRoots,DisplayMemberPath="Display",SelectionMode=ListViewSelectionMode.Single,MinWidth=360,MaxWidth=620,MaxHeight=360};
            var feedback=new TextBlock{Text=ViewModel.RecoveryRoots.Count==0?"未发现可核对的遗留挂载。":"",TextWrapping=TextWrapping.Wrap};
            var choose=new Button{Content="选择旧挂载目录"};
            var panel=new StackPanel{Spacing=12};panel.Children.Add(list);panel.Children.Add(choose);panel.Children.Add(feedback);
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title="遗留挂载",Content=panel,PrimaryButtonText="清空并注销",CloseButtonText="关闭",DefaultButton=ContentDialogButton.Close,IsPrimaryButtonEnabled=false};
            list.SelectionChanged+=(_,_)=>{ViewModel.SelectedRecoveryRoot=list.SelectedItem as OrphanedMountCandidate;if(_recoveryDialog is {} dialog)dialog.IsPrimaryButtonEnabled=ViewModel.SelectedRecoveryRoot?.CanRemove==true;};
            choose.Click+=async(_,_)=>
            {
                choose.IsEnabled=false;
                try
                {
                    var picker=new Windows.Storage.Pickers.FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
                    var folder=await picker.PickSingleFolderAsync();if(folder is null||cancel.IsCancellationRequested||ViewModel.IsStopping)return;
                    await ViewModel.AddRecoveryRootAsync(folder.Path,cancel.Token);if(!ViewModel.IsStopping){list.SelectedItem=ViewModel.SelectedRecoveryRoot;feedback.Text="";}
                }
                catch(Exception error){if(!ViewModel.IsStopping)feedback.Text=error.Message;}
                finally{if(!ViewModel.IsStopping)choose.IsEnabled=true;}
            };
            var selected=await _recoveryDialog.ShowAsync();if(selected!=ContentDialogResult.Primary||ViewModel.SelectedRecoveryRoot is not {} root||ViewModel.IsStopping)return;
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title="确认清空遗留挂载",Content=root.LocalPath+"\n\n全部本地内容（包括本地修改和新建文件）将永久删除，已删内容不会回滚。离线注销同步根，保留空目录及远端内容。无法从旧根恢复服务器配置或凭据。",PrimaryButtonText="清空并注销",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
            if(await _recoveryDialog.ShowAsync()!=ContentDialogResult.Primary||ViewModel.IsStopping)return;
            var progress=new TextBlock{Text="正在重新核对根身份…",TextWrapping=TextWrapping.Wrap,MaxWidth=620};
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title="处理遗留挂载",Content=progress,CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
            _recoveryDialog.CloseButtonClick+=(_,_)=>cancel.Cancel();var shown=_recoveryDialog.ShowAsync().AsTask();
            try
            {
                var result=await ViewModel.ClearRecoveryRootAsync(root,cancel.Token,line=>App.Current.UiDispatcher.TryEnqueue(()=>{if(!_disposed&&!ViewModel.IsStopping)progress.Text=line;}));
                if(!ViewModel.IsStopping){progress.Text=result.Completed?"已清空并注销，空目录已保留。":result.Stage+"\n"+string.Join("\n",result.Errors);_recoveryDialog.CloseButtonText="关闭";}
            }
            catch(OperationCanceledException){if(!ViewModel.IsStopping)ViewModel.Status="遗留挂载处理已取消，已完成的删除不回滚，可再次显式核对并继续。";}
            catch(Exception error){if(!ViewModel.IsStopping){ViewModel.Status=error.Message;progress.Text=error.Message;_recoveryDialog.CloseButtonText="关闭";}}
            await shown;
        }
        finally{_recoveryDialog=null;_recoveryCancel=null;}
    });
    private Task RemoveAsync(Guid id)=>ViewModel.RunForMountAsync(id,async selected=>await ViewModel.RunDialogAsync(async()=>
    {
        var mount=selected.Definition;
        var unregistered=mount.RegistrationStage==MountRegistrationStage.NotRegistered;
        var message=mount.LocalPath+"\n\n下载并移除：完整下载后保留为普通本地目录。\n\n清空并移除：永久删除目录内全部内容，包括本地修改和新建文件，保留空根目录；不删除文件源内容。";
        if(unregistered)message=mount.LocalPath+"\n\n移除挂载：保留本地文件，不连接远端。\n\n清空并移除：永久删除根目录内全部本地内容，保留空根目录及远端数据。";
        if(mount.RemovalPending)message+="\n\n上次移除尚未完成，请继续相同的移除方式。";
        var dialog=new ContentDialog{XamlRoot=XamlRoot,Title="移除挂载",Content=message,PrimaryButtonText=unregistered?"移除挂载（保留本地文件）":"下载并移除",SecondaryButtonText="清空并移除",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close,
            IsPrimaryButtonEnabled=!mount.RemovalPending||mount.RemovalMode==MountRemovalMode.PreserveFiles,
            IsSecondaryButtonEnabled=!mount.RemovalPending||mount.RemovalMode==MountRemovalMode.ClearContents};
        var result=await dialog.ShowAsync();
        if(result==ContentDialogResult.None)return;
        if(result==ContentDialogResult.Secondary)
        {
            var confirm=new ContentDialog{XamlRoot=XamlRoot,Title="确认清空并移除",Content=mount.LocalPath+"\n\n目录内全部本地内容（包括修改和新建文件）将被永久删除。保留空根目录及远端数据，已删除内容不会回滚。",
                PrimaryButtonText="清空并移除",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
            if(await confirm.ShowAsync()!=ContentDialogResult.Primary)return;
        }
        ViewModel.QueueRemoval(mount.Id,result==ContentDialogResult.Secondary);
    }));
    /// <summary>真正释放才取消对话框和视图订阅；导航离页不停止后台挂载。</summary>
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;Loaded-=PageLoaded;_viewport.Stop();_diagnosticDialog?.Hide();_recoveryCancel?.Cancel();_recoveryDialog?.Hide();_configurationDialog?.CancelForShutdown();_backupDialog?.CancelForShutdown();
        ViewModel.Refreshing-=CaptureScroll;ViewModel.Refreshed-=RestoreSelection;ViewModel.StateChanged-=UpdatePageState;ViewModel.Dispose();
    }
}
