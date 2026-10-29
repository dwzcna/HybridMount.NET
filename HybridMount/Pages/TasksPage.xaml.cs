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
    private bool _showingResults;
    /// <summary>初始化页面并按任务 ID 恢复刷新后的选择。</summary>
    public TasksPage()
    {
        InitializeComponent();
        JobList.SelectionChanged+=(_,_)=>{if(JobList.SelectedItem is MountOperationSnapshot job)_selectedJob=job.Id;};
        ViewModel.Refreshed+=RestoreSelection;
    }
    private void RestoreSelection()=>JobList.SelectedItem=ViewModel.Operations.FirstOrDefault(job=>job.Id==_selectedJob);
    private void CancelJob(object sender,RoutedEventArgs args)
    {
        if(JobList.SelectedItem is not MountOperationSnapshot job) { ViewModel.Status="请先选择任务。";return; }
        ViewModel.Status=App.Current.Mounts.Cancel(job.Id)?"已请求取消；已完成的步骤保留，已删除内容不会恢复。":"任务已结束或不存在。";
    }
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
    public void Dispose() { ViewModel.Refreshed-=RestoreSelection;ViewModel.Dispose(); }
}
