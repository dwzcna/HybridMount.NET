using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace HybridMount.Pages;

/// <summary>挂载管理入口；耗时工作交给应用级协调器，页面不拥有连接。</summary>
public sealed partial class MountsPage:Page,IDisposable
{
    public MountsViewModel ViewModel {get;}=new(App.Current.Mounts);
    private bool _busy;
    private Guid? _selectedMount;
    private string? _selectedJob;
    public MountsPage()
    {
        InitializeComponent();
        MountList.SelectionChanged+=(_,_)=>{if(MountList.SelectedItem is MountSnapshot mount)_selectedMount=mount.Definition.Id;};
        JobList.SelectionChanged+=(_,_)=>{if(JobList.SelectedItem is MountOperationSnapshot job)_selectedJob=job.Id;};
        ViewModel.Refreshed+=RestoreSelection;
    }
    private void RestoreSelection()
    {
        MountList.SelectedItem=ViewModel.Mounts.FirstOrDefault(mount=>mount.Definition.Id==_selectedMount);
        JobList.SelectedItem=ViewModel.Operations.FirstOrDefault(job=>job.Id==_selectedJob);
    }
    private MountSnapshot Selected()=>MountList.SelectedItem as MountSnapshot??throw new InvalidOperationException("请先选择一个挂载。");
    private async Task Run(Func<Task> work)
    {
        if(_busy)return;_busy=true;
        try{await work();}catch(Exception error){ViewModel.Status=error.Message;App.Current.Logger.Queue("挂载","Error",error.Message);}finally{_busy=false;}
    }
    private async void ChooseFolder(object sender,RoutedEventArgs args)=>await Run(async()=>
    {
        var picker=new FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,App.Current.MainWindow!.Handle);
        var folder=await picker.PickSingleFolderAsync();if(folder is not null)PathField.Text=folder.Path;
    });
    private async void CreateMount(object sender,RoutedEventArgs args)=>await Run(async()=>{var mount=await App.Current.Mounts.AddAsync(NameField.Text,PathField.Text,AutoConnectField.IsOn);ViewModel.Status=$"已连接 {mount.Name}。";});
    private async void Connect(object sender,RoutedEventArgs args)=>await Run(()=>App.Current.Mounts.ConnectAsync(Selected().Definition.Id));
    private async void Disconnect(object sender,RoutedEventArgs args)=>await Run(()=>App.Current.Mounts.DisconnectAsync(Selected().Definition.Id));
    private async void OpenFolder(object sender,RoutedEventArgs args)=>await Run(async()=>{var folder=await StorageFolder.GetFolderFromPathAsync(Selected().Definition.LocalPath);await Windows.System.Launcher.LaunchFolderAsync(folder);});
    private void Queue(string operation){try{var mount=Selected();var id=App.Current.Mounts.Queue(mount.Definition.Id,operation);ViewModel.Status=$"操作已提交，任务 {id}。";}catch(Exception error){ViewModel.Status=error.Message;}}
    private void RefreshMount(object sender,RoutedEventArgs args)=>Queue("refresh");
    private void Keep(object sender,RoutedEventArgs args)=>Queue("keepOnDevice");
    private void Release(object sender,RoutedEventArgs args)=>Queue("freeSpace");
    private async void Remove(object sender,RoutedEventArgs args)=>await Run(async()=>
    {
        var mount=Selected();
        var dialog=new ContentDialog{XamlRoot=XamlRoot,Title="移除挂载并保留文件",Content="将先下载所有剩余文件，再解除占位并保留为普通本地目录。本地修改和新建文件会保留；失败时可重试。",PrimaryButtonText="下载并移除",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
        if(await dialog.ShowAsync()==ContentDialogResult.Primary){App.Current.Mounts.Queue(mount.Definition.Id,"remove");ViewModel.Status="保留文件的移除任务已提交。";}
    });
    private async void ShowResults(object sender,RoutedEventArgs args)=>await Run(async()=>
    {
        if(JobList.SelectedItem is not MountOperationSnapshot selected)throw new InvalidOperationException("请先选择任务。");
        var job=App.Current.Mounts.GetOperation(selected.Id)??selected;
        var list=new ListView{ItemsSource=job.Results,ItemTemplate=(DataTemplate)Resources["JobResultTemplate"],MaxHeight=400,MinWidth=400,SelectionMode=ListViewSelectionMode.None};
        await new ContentDialog{XamlRoot=XamlRoot,Title=job.DisplayOperation+"："+job.DisplayState,Content=list,CloseButtonText="关闭"}.ShowAsync();
    });
    private void CancelJob(object sender,RoutedEventArgs args){if(JobList.SelectedItem is MountOperationSnapshot job)ViewModel.Status=App.Current.Mounts.Cancel(job.Id)?"已请求取消；已完成步骤会保留。":"任务已结束或不存在。";}
    public void Dispose(){ViewModel.Refreshed-=RestoreSelection;ViewModel.Dispose();}
}
