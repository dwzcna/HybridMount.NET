using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.UI.Dispatching;

namespace HybridMount.ViewModels;

/// <summary>将应用级后台任务快照合并派发到 UI，不拥有或停止任务。</summary>
public sealed class TasksViewModel : ObservableObject, IDisposable
{
    private readonly MountManager _manager;
    private readonly DispatcherQueue _dispatcher=DispatcherQueue.GetForCurrentThread();
    private int _pending;
    private bool _disposed;
    private string _status="切换页面或关闭到托盘后，后台任务会继续运行。";
    /// <summary>执行中、排队／等待和历史分组展示，同组使用稳定提交顺序。</summary>
    public ObservableCollection<MountOperationSnapshot> Operations { get; }=[];
    /// <summary>刷新后由页面按任务 ID 恢复选择。</summary>
    public event Action? Refreshing;
    public event Action? Refreshed;
    /// <summary>当前操作提示。</summary>
    public string Status { get=>_status; set=>SetProperty(ref _status,value); }
    /// <summary>在 UI 线程订阅应用级任务；释放时解除订阅。</summary>
    public TasksViewModel(MountManager manager) { _manager=manager;_manager.Changed+=Changed;Refresh(); }
    private void Changed()
    {
        if(_disposed || Interlocked.Exchange(ref _pending,1)!=0)return;
        if(!_dispatcher.TryEnqueue(()=>{Interlocked.Exchange(ref _pending,0);if(!_disposed)Refresh();}))Interlocked.Exchange(ref _pending,0);
    }
    private void Refresh()
    {
        try { var snapshots=OperationDisplayOrder.Sort(_manager.Operations);Refreshing?.Invoke();SnapshotCollections.Reconcile(Operations,snapshots,operation=>operation.Id);Refreshed?.Invoke(); }
        catch(Exception error) { Status=error.Message; }
    }
    /// <summary>解除快照订阅；不取消任何后台工作。</summary>
    public void Dispose() { _disposed=true;_manager.Changed-=Changed; }
}
