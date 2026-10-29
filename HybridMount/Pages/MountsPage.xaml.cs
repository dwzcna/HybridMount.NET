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
    public MountsViewModel ViewModel { get; }=new(App.Current.Mounts);
    private bool _busy;
    private Guid? _selectedMount;
    /// <summary>创建页面并保留刷新前选中的挂载 ID。</summary>
    public MountsPage()
    {
        InitializeComponent();
        MountList.SelectionChanged+=(_,_)=>{if(MountList.SelectedItem is MountSnapshot mount)_selectedMount=mount.Definition.Id;};
        ViewModel.Refreshed+=RestoreSelection;
    }
    private void RestoreSelection()=>MountList.SelectedItem=ViewModel.Mounts.FirstOrDefault(mount=>mount.Definition.Id==_selectedMount);
    private MountSnapshot Selected()=>MountList.SelectedItem as MountSnapshot??throw new InvalidOperationException("请先选择一个挂载。");
    private async Task Run(Func<Task> work)
    {
        if(_busy)return;_busy=true;
        try { await work(); }
        catch(Exception error) { ViewModel.Status=error.Message;App.Current.Logger.Queue("挂载","Error",error.Message); }
        finally { _busy=false; }
    }
    private async void CreateMockMount(object sender,RoutedEventArgs args)=>await Run(async()=>
    {
        var dialog=new MockMountDialog{XamlRoot=XamlRoot};await dialog.ShowAsync();
        if(dialog.CreatedMount is not null) { _selectedMount=dialog.CreatedMount.Id;RestoreSelection();ViewModel.Status=dialog.ResultMessage; }
    });
    private async void Connect(object sender,RoutedEventArgs args)=>await Run(()=>App.Current.Mounts.ConnectAsync(Selected().Definition.Id));
    private async void Disconnect(object sender,RoutedEventArgs args)=>await Run(()=>App.Current.Mounts.DisconnectAsync(Selected().Definition.Id));
    private async void OpenFolder(object sender,RoutedEventArgs args)=>await Run(async()=>{var folder=await StorageFolder.GetFolderFromPathAsync(Selected().Definition.LocalPath);await Windows.System.Launcher.LaunchFolderAsync(folder);});
    private void Queue(string operation)
    {
        if(_busy)return;
        try { App.Current.Mounts.Queue(Selected().Definition.Id,operation);ViewModel.Status="操作已提交，可在任务页查看进度和逐项结果。"; }
        catch(Exception error) { ViewModel.Status=error.Message; }
    }
    private void RefreshMount(object sender,RoutedEventArgs args)=>Queue("refresh");
    private void Keep(object sender,RoutedEventArgs args)=>Queue("keepOnDevice");
    private void Release(object sender,RoutedEventArgs args)=>Queue("freeSpace");
    private async void Remove(object sender,RoutedEventArgs args)=>await Run(async()=>
    {
        var mount=Selected().Definition;
        var message=mount.LocalPath+"\n\n下载并移除：完整下载后保留为普通本地目录。\n\n清空并移除：永久删除目录内全部内容，包括本地修改和新建文件，保留空根目录；不删除文件源内容。";
        if(mount.RemovalPending)message+="\n\n上次移除尚未完成，请继续相同的移除方式。";
        var dialog=new ContentDialog{XamlRoot=XamlRoot,Title="移除挂载",Content=message,PrimaryButtonText="下载并移除",SecondaryButtonText="清空并移除",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close,
            IsPrimaryButtonEnabled=!mount.RemovalPending||mount.RemovalMode==MountRemovalMode.PreserveFiles,
            IsSecondaryButtonEnabled=!mount.RemovalPending||mount.RemovalMode==MountRemovalMode.ClearContents};
        var result=await dialog.ShowAsync();
        if(result==ContentDialogResult.None)return;
        App.Current.Mounts.Queue(mount.Id,result==ContentDialogResult.Primary?"remove":"clearAndRemove");
        ViewModel.Status="移除任务已提交，可在任务页查看进度和逐项结果。";
    });
    /// <summary>窗口释放时解除列表订阅，不断开挂载。</summary>
    public void Dispose() { ViewModel.Refreshed-=RestoreSelection;ViewModel.Dispose(); }
}
