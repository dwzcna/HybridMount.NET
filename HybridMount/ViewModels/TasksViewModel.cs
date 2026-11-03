using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.ViewModels;

/// <summary>将应用级后台任务快照合并派发到 UI，不拥有或停止任务。</summary>
public sealed class TasksViewModel : ObservableObject, IDisposable
{
    private readonly MountManager _manager;
    private readonly IUiDispatcher _dispatcher;
    private int _pending;
    private bool _disposed,_clearingCompleted,_dialogBusy;
    private string? _selectedJob,_dialogJob;
    private string _copyPath="";
    private string _status="切换页面或关闭到托盘后，后台任务会继续运行。";
    /// <summary>执行中、排队／等待和历史分组展示，同组使用稳定提交顺序。</summary>
    public ObservableCollection<MountOperationSnapshot> Operations { get; }=[];
    /// <summary>刷新后由页面按任务 ID 恢复选择。</summary>
    public event Action? Refreshing;
    public event Action? Refreshed;
    /// <summary>仅有成功历史且当前未清理时可操作；当前任务或故障不会因清理丢失。</summary>
    public bool CanClearCompleted=>!_disposed&&!_clearingCompleted&&Operations.Any(operation=>operation.State=="Completed");
    /// <summary>清理在应用生命周期内执行；防止重复点击，失败保留页面状态并显示原因。</summary>
    public async Task ClearCompletedAsync()
    {
        if(!CanClearCompleted)return;
        _clearingCompleted=true;OnPropertyChanged(nameof(CanClearCompleted));
        try
        {
            var count=await _manager.ClearCompletedOperationsAsync();
            if(!_disposed)Status=count==0?"没有可清除的已完成任务。":$"已清除 {count} 条已完成任务；其他任务保留。";
        }
        catch(OperationCanceledException)when(_manager.IsStopping){if(!_disposed)Status="程序正在退出，已停止清理。";}
        catch(Exception error){if(!_disposed)Status="清除失败："+error.Message;}
        finally{_clearingCompleted=false;if(!_disposed){Refresh();OnPropertyChanged(nameof(CanClearCompleted));}}
    }
    /// <summary>当前操作提示。</summary>
    public string Status {get=>_status;set{if(!_disposed)SetProperty(ref _status,value);}}
    public string? SelectedJobId {get=>_selectedJob;set{if(!_disposed)SetProperty(ref _selectedJob,value);}}
    public MountOperationSnapshot? Selected=>Operations.FirstOrDefault(x=>x.Id==_selectedJob);
    public string CopyPath {get=>_copyPath;set{if(!_disposed)SetProperty(ref _copyPath,value);}}
    public void CancelSelected()
    {
        if(_disposed||_manager.IsStopping)return;
        if(Selected is not {} selected){Status="请先选择任务。";return;}
        try{Status=_manager.Cancel(selected.Id)?"已请求取消；已完成的步骤保留，已删除内容不会恢复。":"任务已结束或不存在。";}catch(Exception error){Status=error.Message;}
    }
    public void RetrySelected()
    {
        if(_disposed||_manager.IsStopping)return;
        if(Selected is not {Operation:"upload" or "remoteUpdate"} selected){Status="请先选择上传或远端更新任务。";return;}
        try{Status=_manager.RetryUpload(selected.Id)?"已提交重试；如挂载未连接，请先在挂载页连接。":"当前上传任务不能重试。";}catch(Exception error){Status=error.Message;}
    }
    public bool BeginCopy()
    {
        if(_disposed||_manager.IsStopping||_dialogBusy)return false;
        if(Selected is not {Operation:"upload"} selected){Status="请先选择上传任务。";return false;}
        _dialogBusy=true;_dialogJob=selected.Id;
        var original=selected.ItemPath??"副本";var extension=Path.GetExtension(original);CopyPath=original[..(original.Length-extension.Length)]+" (副本)"+extension;return true;
    }
    /// <summary>捕获的原任务只可提交一次；选择切换不改变副本的来源，应用服务仍校验远端目标。</summary>
    public void SubmitCopy()
    {
        if(_disposed||_manager.IsStopping||!_dialogBusy||_dialogJob is not {} id)return;
        _dialogJob=null;
        try{SelectedJobId=_manager.UploadAsCopy(id,CopyPath.Trim());Status="副本任务已提交；目标已存在时会拒绝覆盖。";}
        catch(Exception error){Status=error.Message;}
    }
    public MountOperationSnapshot? BeginResults()
    {
        if(_disposed||_manager.IsStopping||_dialogBusy)return null;
        if(Selected is not {} selected){Status="请先选择任务。";return null;}
        try{var result=_manager.GetOperation(selected.Id)??selected;_dialogBusy=true;return result;}
        catch(Exception error){Status=error.Message;return null;}
    }
    public void EndDialog(){_dialogBusy=false;_dialogJob=null;}
    /// <summary>在 UI 线程订阅应用级任务；释放时解除订阅。</summary>
    public TasksViewModel(MountManager manager,IUiDispatcher dispatcher) { _dispatcher=dispatcher;_manager=manager;_manager.Changed+=Changed;Refresh(); }
    private void Changed()
    {
        if(_disposed || Interlocked.Exchange(ref _pending,1)!=0)return;
        if(!_dispatcher.TryEnqueue(()=>{Interlocked.Exchange(ref _pending,0);if(!_disposed)Refresh();}))Interlocked.Exchange(ref _pending,0);
    }
    private void Refresh()
    {
        if(_disposed)return;
        try { var snapshots=OperationDisplayOrder.Sort(_manager.Operations);Refreshing?.Invoke();SnapshotCollections.Reconcile(Operations,snapshots,operation=>operation.Id);OnPropertyChanged(nameof(CanClearCompleted));Refreshed?.Invoke(); }
        catch(Exception error) { Status=error.Message; }
    }
    /// <summary>解除快照订阅；不取消任何后台工作。</summary>
    public void Dispose() { _disposed=true;_manager.Changed-=Changed; }
}
