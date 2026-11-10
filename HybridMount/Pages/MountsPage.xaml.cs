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
                case "open":await ViewModel.RunDialogAsync(async()=>{if(ViewModel.FindMount(args.MountId) is not {} mount)return;var folder=await StorageFolder.GetFolderFromPathAsync(mount.Definition.LocalPath);if(!ViewModel.IsStopping)await global::Windows.System.Launcher.LaunchFolderAsync(folder);});break;
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
    private Task ShowConfigurationAsync(FileSourceKind kind,MountDefinition? existing=null,bool httpsOnly=false)=>ViewModel.RunDialogAsync(async()=>
    {
        try
        {
            ContentDialog dialog=kind switch
            {
                FileSourceKind.WebDav=>new WebDAVConfigurationContentDialog(ViewModel,existing,httpsOnly),
                FileSourceKind.S3=>new S3ConfigurationContentDialog(ViewModel,existing),
                FileSourceKind.Synology=>new SynologyConfigurationContentDialog(ViewModel,existing),
                _=>throw new NotSupportedException("此协议尚未提供配置窗口。")
            };
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
    private async void CreateWebDavHttpsMount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.WebDav,httpsOnly:true);
    private async void CreateS3Mount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.S3);
    private async void CreateSynologyMount(object sender,RoutedEventArgs args)=>await ShowConfigurationAsync(FileSourceKind.Synology);
    /// <summary>导出对话框默认选择全部网络挂载；真正的凭据读取与密文写入由应用服务执行。</summary>
    private async void ExportConfigurations(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        try { _backupDialog=new MountBackupDialog{XamlRoot=XamlRoot};await _backupDialog.ShowAsync();if(_backupDialog.ResultMessage.Length>0)ViewModel.Status=_backupDialog.ResultMessage; }
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
            _backupDialog=new MountBackupDialog(file.Path){XamlRoot=XamlRoot};await _backupDialog.ShowAsync();
            if(_backupDialog.ImportedIds.Count>0){ViewModel.SelectedMountId=_backupDialog.ImportedIds[^1];RestoreSelection();}
            if(_backupDialog.ResultMessage.Length>0)ViewModel.Status=_backupDialog.ResultMessage;
        }
        finally { _backupDialog=null; }
    });
    /// <summary>多选只捕获快照；确认后应用串行处理，取消时对话框等待当前项及临时连接收尾。</summary>
    private async void RecoverOrphanedMounts(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        using var cancel=new CancellationTokenSource();_recoveryCancel=cancel;
        var list=new ListView{ItemsSource=ViewModel.RecoveryRoots,DisplayMemberPath="Display",SelectionMode=ListViewSelectionMode.Multiple,IsMultiSelectCheckBoxEnabled=true,MinWidth=360,MaxWidth=620,MaxHeight=320};
        var feedback=new InfoBar{IsClosable=true,Severity=InfoBarSeverity.Informational,IsOpen=false};
        var count=new TextBlock();var choose=new Button{Content="选择旧挂载目录"};
        var selectAll=new Button{Content="全选可处理项"};var clearAll=new Button{Content="取消全选"};
        var choosing=false;var changing=false;var selecting=true;
        void UpdateSelection()
        {
            if(_disposed||ViewModel.IsStopping||!selecting)return;
            if(!changing)
            {
                changing=true;try{foreach(var invalid in list.SelectedItems.OfType<OrphanedMountCandidate>().Where(x=>!x.CanRemove).ToArray())list.SelectedItems.Remove(invalid);}finally{changing=false;}
            }
            count.Text=$"已选择 {list.SelectedItems.Count} 项";
            if(_recoveryDialog is {} dialog){dialog.IsPrimaryButtonEnabled=!choosing&&list.SelectedItems.Count>0;dialog.PrimaryButtonText=$"处理所选（{list.SelectedItems.Count}）";}
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
            if(ViewModel.RecoveryRoots.Count==0){feedback.Message="未发现可核对的遗留挂载，可选择旧挂载目录进行核验。";feedback.IsOpen=true;}
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title="遗留挂载",Content=panel,PrimaryButtonText="处理所选",CloseButtonText="关闭",DefaultButton=ContentDialogButton.Close,IsPrimaryButtonEnabled=false};
            UpdateSelection();
            var selected=await _recoveryDialog.ShowAsync();selecting=false;
            if(selected!=ContentDialogResult.Primary||ViewModel.IsStopping)return;
            var captured=HybridMount.Models.Mounting.Recovery.OrphanedMountBatch.Capture(list.SelectedItems.OfType<OrphanedMountCandidate>());
            var details=string.Join("\n\n",captured.Select(root=>root.LocalPath+"\n"+RecoveryActionDescription(root)));
            if(captured.Any(x=>!x.ShellOnly))details+="\n\n所列清空项目的全部本地内容（包括本地修改和新建文件）将永久删除，已删除内容不会回滚。保留空根目录及远端内容。";
            var confirmation=new TextBlock{Text=details,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true,MaxWidth=620};
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title=$"确认处理 {captured.Length} 个遗留项目",Content=new ScrollViewer{Content=confirmation,MaxHeight=420},PrimaryButtonText="确认处理",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
            if(await _recoveryDialog.ShowAsync()!=ContentDialogResult.Primary||ViewModel.IsStopping)return;
            var progress=new TextBlock{Text="正在重新核对目录身份…",TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true,MaxWidth=620};
            _recoveryDialog=new(){XamlRoot=XamlRoot,Title="处理遗留挂载",Content=new ScrollViewer{Content=progress,MaxHeight=420},CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
            var working=true;long lastProgress=0;
            void CancelClicked(ContentDialog _,ContentDialogButtonClickEventArgs click)
            {if(working){click.Cancel=true;cancel.Cancel();if(_recoveryDialog is {} dialog)dialog.CloseButtonText="正在取消…";}}
            void ProgressClosing(ContentDialog _,ContentDialogClosingEventArgs closing)
            {if(working&&!_disposed&&!ViewModel.IsStopping){closing.Cancel=true;cancel.Cancel();if(_recoveryDialog is {} dialog)dialog.CloseButtonText="正在取消…";}}
            _recoveryDialog.Closing+=ProgressClosing;_recoveryDialog.CloseButtonClick+=CancelClicked;var shown=_recoveryDialog.ShowAsync().AsTask();
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
            catch(OperationCanceledException){if(!ViewModel.IsStopping){progress.Text="已取消，已完成的删除不回滚。";ViewModel.Status=progress.Text;}}
            catch(Exception error){if(!ViewModel.IsStopping){progress.Text=error.Message;ViewModel.Status=error.Message;}}
            finally{working=false;if(!ViewModel.IsStopping&&_recoveryDialog is {} dialog)dialog.CloseButtonText="关闭";}
            await shown;_recoveryDialog.CloseButtonClick-=CancelClicked;_recoveryDialog.Closing-=ProgressClosing;
        }
        finally
        {
            selecting=false;list.SelectionChanged-=SelectionChanged;list.ContainerContentChanging-=PrepareRow;choose.Click-=Choose;selectAll.Click-=SelectAll;clearAll.Click-=ClearAll;
            cancel.Cancel();_recoveryCancel=null;_recoveryDialog=null;
        }
    });
    /// <summary>确认逐项标明内容影响，不把入口清理与清空根目录混为同一操作。</summary>
    private static string RecoveryActionDescription(OrphanedMountCandidate root)=>root.OrphanedDirectory?"原提供程序归属无法核验。临时本地接管并永久删除全部本地内容，随后注销临时根，保留空根目录。":root.ShellOnly?"仅移除当前用户的 Explorer 遗留入口，保留目录和全部文件。":"清空全部本地内容后离线注销，保留空根目录及远端内容。";
    private Task RemoveAsync(Guid id)=>ViewModel.RunForMountAsync(id,async selected=>await ViewModel.RunDialogAsync(async()=>
    {
        var mount=selected.Definition;
        var unregistered=mount.RegistrationStage==MountRegistrationStage.NotRegistered;
        var message=mount.LocalPath+"\n\n下载并移除：完整下载后保留为普通本地目录。\n\n清空并移除：永久删除目录内全部内容，包括本地修改和新建文件，保留空根目录；不删除文件源内容。";
        if(unregistered)message=mount.LocalPath+"\n\n移除挂载：保留本地文件，不连接远端。\n\n清空并移除：永久删除根目录内全部本地内容，保留空根目录及远端数据。";
        if(mount.RemovalPending)message+="\n\n"+selected.Detail+(mount.RemovalStage==MountRemovalStage.Unregistering?"\n内容处理已经完成，只继续注销和 Explorer 入口收尾。":"\n请继续相同的移除方式。");
        var dialog=new ContentDialog{XamlRoot=XamlRoot,Title="移除挂载",Content=message,PrimaryButtonText=mount.RemovalPending&&mount.RemovalStage==MountRemovalStage.Unregistering?"继续移除收尾":unregistered?"移除挂载（保留本地文件）":"下载并移除",SecondaryButtonText=mount.RemovalPending&&mount.RemovalStage==MountRemovalStage.Unregistering?"继续移除收尾":"清空并移除",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close,
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
