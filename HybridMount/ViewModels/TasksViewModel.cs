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
    private readonly TaskFocusService? _focus;
    private long _focusRevision;
    private bool _issuesOnly,_conflictsOnly;
    private ConflictComparison? _comparison;
    private CancellationTokenSource? _comparisonCancel;
    private bool _conflictBusy;
    private string _conflictStatus="";
    private Guid? _filterMount;
    private IReadOnlyList<string> _filterPaths=[];
    private bool _disposed,_clearingHistory,_dialogBusy;
    private string? _selectedJob,_dialogJob;
    private string _copyPath="";
    private string _status="";
    /// <summary>执行中、排队／等待和历史分组展示，同组使用稳定提交顺序。</summary>
    public ObservableCollection<MountOperationSnapshot> Operations { get; }=[];
    /// <summary>刷新后由页面按任务 ID 恢复选择。</summary>
    public event Action? Refreshing;
    public event Action? Refreshed;
    public bool CanClearCompleted=>CanClear(TaskHistoryClearMode.Completed);
    public bool CanClearFailed=>CanClear(TaskHistoryClearMode.Failed);
    public bool CanClearAll=>CanClear(TaskHistoryClearMode.All);
    private bool CanClear(TaskHistoryClearMode mode)=>!_disposed&&!_manager.IsStopping&&!_clearingHistory&&_manager.Operations.Any(operation=>TaskHistoryRules.Matches(mode,operation.State));
    private void NotifyClearState(){OnPropertyChanged(nameof(CanClearCompleted));OnPropertyChanged(nameof(CanClearFailed));OnPropertyChanged(nameof(CanClearAll));}
    public Task ClearCompletedAsync()=>ClearHistoryAsync(TaskHistoryClearMode.Completed);
    public Task ClearFailedAsync()=>ClearHistoryAsync(TaskHistoryClearMode.Failed);
    public Task ClearAllAsync()=>ClearHistoryAsync(TaskHistoryClearMode.All);
    /// <summary>三种清理共享防重入；页面离开不取消已接纳工作，失败或退出不产生迟到更新。</summary>
    private async Task ClearHistoryAsync(TaskHistoryClearMode mode)
    {
        if(!CanClear(mode))return;
        _clearingHistory=true;NotifyClearState();
        var label=mode==TaskHistoryClearMode.Completed?"已完成":mode==TaskHistoryClearMode.Failed?"失败及部分失败":"已结束";
        try
        {
            var count=await _manager.ClearOperationHistoryAsync(mode);
            if(!_disposed)Status=count==0?$"没有可清除的{label}历史；进行中或待恢复的任务已保留。":$"已清除 {count} 条{label}任务历史；进行中和待处理任务保留。";
        }
        catch(OperationCanceledException)when(_manager.IsStopping){if(!_disposed)Status="程序正在退出，已停止清理。";}
        catch(Exception error){if(!_disposed)Status="清除失败："+error.Message;}
        finally{_clearingHistory=false;if(!_disposed){Refresh();NotifyClearState();}}
    }
    /// <summary>当前操作提示。</summary>
    public string Status {get=>_status;set{if(!_disposed)SetProperty(ref _status,value);}}
    public bool IssuesOnly {get=>_issuesOnly;set{if(!_disposed&&SetProperty(ref _issuesOnly,value)){if(value){_conflictsOnly=false;OnPropertyChanged(nameof(ConflictsOnly));}Refresh();}}}
    public bool ConflictsOnly{get=>_conflictsOnly;set{if(!_disposed&&SetProperty(ref _conflictsOnly,value)){if(value){_issuesOnly=false;OnPropertyChanged(nameof(IssuesOnly));}Refresh();}}}
    public string IssueViewTitle=>_filterMount is {} id?"同步问题 · "+(_manager.Mounts.FirstOrDefault(x=>x.Definition.Id==id)?.Definition.Name??"挂载"):"同步问题";
    public bool HasIssueFilter=>_filterMount is not null||_filterPaths.Count>0;
    public void ClearIssueFilter(){_filterMount=null;_filterPaths=[];Refresh();}
    public string? SelectedLocation()
    {
        if(_disposed||_manager.IsStopping||Selected is not {Operation:"upload" or "remoteUpdate"} selected){Status="请先选择同步问题。";return null;}
        try{return _manager.SyncIssueLocalPath(selected.Id);}catch(Exception error){Status=error.Message;return null;}
    }
    private void FocusChanged()
    {
        if(_disposed)return;_dispatcher.TryEnqueue(()=>{if(!_disposed)ApplyFocus();});
    }
    private void ApplyFocus()
    {
        if(_focus?.Current is not {} focus||focus.Revision==_focusRevision)return;
        _focusRevision=focus.Revision;_filterMount=focus.MountId;_filterPaths=focus.Paths;_issuesOnly=true;_conflictsOnly=false;OnPropertyChanged(nameof(ConflictsOnly));
        OnPropertyChanged(nameof(IssuesOnly));Refresh();SelectedJobId=Operations.FirstOrDefault()?.Id;Refreshed?.Invoke();
    }
    public string? SelectedJobId {get=>_selectedJob;set{if(!_disposed)SetProperty(ref _selectedJob,value);}}
    public MountOperationSnapshot? Selected=>Operations.FirstOrDefault(x=>x.Id==_selectedJob);
    public string CopyPath {get=>_copyPath;set{if(!_disposed)SetProperty(ref _copyPath,value);}}
    public string CopyContentNotice=>"已有上传快照时，副本使用该快照，后续本地编辑不会改变副本。没有快照时，将使用上传准备时的当前本地内容，而不是冲突时的内容。";
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
        try{var result=IssuesOnly?selected:_manager.GetOperation(selected.Id)??selected;_dialogBusy=true;return result;}
        catch(Exception error){Status=error.Message;return null;}
    }
    /// <summary>关闭比较只取消本次准备，不取消原冲突；已开始的受保护写入由应用完成安全收尾。</summary>
    public void EndDialog(){_comparisonCancel?.Cancel();_comparisonCancel?.Dispose();_comparisonCancel=null;_comparison=null;_dialogBusy=false;_dialogJob=null;if(!_disposed)NotifyConflict();}
    public string ConflictTitle=>"处理冲突 · "+(_comparison?.Path??"文件");
    public string ConflictLocalText=>_comparison?.LocalText??"尚未核对";
    public string ConflictRemoteText=>_comparison?.RemoteText??"尚未核对";
    public string ConflictBaselineText=>_comparison?.BaselineText??"";
    public string ConflictLocalRestriction=>_comparison?.LocalRestriction??"请先重新核对。";
    public string ConflictRemoteRestriction=>_comparison?.RemoteRestriction??"请先重新核对。";
    public string ConflictRecoveryText=>_comparison?.RecoveryPath is {Length:>0} path?"保留的本地恢复备份："+path:"";
    public string ConflictStatus=>_conflictStatus;
    public bool CanResolveWithLocal=>!_disposed&&!_conflictBusy&&_comparison?.CanUseLocal==true;
    public bool CanResolveWithRemote=>!_disposed&&!_conflictBusy&&_comparison?.CanUseRemote==true;
    public bool CanRefreshConflict=>!_disposed&&!_conflictBusy;
    private void NotifyConflict(){foreach(var property in new[]{nameof(ConflictTitle),nameof(ConflictLocalText),nameof(ConflictRemoteText),nameof(ConflictBaselineText),nameof(ConflictLocalRestriction),nameof(ConflictRemoteRestriction),nameof(ConflictRecoveryText),nameof(ConflictStatus),nameof(CanResolveWithLocal),nameof(CanResolveWithRemote),nameof(CanRefreshConflict)})OnPropertyChanged(property);}
    /// <summary>捕获原事务ID，选择切换不会把比较授权转移到其他文件。</summary>
    public async Task<bool> BeginConflictAsync()
    {
        if(_disposed||_manager.IsStopping||_dialogBusy||_conflictBusy)return false;
        if(Selected is not{Operation:"upload" or "remoteUpdate"} selected){Status="请先选择文件冲突。";return false;}
        var owner=new CancellationTokenSource();_dialogBusy=true;_dialogJob=selected.Id;_comparisonCancel=owner;await RefreshConflictAsync();return !_disposed&&_dialogBusy&&_dialogJob==selected.Id&&_comparisonCancel==owner;
    }
    public async Task RefreshConflictAsync()
    {
        if(_disposed||_conflictBusy||!_dialogBusy||_dialogJob is not {} id||_comparisonCancel is null)return;
        var owner=_comparisonCancel;_conflictBusy=true;_comparison=null;_conflictStatus="正在只读核对两端信息…";NotifyConflict();
        try{var compared=await _manager.CompareConflictAsync(id,owner.Token);if(!_disposed&&_dialogJob==id&&_comparisonCancel==owner){_comparison=compared;_conflictStatus="核对时间："+compared.ObservedUtc.ToLocalTime().ToString("G");}}
        catch(OperationCanceledException){if(!_disposed&&_comparisonCancel==owner)_conflictStatus="核对已停止。";}
        catch(Exception error){if(!_disposed&&_comparisonCancel==owner)_conflictStatus=error.Message;}
        finally{_conflictBusy=false;if(!_disposed)NotifyConflict();}
    }
    /// <summary>直接选择调用独立条件取舍流程；失败使旧比较失效，禁止重复点击重发。</summary>
    public async Task<bool> ResolveConflictAsync(bool useLocal)
    {
        if(!(useLocal?CanResolveWithLocal:CanResolveWithRemote)||_comparison is not {} compared||_comparisonCancel is null)return false;
        _conflictBusy=true;_conflictStatus=useLocal?"正在冻结本地内容并提交条件上传…":"正在备份本地、下载并核验远端内容…";NotifyConflict();
        try{var result=await _manager.ResolveConflictAsync(compared,useLocal,_comparisonCancel.Token);if(!_disposed){Status=result;_conflictStatus=result;}return true;}
        catch(OperationCanceledException){if(!_disposed)_conflictStatus="处理已停止；已保存的备份和恢复阶段保留。";return false;}
        catch(Exception error){if(!_disposed)_conflictStatus=error.Message;return false;}
        finally{_comparison=null;_conflictBusy=false;if(!_disposed){Refresh();NotifyConflict();}}
    }
    /// <summary>在 UI 线程订阅应用级任务；释放时解除订阅。</summary>
    public TasksViewModel(MountManager manager,IUiDispatcher dispatcher,TaskFocusService? focus=null) { _dispatcher=dispatcher;_manager=manager;_focus=focus;_manager.Changed+=Changed;if(focus is not null)focus.Changed+=FocusChanged;Refresh();ApplyFocus(); }
    private void Changed()
    {
        if(_disposed || Interlocked.Exchange(ref _pending,1)!=0)return;
        if(!_dispatcher.TryEnqueue(()=>{Interlocked.Exchange(ref _pending,0);if(!_disposed)Refresh();}))Interlocked.Exchange(ref _pending,0);
    }
    private void Refresh()
    {
        if(_disposed)return;
        try { var source=_issuesOnly||_conflictsOnly?(_conflictsOnly?_manager.Conflicts:_manager.SyncIssues).Where(x=>(_filterMount is null||x.MountId==_filterMount)&&(_filterPaths.Count==0||_filterPaths.Any(path=>path.Length==0||x.ItemPath?.Equals(path,StringComparison.OrdinalIgnoreCase)==true||x.ItemPath?.StartsWith(path+"/",StringComparison.OrdinalIgnoreCase)==true))):_manager.Operations;
            var snapshots=OperationDisplayOrder.Sort(source);OnPropertyChanged(nameof(IssueViewTitle));OnPropertyChanged(nameof(HasIssueFilter));Refreshing?.Invoke();SnapshotCollections.Reconcile(Operations,snapshots,operation=>operation.Id);NotifyClearState();Refreshed?.Invoke(); }
        catch(Exception error) { Status=error.Message; }
    }
    /// <summary>解除快照订阅；不取消任何后台工作。</summary>
    public void Dispose() { EndDialog();_disposed=true;_manager.Changed-=Changed;if(_focus is not null)_focus.Changed-=FocusChanged; }
}
