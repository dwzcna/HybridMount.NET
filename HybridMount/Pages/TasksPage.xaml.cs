using HybridMount.Helpers;
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
    public TasksViewModel ViewModel { get; }=new(App.Current.Mounts,App.Current.UiDispatcher);
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
    protected override void OnNavigatedFrom(NavigationEventArgs args){_viewport.Leave(JobList);base.OnNavigatedFrom(args);}
    private void CaptureScroll(){_restoring=true;_viewport.Capture(JobList);}
    private void RestoreSelection()
    {
        if(_disposed)return;_restoring=true;
        try{JobList.SelectedItem=ViewModel.Selected;}finally{_restoring=false;}
        _viewport.Restore(JobList);
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
            var dialog=new ContentDialog{XamlRoot=XamlRoot,Title="另存远端副本",Content=field,PrimaryButtonText="上传副本",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
            if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
            ViewModel.SubmitCopy();RestoreSelection();
        }
        catch(Exception error){ViewModel.Status=error.Message;}
        finally{ViewModel.EndDialog();}
    }
    /// <summary>仅清理已完成历史，实际操作和异常处理由 ViewModel 及应用所有者执行。</summary>
    private async void ClearCompleted(object sender,RoutedEventArgs args)=>await ViewModel.ClearCompletedAsync();
    private async void ShowResults(object sender,RoutedEventArgs args)
    {
        var job=ViewModel.BeginResults();if(job is null)return;
        try
        {
            var list=new ListView{ItemsSource=job.Results,ItemTemplate=(DataTemplate)Resources["JobResultTemplate"],MaxHeight=420,MinWidth=400,SelectionMode=ListViewSelectionMode.None};
            await new ContentDialog{XamlRoot=XamlRoot,Title=job.DisplayOperation+"："+job.DisplayState,Content=list,CloseButtonText="关闭"}.ShowAsync();
        }
        catch(Exception error) { ViewModel.Status=error.Message; }
        finally { ViewModel.EndDialog(); }
    }
    /// <summary>窗口释放时解除订阅；页面离开导航时仍保留缓存。</summary>
    public void Dispose() { if(_disposed)return;_disposed=true;Loaded-=PageLoaded;_viewport.Stop();ViewModel.Refreshing-=CaptureScroll;ViewModel.Refreshed-=RestoreSelection;ViewModel.Dispose(); }
}
