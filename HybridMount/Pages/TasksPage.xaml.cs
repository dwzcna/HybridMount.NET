using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Pages;

/// <summary>独立任务页；后台任务由应用托管，页面仅保留选择及展示结果。</summary>
public sealed partial class TasksPage : Page, IDisposable
{
    /// <summary>UI 线程上的任务快照集合。</summary>
    public TasksViewModel ViewModel { get; }=new(App.Current.Mounts);
    private string? _selectedJob;
    private bool _showingResults,_restoring,_disposed;
    private ScrollViewer? _listScroll;
    private double _offset;
    private long _refreshVersion;
    /// <summary>初始化页面并按任务 ID 恢复刷新后的选择。</summary>
    public TasksPage()
    {
        InitializeComponent();
        JobList.SelectionChanged+=(_,_)=>{if(!_restoring&&JobList.SelectedItem is MountOperationSnapshot job)_selectedJob=job.Id;};
        ViewModel.Refreshing+=CaptureScroll;ViewModel.Refreshed+=RestoreSelection;
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for(var i=0;i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
        {var child=Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent,i);if(child is ScrollViewer viewer)return viewer;if(FindScroll(child) is {} nested)return nested;}
        return null;
    }
    private void CaptureScroll(){_restoring=true;_listScroll??=FindScroll(JobList);_offset=_listScroll?.VerticalOffset??0;}
    private void RestoreSelection()
    {
        try{JobList.SelectedItem=ViewModel.Operations.FirstOrDefault(job=>job.Id==_selectedJob);}finally{_restoring=false;}
        var revision=++_refreshVersion;var offset=_offset;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,()=>{if(!_disposed&&revision==_refreshVersion)_listScroll?.ChangeView(null,offset,null,true);});
    }
    private void CancelJob(object sender,RoutedEventArgs args)
    {
        if(JobList.SelectedItem is not MountOperationSnapshot job) { ViewModel.Status="请先选择任务。";return; }
        ViewModel.Status=App.Current.Mounts.Cancel(job.Id)?"已请求取消；已完成的步骤保留，已删除内容不会恢复。":"任务已结束或不存在。";
    }
    /// <summary>显式重试保持原始覆盖基线；结果不明仍先只读核对。</summary>
    private void RetryUpload(object sender,RoutedEventArgs args)
    {
        if(JobList.SelectedItem is not MountOperationSnapshot {Operation:"upload" or "remoteUpdate"} job){ViewModel.Status="请先选择上传或远端更新任务。";return;}
        ViewModel.Status=App.Current.Mounts.RetryUpload(job.Id)?"已提交重试；如挂载未连接，请先在挂载页连接。":"当前上传任务不能重试。";
    }
    /// <summary>以新相对路径安全创建远端副本，不改变原始冲突文件或远端旧项。</summary>
    private async void SaveUploadCopy(object sender,RoutedEventArgs args)
    {
        if(_showingResults)return;
        if(JobList.SelectedItem is not MountOperationSnapshot {Operation:"upload"} selected){ViewModel.Status="请先选择上传任务。";return;}
        _showingResults=true;
        try
        {
            var original=selected.ItemPath??"副本";var extension=Path.GetExtension(original);var name=original[..(original.Length-extension.Length)]+" (副本)"+extension;
            var field=new TextBox{Header="远端新文件相对路径",Text=name,MinWidth=400};
            var dialog=new ContentDialog{XamlRoot=XamlRoot,Title="另存远端副本",Content=field,PrimaryButtonText="上传副本",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            _selectedJob=App.Current.Mounts.UploadAsCopy(selected.Id,field.Text.Trim());ViewModel.Status="副本任务已提交；目标已存在时会拒绝覆盖。";RestoreSelection();
        }
        catch(Exception error){ViewModel.Status=error.Message;}
        finally{_showingResults=false;}
    }
    /// <summary>仅清理已完成历史，实际操作和异常处理由 ViewModel 及应用所有者执行。</summary>
    private async void ClearCompleted(object sender,RoutedEventArgs args)=>await ViewModel.ClearCompletedAsync();
    private async void ShowResults(object sender,RoutedEventArgs args)
    {
        if(_showingResults)return;
        if(JobList.SelectedItem is not MountOperationSnapshot selected) { ViewModel.Status="请先选择任务。";return; }
        _showingResults=true;
        try
        {
            var job=App.Current.Mounts.GetOperation(selected.Id)??selected;
            var list=new ListView{ItemsSource=job.Results,ItemTemplate=(DataTemplate)Resources["JobResultTemplate"],MaxHeight=420,MinWidth=400,SelectionMode=ListViewSelectionMode.None};
            await new ContentDialog{XamlRoot=XamlRoot,Title=job.DisplayOperation+"："+job.DisplayState,Content=list,CloseButtonText="关闭"}.ShowAsync();
        }
        catch(Exception error) { ViewModel.Status=error.Message; }
        finally { _showingResults=false; }
    }
    /// <summary>窗口释放时解除订阅；页面离开导航时仍保留缓存。</summary>
    public void Dispose() { _disposed=true;ViewModel.Refreshing-=CaptureScroll;ViewModel.Refreshed-=RestoreSelection;ViewModel.Dispose(); }
}
