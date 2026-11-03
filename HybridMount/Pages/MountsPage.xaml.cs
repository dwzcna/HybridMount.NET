using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using HybridMount.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace HybridMount.Pages;

/// <summary>挂载管理入口；连接和任务由应用拥有，结果在独立任务页展示。</summary>
public sealed partial class MountsPage : Page, IDisposable
{
    /// <summary>仅订阅挂载列表，不加载任务历史。</summary>
    public MountsViewModel ViewModel { get; }=new(App.Current.Mounts,App.Current.UiDispatcher,App.Current.Logger);
    private bool _loadingControls,_restoringSelection,_disposed;
    private MountBackupDialog? _backupDialog;
    private SqsSettingsDialog? _sqsDialog;
    private ScrollViewer? _listScroll;
    private double _listOffset;
    private long _refreshVersion;

    /// <summary>UI 线程创建；选择按挂载 ID 保留，后台快照不覆盖尚未保存的检测间隔。</summary>
    public MountsPage()
    {
        InitializeComponent();
        MountList.SelectionChanged+=(_,_)=>
        {
            if(_restoringSelection)return;
            ViewModel.SelectedMountId=(MountList.SelectedItem as MountSnapshot)?.Definition.Id;
            DetailsScroll.ChangeView(null,0,null,true);UpdateDetails();_=ViewModel.ReadCapacityAsync(ViewModel.SelectedMountId);
        };
        ViewModel.Refreshing+=CaptureScroll;ViewModel.Refreshed+=RestoreSelection;ViewModel.StateChanged+=UpdateDetails;UpdateDetails();
    }
    /// <summary>窄窗口上下排列，宽窗口并排；按钮和文字在详情内纵向排列，无横向操作条。</summary>
    private void WorkspaceSizeChanged(object sender,SizeChangedEventArgs args)
    {
        var narrow=args.NewSize.Width<820;
        ListColumn.Width=narrow?new(1,GridUnitType.Star):new GridLength(Math.Clamp(args.NewSize.Width*0.34,240,360));
        DetailColumn.Width=narrow?new(0):new(1,GridUnitType.Star);
        ListRow.Height=narrow?new(2,GridUnitType.Star):new(1,GridUnitType.Star);
        DetailRow.Height=narrow?new(3,GridUnitType.Star):new(0);
        Grid.SetColumn(DetailsPanel,narrow?0:1);Grid.SetRow(DetailsPanel,narrow?1:0);
    }
    private static T? FindVisual<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent,i);
            if(child is T result)return result;
            if(FindVisual<T>(child) is {} nested)return nested;
        }
        return null;
    }
    private void CaptureScroll(){_restoringSelection=true;_listScroll??=FindVisual<ScrollViewer>(MountList);_listOffset=_listScroll?.VerticalOffset??0;}
    private void RestoreSelection()
    {
        if(_disposed)return;
        _restoringSelection=true;
        try{MountList.SelectedItem=ViewModel.Mounts.FirstOrDefault(mount=>mount.Definition.Id==ViewModel.SelectedMountId);}
        finally{_restoringSelection=false;}
        UpdateDetails();var revision=++_refreshVersion;var offset=_listOffset;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>
        {if(!_disposed&&revision==_refreshVersion)_listScroll?.ChangeView(null,offset,null,true);});
    }
    /// <summary>只更新显示；所有开关写回均由加载保护与捕获的挂载 ID 隔离。</summary>
    private void UpdateDetails()
    {
        if(_disposed)return;var mount=ViewModel.Selected;
        var id=mount?.Definition.Id;var actions=ViewModel.Actions;
        EmptySelection.Visibility=mount is null?Visibility.Visible:Visibility.Collapsed;DetailsScroll.Visibility=mount is null?Visibility.Collapsed:Visibility.Visible;
        ConnectButton.Content=actions.ConnectText;ConnectButton.IsEnabled=actions.CanConnect;
        CancelConnectionButton.Visibility=mount?.IsConnecting==true?Visibility.Visible:Visibility.Collapsed;
        CancelConnectionButton.IsEnabled=actions.CanCancelConnection&&id is {} connecting&&!ViewModel.IsCanceling(connecting);
        DisconnectButton.Visibility=mount?.IsConnecting==true?Visibility.Collapsed:Visibility.Visible;DisconnectButton.IsEnabled=actions.CanDisconnect;
        OpenButton.IsEnabled=actions.CanOpen;CredentialsButton.IsEnabled=actions.CanEdit&&!ViewModel.DialogBusy;
        ScanButton.IsEnabled=actions.CanRunConnected;RefreshButton.IsEnabled=actions.CanRunConnected;
        KeepButton.IsEnabled=actions.CanRunConnected;ReleaseButton.IsEnabled=actions.CanRunConnected;
        RemoveButton.IsEnabled=actions.CanRemove&&!ViewModel.DialogBusy;RemoveButton.Content=actions.RemoveText;
        ThumbnailsToggle.IsEnabled=actions.CanEdit;RemoteAutoToggle.IsEnabled=IncrementalInterval.IsEnabled=FullInterval.IsEnabled=SaveIntervalsButton.IsEnabled=actions.CanEdit;
        AddButton.IsEnabled=ExportButton.IsEnabled=ImportButton.IsEnabled=!ViewModel.DialogBusy&&!ViewModel.IsStopping;
        if(mount is null)return;
        var definition=mount.Definition;var source=definition.Source;
        DetailName.Text=definition.Name;DetailStatus.Text=mount.Status+(definition.RemovalPending?" · 移除模式已固定":"");DetailPath.Text=definition.LocalPath;
        DetailSource.Text=mount.SourceDisplay+(source is null?"":"\n"+source.Endpoint)+(source?.Kind==FileSourceKind.S3?$"\n桶：{source.Bucket}\n前缀：{(source.Prefix.Length==0?"（根）":source.Prefix)}\n区域：{source.Region} · {(source.PathStyle?"路径寻址":"虚拟主机寻址")}":"");
        CapacityDescription.Text=mount.CapacityDisplay;
        DetailSummary.Text=mount.Detail.Length>180?mount.Detail[..180]+"…":mount.Detail;
        DetailDiagnostic.Text=mount.Detail;DiagnosticExpander.Visibility=mount.Detail.Length>0?Visibility.Visible:Visibility.Collapsed;
        UploadDescription.Text=mount.AutoUploadDisplay;ThumbnailDescription.Text=mount.ThumbnailDisplay;
        DetectionDescription.Text=mount.RemoteDetectionDisplay;SqsDescription.Text=mount.SqsDisplay;
        RemoteControls.Visibility=source?.Kind is FileSourceKind.WebDav or FileSourceKind.S3?Visibility.Visible:Visibility.Collapsed;
        IncrementalInterval.Visibility=source?.Kind==FileSourceKind.WebDav?Visibility.Visible:Visibility.Collapsed;
        SqsSettingsButton.Visibility=source?.Kind==FileSourceKind.S3?Visibility.Visible:Visibility.Collapsed;SqsSettingsButton.IsEnabled=actions.CanEdit&&!ViewModel.DialogBusy;
        RemovalNotice.Visibility=definition.RemovalPending?Visibility.Visible:Visibility.Collapsed;
        RemovalNotice.Text=definition.RemovalPending?$"上次移除未完成，请继续“{actions.RemoveText}”。已处理的步骤不会回滚。":"";
        var draft=ViewModel.Draft(definition);
        _loadingControls=true;
        try
        {
            ThumbnailsToggle.IsOn=ViewModel.ThumbnailDraft(definition);
            RemoteAutoToggle.IsOn=draft.Enabled;IncrementalInterval.Value=draft.Incremental;FullInterval.Value=draft.Full;
        }
        finally{_loadingControls=false;}
    }
    private void CaptureDraft()
    {if(ViewModel.Selected is {} mount)ViewModel.UpdateDraft(mount.Definition.Id,RemoteAutoToggle.IsOn,IncrementalInterval.Value,FullInterval.Value);}
    private void IntervalChanged(NumberBox sender,NumberBoxValueChangedEventArgs args){if(!_loadingControls)CaptureDraft();}
    private async void RemoteAutoChanged(object sender,RoutedEventArgs args){if(!_loadingControls){CaptureDraft();await ViewModel.SaveSelectedDetectionAsync();}}
    private async void SaveDetectionIntervals(object sender,RoutedEventArgs args){if(!_loadingControls){CaptureDraft();await ViewModel.SaveSelectedDetectionAsync();}}
    private async void ThumbnailsChanged(object sender,RoutedEventArgs args){if(!_loadingControls)await ViewModel.SetSelectedThumbnailAsync(ThumbnailsToggle.IsOn);}
    private async void ReviewUploadChanges(object sender,RoutedEventArgs args)=>await ViewModel.ScanChangesAsync();
    private async void CreateWebDavMount(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        var dialog=new NetworkMountDialog{XamlRoot=XamlRoot};await dialog.ShowAsync();
        if(dialog.CreatedMount is not null) { ViewModel.SelectedMountId=dialog.CreatedMount.Id;RestoreSelection();ViewModel.Status=dialog.ResultMessage; }
    });
    private async void CreateS3Mount(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>
    {
        var dialog=new NetworkMountDialog(FileSourceKind.S3){XamlRoot=XamlRoot};await dialog.ShowAsync();
        if(dialog.CreatedMount is not null){ViewModel.SelectedMountId=dialog.CreatedMount.Id;RestoreSelection();ViewModel.Status=dialog.ResultMessage;}
    });
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
    private async void Connect(object sender,RoutedEventArgs args)=>await ViewModel.ConnectAsync();
    /// <summary>连接期间取消入口独立于该挂载的连接忙碌标志，等待完整断开后才允许重连。</summary>
    private async void CancelConnection(object sender,RoutedEventArgs args)=>await ViewModel.CancelConnectionAsync();
    private async void UpdateCredentials(object sender,RoutedEventArgs args)=>await ViewModel.RunSelectedAsync(async selected=>await ViewModel.RunDialogAsync(async()=>
    {
        var mount=selected.Definition;if(mount.Source is null)throw new InvalidOperationException("旧版模拟源仅支持迁出。");
        var dialog=new NetworkMountDialog(mount.Source.Kind,mount){XamlRoot=XamlRoot};await dialog.ShowAsync();ViewModel.Status=dialog.ResultMessage;
    }));
    private async void SqsSettings(object sender,RoutedEventArgs args)=>await ViewModel.RunSelectedAsync(async mount=>await ViewModel.RunDialogAsync(async()=>
    {
        try{_sqsDialog=new SqsSettingsDialog(mount.Definition){XamlRoot=XamlRoot};await _sqsDialog.ShowAsync();}
        finally{_sqsDialog=null;}
    }));
    private async void Disconnect(object sender,RoutedEventArgs args)=>await ViewModel.DisconnectAsync();
    private async void OpenFolder(object sender,RoutedEventArgs args)=>await ViewModel.RunDialogAsync(async()=>{var folder=await StorageFolder.GetFolderFromPathAsync(ViewModel.RequireSelected().Definition.LocalPath);await Windows.System.Launcher.LaunchFolderAsync(folder);});
    private void Queue(string operation)=>ViewModel.QueueSelected(operation);
    private void RefreshMount(object sender,RoutedEventArgs args)=>Queue("refresh");
    private void Keep(object sender,RoutedEventArgs args)=>Queue("keepOnDevice");
    private void Release(object sender,RoutedEventArgs args)=>Queue("freeSpace");
    private async void Remove(object sender,RoutedEventArgs args)=>await ViewModel.RunSelectedAsync(async selected=>await ViewModel.RunDialogAsync(async()=>
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
    /// <summary>窗口释放时解除列表订阅，不断开挂载。</summary>
    public void Dispose() { if(_disposed)return;_disposed=true;_sqsDialog?.CancelForShutdown();_backupDialog?.CancelForShutdown();ViewModel.Refreshing-=CaptureScroll;ViewModel.Refreshed-=RestoreSelection;ViewModel.StateChanged-=UpdateDetails;ViewModel.Dispose(); }
}
