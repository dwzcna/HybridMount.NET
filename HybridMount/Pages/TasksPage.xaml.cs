using HybridMount.Helpers.UI;
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
    private IDisposable? _localization;
    /// <summary>UI 线程上的任务快照集合。</summary>
    public TasksViewModel ViewModel { get; }=new(App.Current.TasksQueue,App.Current.UiDispatcher,App.Current.TaskFocus);
    private bool _restoring,_disposed;
    private HeaderLayoutCoordinator? _header;
    private readonly PageScrollBookmark _viewport=new();
    /// <summary>初始化页面并按任务 ID 恢复刷新后的选择。</summary>
    public TasksPage()
    {
        try{InitializeComponent();_localization=global::HybridMount.Helpers.UI.Ui.ObserveLanguage(this,()=>{Bindings.Update();_header?.Invalidate();});NavigationCacheMode=NavigationCacheMode.Required;}catch{ViewModel.Dispose();throw;}
        _header=new(TaskToolbar,TaskFilters,TaskActions,SetFilterLabels);
        JobList.SelectionChanged+=(_,_)=>{if(!_restoring&&JobList.SelectedItem is TaskListItem job)ViewModel.SelectedJobId=job.Id;};
        ViewModel.Refreshing+=CaptureScroll;ViewModel.Refreshed+=RestoreSelection;ViewModel.PropertyChanged+=ToolbarStateChanged;Loaded+=PageLoaded;
    }
    private void PageLoaded(object sender,RoutedEventArgs args){_viewport.Restore(JobList);_header?.Invalidate();}
    private void ToolbarStateChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs args)
    {if(args.PropertyName==nameof(TasksViewModel.HasIssueFilter))_header?.Invalidate();}
    /// <summary>仅切换筛选标签；图标、读屏名称、提示及所选分类始终保留。</summary>
    private void SetFilterLabels(bool compact)
    {
        foreach(var label in new[]{AllFilterLabel,IssuesFilterLabel,ConflictsFilterLabel})label.Visibility=compact?Visibility.Collapsed:Visibility.Visible;
        ClearFilterCommands.DefaultLabelPosition=compact?CommandBarDefaultLabelPosition.Collapsed:CommandBarDefaultLabelPosition.Right;
    }
    protected override void OnNavigatedTo(NavigationEventArgs args){base.OnNavigatedTo(args);_viewport.Enter(JobList);RestoreSelection();}
    protected override void OnNavigatedFrom(NavigationEventArgs args){ViewModel.EndDialog();_viewport.Leave(JobList);base.OnNavigatedFrom(args);}
    private void CaptureScroll(){_restoring=true;_viewport.Capture(JobList);}
    private void RestoreSelection()
    {
        if(_disposed)return;_restoring=true;
        try{if(!ReferenceEquals(JobList.SelectedItem,ViewModel.SelectedRow))JobList.SelectedItem=ViewModel.SelectedRow;}finally{_restoring=false;}
        _viewport.Restore(JobList);
    }
    private void ClearIssueFilter(object sender,RoutedEventArgs args)=>ViewModel.ClearIssueFilter();
    /// <summary>只读核对改名事务，界面不直接发送远端改名。</summary>
    private async void RecheckRename(object sender,RoutedEventArgs args)=>await ViewModel.RecheckSelectedAsync();
    /// <summary>文件存在时由Shell选中；已移除文件打开最近仍存在的父目录，不连接服务或自动上传。</summary>
    private async void OpenLocation(object sender,RoutedEventArgs args)
    {
        if(await ViewModel.SelectedLocationAsync() is not {} path)return;
        try
        {
            var location=await Task.Run(()=>{var folder=Path.GetDirectoryName(path)!;while(!Directory.Exists(folder)&&Path.GetDirectoryName(folder) is {} parent)folder=parent;return(Folder:folder,File:File.Exists(path),Directory:Directory.Exists(path));});if(_disposed||App.Current.IsExiting)return;var folder=location.Folder;
            var storage=await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder);if(_disposed||App.Current.IsExiting)return;
            var options=new global::Windows.System.FolderLauncherOptions();
            if(location.File)options.ItemsToSelect.Add(await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path));
            else if(location.Directory)options.ItemsToSelect.Add(await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(path));
            if(!_disposed&&!App.Current.IsExiting)await global::Windows.System.Launcher.LaunchFolderAsync(storage,options);
        }
        catch(Exception error){if(!_disposed)ViewModel.ReportError(error.Message);}
    }
    /// <summary>比较及取舍由对应ViewModel持有，页面只显示模态操作界面。</summary>
    private async void ResolveConflict(object sender,RoutedEventArgs args)
    {
        var owned=false;
        try{owned=await ViewModel.BeginConflictAsync();if(!owned)return;await new ConflictResolutionContentDialog(ViewModel){XamlRoot=XamlRoot}.ShowAsync();}
        catch(Exception error){if(!_disposed)ViewModel.ReportError(error.Message);}
        finally{if(owned)ViewModel.EndDialog();}
    }
    private async void CancelJob(object sender,RoutedEventArgs args)=>await ViewModel.CancelSelectedAsync();
    private async void RetryUpload(object sender,RoutedEventArgs args)=>await ViewModel.RetrySelectedAsync();
    /// <summary>以新相对路径安全创建远端副本，不改变原始冲突文件或远端旧项。</summary>
    private async void SaveUploadCopy(object sender,RoutedEventArgs args)
    {
        if(!ViewModel.BeginCopy())return;
        try
        {
            var field=global::HybridMount.Helpers.UI.Ui.Configure(new TextBox{Text=ViewModel.CopyPath,MinWidth=400},("Header",global::HybridMount.Services.Localization.UiText.Message("Text_E01311A466BF")));
            field.TextChanged+=(_,_)=>ViewModel.CopyPath=field.Text;
            var content=new StackPanel{Spacing=12,MaxWidth=520};content.Children.Add(new TextBlock{Text=ViewModel.CopyContentNotice,TextWrapping=TextWrapping.Wrap});content.Children.Add(field);
            var dialog=global::HybridMount.Helpers.UI.Ui.Configure(new ContentDialog{XamlRoot=XamlRoot,Content=content,DefaultButton=ContentDialogButton.Close},("Title",global::HybridMount.Services.Localization.UiText.Message("Text_9867EF04D433")),("PrimaryButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_F66DF4636FF4")),("CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_2CD0F3BE8738")));
            if(await global::HybridMount.Helpers.UI.Ui.ShowAsync(dialog)!=ContentDialogResult.Primary)return;
            await ViewModel.SubmitCopyAsync();RestoreSelection();
        }
        catch(Exception error){ViewModel.ReportError(error.Message);}
        finally{ViewModel.EndDialog();}
    }
    /// <summary>仅清理已完成历史，实际操作和异常处理由 ViewModel 及应用所有者执行。</summary>
    private async void ClearCompleted(object sender,RoutedEventArgs args)=>await ViewModel.ClearCompletedAsync();
    private async void ClearFailed(object sender,RoutedEventArgs args)=>await ViewModel.ClearFailedAsync();
    private async void ClearAll(object sender,RoutedEventArgs args)=>await ViewModel.ClearAllAsync();
    private async void ShowResults(object sender,RoutedEventArgs args)
    {
        var job=await ViewModel.BeginResultsAsync();if(job is null)return;
        try
        {
            var list=new ListView{ItemsSource=job.Results,ItemTemplate=(DataTemplate)Resources["JobResultTemplate"],MaxHeight=420,MinWidth=400,SelectionMode=ListViewSelectionMode.None};
            var details=new StackPanel{Spacing=12,MinWidth=360,MaxWidth=580};
            details.Children.Add(global::HybridMount.Helpers.UI.Ui.Bind(new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true},"Text",()=>global::HybridMount.Services.Localization.ResourceMessage.Raw(job.IssueDetails+(job.WorkflowDetails.Length==0?"":"\n"+job.WorkflowDetails))));
            if(!ViewModel.IssuesOnly&&!ViewModel.ConflictsOnly)details.Children.Add(list);
            await global::HybridMount.Helpers.UI.Ui.Bind(global::HybridMount.Helpers.UI.Ui.Configure(new ContentDialog{XamlRoot=XamlRoot,Content=new ScrollViewer{MaxHeight=460,Content=details}},("CloseButtonText",global::HybridMount.Services.Localization.UiText.Message("Text_3FD47EDCE45B"))),"Title",()=>global::HybridMount.Services.Localization.ResourceMessage.Raw(job.DisplayOperation+"："+job.DisplayState)).ShowAsync();
        }
        catch(Exception error) { ViewModel.ReportError(error.Message); }
        finally { ViewModel.EndDialog(); }
    }
    /// <summary>窗口释放时解除订阅；页面离开导航时仍保留缓存。</summary>
    public void Dispose() {_header?.Dispose();_header=null;_localization?.Dispose();_localization=null; if(_disposed)return;_disposed=true;Loaded-=PageLoaded;_viewport.Stop();ViewModel.Refreshing-=CaptureScroll;ViewModel.Refreshed-=RestoreSelection;ViewModel.PropertyChanged-=ToolbarStateChanged;ViewModel.Dispose(); }
}
