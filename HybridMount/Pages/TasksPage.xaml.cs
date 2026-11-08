using HybridMount.Helpers;
using HybridMount.Views;
using Microsoft.UI.Xaml.Navigation;
using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Pages;

/// <summary>独立任务页；后台任务由应用托管，页面仅保留选择及展示结果。</summary>
public sealed partial class TasksPage : Page, IDisposable
{
    /// <summary>UI 线程上的任务快照集合。</summary>
    public TasksViewModel ViewModel { get; }=new(App.Current.Mounts,App.Current.UiDispatcher,App.Current.TaskFocus);
    private bool _restoring,_disposed;
    private readonly PageScrollBookmark _viewport=new();
    /// <summary>初始化页面并按任务 ID 恢复刷新后的选择。</summary>
    public TasksPage()
    {
        try{InitializeComponent();NavigationCacheMode=NavigationCacheMode.Required;}catch{ViewModel.Dispose();throw;}
        JobList.SelectionChanged+=(_,_)=>{if(!_restoring&&JobList.SelectedItem is MountOperationSnapshot job)ViewModel.SelectedJobId=job.Id;};
        ViewModel.Refreshing+=CaptureScroll;ViewModel.Refreshed+=RestoreSelection;Loaded+=PageLoaded;
    }
    private void PageLoaded(object sender,RoutedEventArgs args)=>_viewport.Restore(JobList);
    protected override void OnNavigatedTo(NavigationEventArgs args){base.OnNavigatedTo(args);_viewport.Enter(JobList);RestoreSelection();}
    protected override void OnNavigatedFrom(NavigationEventArgs args){ViewModel.EndDialog();_viewport.Leave(JobList);base.OnNavigatedFrom(args);}
    private void CaptureScroll(){_restoring=true;_viewport.Capture(JobList);}
    private void RestoreSelection()
    {
        if(_disposed)return;_restoring=true;
        try{JobList.SelectedItem=ViewModel.Selected;}finally{_restoring=false;}
        _viewport.Restore(JobList);
    }
    /// <summary>窄窗口将历史清理放在下一行，避免新增按钮被裁切；不影响任务列表状态。</summary>
    private void ToolbarSizeChanged(object sender,SizeChangedEventArgs args)
    {if(_disposed||HistoryToolbar is null)return;var narrow=args.NewSize.Width<1150;Grid.SetRow(HistoryToolbar,narrow?1:0);Grid.SetColumn(HistoryToolbar,narrow?0:1);Grid.SetColumnSpan(HistoryToolbar,narrow?2:1);Toolbar.RowSpacing=narrow?8:0;}
    private void ClearIssueFilter(object sender,RoutedEventArgs args)=>ViewModel.ClearIssueFilter();
    /// <summary>只读核对改名事务，界面不直接发送远端改名。</summary>
    private void RecheckRename(object sender,RoutedEventArgs args)=>ViewModel.RecheckSelected();
    /// <summary>文件存在时由Shell选中；已移除文件打开最近仍存在的父目录，不连接服务或自动上传。</summary>
    private async void OpenLocation(object sender,RoutedEventArgs args)
    {
        if(ViewModel.SelectedLocation() is not {} path)return;
        try
        {
            var folder=Path.GetDirectoryName(path)!;while(!Directory.Exists(folder)&&Path.GetDirectoryName(folder) is {} parent)folder=parent;
            var storage=await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder);if(_disposed||App.Current.IsExiting)return;
            var options=new Windows.System.FolderLauncherOptions();
            if(File.Exists(path))options.ItemsToSelect.Add(await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
            else if(Directory.Exists(path))options.ItemsToSelect.Add(await Windows.Storage.StorageFolder.GetFolderFromPathAsync(path));
            if(!_disposed&&!App.Current.IsExiting)await Windows.System.Launcher.LaunchFolderAsync(storage,options);
        }
        catch(Exception error){if(!_disposed)ViewModel.Status=error.Message;}
    }
    /// <summary>比较及取舍由对应ViewModel持有，页面只显示模态操作界面。</summary>
    private async void ResolveConflict(object sender,RoutedEventArgs args)
    {
        var owned=false;
        try{owned=await ViewModel.BeginConflictAsync();if(!owned)return;await new ConflictResolutionContentDialog(ViewModel){XamlRoot=XamlRoot}.ShowAsync();}
        catch(Exception error){if(!_disposed)ViewModel.Status=error.Message;}
        finally{if(owned)ViewModel.EndDialog();}
    }
    private void CancelJob(object sender,RoutedEventArgs args)=>ViewModel.CancelSelected();
    private void RetryUpload(object sender,RoutedEventArgs args)=>ViewModel.RetrySelected();
    /// <summary>以新相对路径安全创建远端副本，不改变原始冲突文件或远端旧项。</summary>
    private async void SaveUploadCopy(object sender,RoutedEventArgs args)
    {
        if(!ViewModel.BeginCopy())return;
        try
        {
            var field=new TextBox{Header="远端新文件相对路径",Text=ViewModel.CopyPath,MinWidth=400};
            field.TextChanged+=(_,_)=>ViewModel.CopyPath=field.Text;
            var content=new StackPanel{Spacing=12,MaxWidth=520};content.Children.Add(new TextBlock{Text=ViewModel.CopyContentNotice,TextWrapping=TextWrapping.Wrap});content.Children.Add(field);
            var dialog=new ContentDialog{XamlRoot=XamlRoot,Title="另存远端副本",Content=content,PrimaryButtonText="上传副本",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            ViewModel.SubmitCopy();RestoreSelection();
        }
        catch(Exception error){ViewModel.Status=error.Message;}
        finally{ViewModel.EndDialog();}
    }
    /// <summary>仅清理已完成历史，实际操作和异常处理由 ViewModel 及应用所有者执行。</summary>
    private async void ClearCompleted(object sender,RoutedEventArgs args)=>await ViewModel.ClearCompletedAsync();
    private async void ClearFailed(object sender,RoutedEventArgs args)=>await ViewModel.ClearFailedAsync();
    private async void ClearAll(object sender,RoutedEventArgs args)=>await ViewModel.ClearAllAsync();
    private async void ShowResults(object sender,RoutedEventArgs args)
    {
        var job=ViewModel.BeginResults();if(job is null)return;
        try
        {
            var list=new ListView{ItemsSource=job.Results,ItemTemplate=(DataTemplate)Resources["JobResultTemplate"],MaxHeight=420,MinWidth=400,SelectionMode=ListViewSelectionMode.None};
            await new ContentDialog{XamlRoot=XamlRoot,Title=job.DisplayOperation+"："+job.DisplayState,Content=ViewModel.IssuesOnly?new ScrollViewer{MaxHeight=460,Content=new TextBlock{Text=job.IssueDetails,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true,MinWidth=360,MaxWidth=580}}:list,CloseButtonText="关闭"}.ShowAsync();
        }
        catch(Exception error) { ViewModel.Status=error.Message; }
        finally { ViewModel.EndDialog(); }
    }
    /// <summary>窗口释放时解除订阅；页面离开导航时仍保留缓存。</summary>
    public void Dispose() { if(_disposed)return;_disposed=true;Loaded-=PageLoaded;_viewport.Stop();ViewModel.Refreshing-=CaptureScroll;ViewModel.Refreshed-=RestoreSelection;ViewModel.Dispose(); }
}
