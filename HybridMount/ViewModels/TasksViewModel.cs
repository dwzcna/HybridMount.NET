using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.ViewModels;

/// <summary>将应用级后台任务快照合并派发到 UI，不拥有或停止任务。</summary>
public sealed class TasksViewModel : ObservableObject, IDisposable
{
    private readonly TasksQueueService _queue;
    private readonly IUiDispatcher _dispatcher;
    private sealed record SnapshotQuery(bool Issues,bool Conflicts,Guid? Mount,IReadOnlyList<string> Paths);
    private sealed record PageSnapshot(MountOperationSnapshot[] Visible,MountOperationSnapshot[] All,MountSnapshot[] Mounts);
    private readonly BackgroundSnapshotPump<SnapshotQuery,PageSnapshot> _snapshots;
    private SnapshotQuery _query=new(false,false,null,Array.Empty<string>());
    private MountOperationSnapshot[] _allForActions=[];
    private MountSnapshot[] _knownMounts=[];
    private readonly TaskTransferRate _rates=new();
    private readonly TaskFocusService? _focus;
    private long _focusRevision,_dialogRevision;
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
    /// <summary>本页可关闭提示；后台刷新不重新打开已关闭消息。</summary>
    public PageStatusMessage StatusMessage {get;}=new();
    /// <summary>执行中、排队／等待和历史分组展示，同组使用稳定提交顺序。</summary>
    public ObservableCollection<TaskListItem> Operations { get; }=[];
    /// <summary>刷新后由页面按任务 ID 恢复选择。</summary>
    public event Action? Refreshing;
    public event Action? Refreshed;
    public bool CanClearCompleted=>CanClear(TaskHistoryClearMode.Completed);
    public bool CanClearFailed=>CanClear(TaskHistoryClearMode.Failed);
    public bool CanClearAll=>CanClear(TaskHistoryClearMode.All);
    private bool CanClear(TaskHistoryClearMode mode)=>!_disposed&&!_queue.IsStopping&&!_clearingHistory&&_allForActions.Any(operation=>TaskHistoryRules.Matches(mode,operation.State));
    private void NotifyClearState(){OnPropertyChanged(nameof(CanClearCompleted));OnPropertyChanged(nameof(CanClearFailed));OnPropertyChanged(nameof(CanClearAll));}
    public Task ClearCompletedAsync()=>ClearHistoryAsync(TaskHistoryClearMode.Completed);
    public Task ClearFailedAsync()=>ClearHistoryAsync(TaskHistoryClearMode.Failed);
    public Task ClearAllAsync()=>ClearHistoryAsync(TaskHistoryClearMode.All);
    /// <summary>三种清理共享防重入；页面离开不取消已接纳工作，失败或退出不产生迟到更新。</summary>
    private async Task ClearHistoryAsync(TaskHistoryClearMode mode)
    {
        if(!CanClear(mode))return;
        _clearingHistory=true;NotifyClearState();
        var label=mode==TaskHistoryClearMode.Completed?global::HybridMount.Services.Localization.UiText.Text("Text_F28461BB49C8"):mode==TaskHistoryClearMode.Failed?global::HybridMount.Services.Localization.UiText.Text("Text_CC88E5B26428"):global::HybridMount.Services.Localization.UiText.Text("Text_D79B1D0E5C61");
        try
        {
            var count=await _queue.ClearOperationHistoryAsync(mode);
            if(!_disposed)Status=count==0?global::HybridMount.Services.Localization.UiText.Text("Text_DF9CE89B8A1A",label):global::HybridMount.Services.Localization.UiText.Text("Text_98B1705744A3",count,label);
        }
        catch(OperationCanceledException)when(_queue.IsStopping){if(!_disposed)Status=global::HybridMount.Services.Localization.UiText.Text("Text_93D305FC0E03");}
        catch(Exception error){if(!_disposed)PublishStatus(global::HybridMount.Services.Localization.UiText.Text("Text_055D4F94325D",error.Message),PageMessageSeverity.Error);_queue.ReportDiagnosticException("任务历史","清除任务历史失败",error);}
        finally{_clearingHistory=false;if(!_disposed){Refresh();NotifyClearState();}}
    }
    /// <summary>当前操作提示。</summary>
    /// <summary>服务原文只作为原文展示，避免与自有提示模板同文时被翻译。</summary>
    public void ReportError(string text){if(!_disposed&&StatusMessage.Show(global::HybridMount.Services.Localization.ResourceMessage.Raw(text),PageMessageSeverity.Error))OnPropertyChanged(nameof(Status));}
    public string Status {get=>StatusMessage.Text;set=>PublishStatus(value);}
    private void PublishStatus(string text,PageMessageSeverity severity=PageMessageSeverity.Information){if(!_disposed&&StatusMessage.Show(text,severity))OnPropertyChanged(nameof(Status));}
    /// <summary>三段筛选的唯一选择：0全部、1同步问题、2冲突；切换一次只发布一次任务快照。</summary>
    public int SelectedTaskFilter {get=>_issuesOnly?1:_conflictsOnly?2:0;set=>SetTaskFilter(value);}
    public bool IssuesOnly {get=>_issuesOnly;set{if(value||_issuesOnly)SetTaskFilter(value?1:0);}}
    public bool ConflictsOnly {get=>_conflictsOnly;set{if(value||_conflictsOnly)SetTaskFilter(value?2:0);}}
    private void SetTaskFilter(int selected)
    {
        if(_disposed||selected is <0 or >2||SelectedTaskFilter==selected)return;
        _issuesOnly=selected==1;_conflictsOnly=selected==2;
        OnPropertyChanged(nameof(IssuesOnly));OnPropertyChanged(nameof(ConflictsOnly));OnPropertyChanged(nameof(SelectedTaskFilter));Refresh();
    }
    public string IssueViewTitle=>_filterMount is {} id?global::HybridMount.Services.Localization.UiText.Text("Text_2459713C2EC0",(_knownMounts.FirstOrDefault(x=>x.Definition.Id==id)?.Definition.Name??global::HybridMount.Services.Localization.UiText.Text("Text_6FC9C71CB14F"))):global::HybridMount.Services.Localization.UiText.Text("Text_73213BCEB8A1");
    public bool HasIssueFilter=>_filterMount is not null||_filterPaths.Count>0;
    public void ClearIssueFilter(){_filterMount=null;_filterPaths=[];Refresh();}
    public async Task<string?> SelectedLocationAsync()
    {
        if(_disposed||_queue.IsStopping||Selected is not {Operation:"upload" or "remoteUpdate" or "excludedRename"} selected){Status=global::HybridMount.Services.Localization.UiText.Text("Text_8EF667892EFE");return null;}
        try{return await _queue.RunClientActionAsync(()=>_queue.SyncIssueLocalPath(selected.Id));}catch(Exception error){PublishStatus(error.Message,PageMessageSeverity.Error);_queue.ReportDiagnosticException("任务页面","任务页面操作未完成",error);return null;}
    }
    private void FocusChanged()
    {
        if(_disposed)return;_dispatcher.TryEnqueue(()=>{if(!_disposed)ApplyFocus();});
    }
    private void ApplyFocus()
    {
        if(_focus?.Current is not {} focus||focus.Revision==_focusRevision)return;
        _focusRevision=focus.Revision;_filterMount=focus.MountId;_filterPaths=focus.Paths;_issuesOnly=true;_conflictsOnly=false;OnPropertyChanged(nameof(ConflictsOnly));
        OnPropertyChanged(nameof(IssuesOnly));OnPropertyChanged(nameof(SelectedTaskFilter));Refresh();SelectedJobId=Operations.FirstOrDefault()?.Id;Refreshed?.Invoke();
    }
    public string? SelectedJobId {get=>_selectedJob;set{if(!_disposed&&SetProperty(ref _selectedJob,value))OnPropertyChanged(nameof(CanCancelSelected));}}
    public bool CanCancelSelected=>!_disposed&&!_queue.IsStopping&&Selected is {State:not("Completed" or "Canceled" or "Failed" or "Superseded" or "Interrupted" or "Canceling")} selected&&selected.Operation!="freeSpace";
    public TaskListItem? SelectedRow=>Operations.FirstOrDefault(x=>x.Id==_selectedJob);
    public MountOperationSnapshot? Selected=>SelectedRow?.Snapshot;
    public string CopyPath {get=>_copyPath;set{if(!_disposed)SetProperty(ref _copyPath,value);}}
    public string CopyContentNotice=>global::HybridMount.Services.Localization.UiText.Text("Text_1501CBC59F3C");
    public async Task CancelSelectedAsync()
    {
        if(_disposed||_queue.IsStopping)return;
        if(Selected is not {} selected){Status=global::HybridMount.Services.Localization.UiText.Text("Text_A1480CEA066A");return;}
        try{var result=await _queue.RunClientActionAsync(()=>_queue.CancelSelected(selected));if(!_disposed)Status=result?selected.Operation=="excludedRename"?global::HybridMount.Services.Localization.UiText.Text("Text_1D73995960A9"):global::HybridMount.Services.Localization.UiText.Text("Text_B868FAF07C8A"):_queue.GetOperation(selected.Id)?.State=="Completed"?global::HybridMount.Services.Localization.UiText.Text("TaskAlreadyCompleted"):global::HybridMount.Services.Localization.UiText.Text("TaskChangedSelectAgain");}catch(Exception error){PublishStatus(error.Message,PageMessageSeverity.Error);_queue.ReportDiagnosticException("任务页面","任务页面操作未完成",error);}
    }
    public async Task RetrySelectedAsync()
    {
        if(_disposed||_queue.IsStopping)return;
        if(Selected is not {Operation:"upload" or "remoteUpdate" or "excludedRename" or "freeSpace"} selected){Status=global::HybridMount.Services.Localization.UiText.Text("Text_44C6E3CF9291");return;}
        try{var result=await _queue.RunClientActionAsync(()=>_queue.RetryUpload(selected.Id));if(!_disposed)Status=result?global::HybridMount.Services.Localization.UiText.Text("Text_F8FFEB435E02"):global::HybridMount.Services.Localization.UiText.Text("Text_EA215D28117F");}catch(Exception error){PublishStatus(error.Message,PageMessageSeverity.Error);_queue.ReportDiagnosticException("任务页面","任务页面操作未完成",error);}
    }
    /// <summary>只读核对改名收尾，不授权再次删除或重新上传。</summary>
    public async Task RecheckSelectedAsync()
    {
        if(_disposed||_queue.IsStopping)return;
        if(Selected is not {Operation:"upload" or "remoteUpdate" or "excludedRename" or "freeSpace"} selected){Status=global::HybridMount.Services.Localization.UiText.Text("Text_320256ADF69A");return;}
        try{var result=await _queue.RunClientActionAsync(()=>_queue.RecheckOperation(selected.Id));if(!_disposed)Status=result?global::HybridMount.Services.Localization.UiText.Text("Text_CB337BBDEC92"):global::HybridMount.Services.Localization.UiText.Text("Text_335625D8BF94");}catch(Exception error){PublishStatus(error.Message,PageMessageSeverity.Error);_queue.ReportDiagnosticException("任务页面","任务页面操作未完成",error);}
    }
    public bool BeginCopy()
    {
        if(_disposed||_queue.IsStopping||_dialogBusy)return false;
        if(Selected is not {Operation:"upload"} selected){Status=global::HybridMount.Services.Localization.UiText.Text("Text_BD7D468AF466");return false;}
        _dialogBusy=true;_dialogJob=selected.Id;
        var original=selected.ItemPath??global::HybridMount.Services.Localization.UiText.Text("Text_BFE958DEA679");var extension=Path.GetExtension(original);CopyPath=global::HybridMount.Services.Localization.UiText.Text("Text_50A225C4305F",original[..(original.Length-extension.Length)],extension);return true;
    }
    /// <summary>捕获的原任务只可提交一次；选择切换不改变副本的来源，应用服务仍校验远端目标。</summary>
    public async Task SubmitCopyAsync()
    {
        if(_disposed||_queue.IsStopping||!_dialogBusy||_dialogJob is not {} id)return;
        _dialogJob=null;var path=CopyPath.Trim();
        try{var copy=await _queue.RunClientActionAsync(()=>_queue.UploadAsCopy(id,path));if(_disposed)return;SelectedJobId=copy;Status=global::HybridMount.Services.Localization.UiText.Text("Text_7265AF89876F");}
        catch(Exception error){PublishStatus(error.Message,PageMessageSeverity.Error);_queue.ReportDiagnosticException("任务页面","任务页面操作未完成",error);}
    }
    public async Task<MountOperationSnapshot?> BeginResultsAsync()
    {
        if(_disposed||_queue.IsStopping||_dialogBusy)return null;
        if(Selected is not {} selected){Status=global::HybridMount.Services.Localization.UiText.Text("Text_A1480CEA066A");return null;}
        _dialogBusy=true;var revision=++_dialogRevision;
        try{var result=IssuesOnly?selected:await _queue.RunClientActionAsync(()=>_queue.GetOperation(selected.Id))??selected;if(_disposed||!_dialogBusy||revision!=_dialogRevision)return null;return result;}
        catch(Exception error){if(revision!=_dialogRevision||_disposed)return null;_dialogBusy=false;PublishStatus(error.Message,PageMessageSeverity.Error);_queue.ReportDiagnosticException("任务页面","任务页面操作未完成",error);return null;}
    }
    /// <summary>关闭比较只取消本次准备，不取消原冲突；已开始的受保护写入由应用完成安全收尾。</summary>
    public void EndDialog(){++_dialogRevision;_comparisonCancel?.Cancel();_comparisonCancel?.Dispose();_comparisonCancel=null;_comparison=null;_dialogBusy=false;_dialogJob=null;if(!_disposed)NotifyConflict();}
    public string ConflictTitle=>global::HybridMount.Services.Localization.UiText.Text("Text_ABEA4B3A3DF2",(_comparison?.Path??global::HybridMount.Services.Localization.UiText.Text("Text_39932F24FE11")));
    public string ConflictLocalText=>_comparison?.LocalText??global::HybridMount.Services.Localization.UiText.Text("Text_7410DAE3F11E");
    public string ConflictRemoteText=>_comparison?.RemoteText??global::HybridMount.Services.Localization.UiText.Text("Text_7410DAE3F11E");
    public string ConflictBaselineText=>_comparison?.BaselineText??"";
    public string ConflictLocalRestriction=>_comparison?.LocalRestriction??global::HybridMount.Services.Localization.UiText.Text("Text_46F09994B42F");
    public string ConflictRemoteRestriction=>_comparison?.RemoteRestriction??global::HybridMount.Services.Localization.UiText.Text("Text_46F09994B42F");
    public string ConflictRecoveryText=>_comparison?.RecoveryPath is {Length:>0} path?global::HybridMount.Services.Localization.UiText.Text("Text_484C52248816",path):"";
    public string ConflictStatus=>_conflictStatus;
    public bool CanResolveWithLocal=>!_disposed&&!_conflictBusy&&_comparison?.CanUseLocal==true;
    public bool CanResolveWithRemote=>!_disposed&&!_conflictBusy&&_comparison?.CanUseRemote==true;
    public bool CanRefreshConflict=>!_disposed&&!_conflictBusy;
    private void NotifyConflict(){foreach(var property in new[]{nameof(ConflictTitle),nameof(ConflictLocalText),nameof(ConflictRemoteText),nameof(ConflictBaselineText),nameof(ConflictLocalRestriction),nameof(ConflictRemoteRestriction),nameof(ConflictRecoveryText),nameof(ConflictStatus),nameof(CanResolveWithLocal),nameof(CanResolveWithRemote),nameof(CanRefreshConflict)})OnPropertyChanged(property);}
    /// <summary>捕获原事务ID，选择切换不会把比较授权转移到其他文件。</summary>
    public async Task<bool> BeginConflictAsync()
    {
        if(_disposed||_queue.IsStopping||_dialogBusy||_conflictBusy)return false;
        if(Selected?.Operation=="excludedRename"){Status=global::HybridMount.Services.Localization.UiText.Text("Text_B659FC3F6B67");return false;}
        if(Selected is not{Operation:"upload" or "remoteUpdate"} selected){Status=global::HybridMount.Services.Localization.UiText.Text("Text_B5852AAAA8E8");return false;}
        var owner=new CancellationTokenSource();_dialogBusy=true;_dialogJob=selected.Id;_comparisonCancel=owner;await RefreshConflictAsync();return !_disposed&&_dialogBusy&&_dialogJob==selected.Id&&_comparisonCancel==owner;
    }
    public async Task RefreshConflictAsync()
    {
        if(_disposed||_conflictBusy||!_dialogBusy||_dialogJob is not {} id||_comparisonCancel is null)return;
        var owner=_comparisonCancel;_conflictBusy=true;_comparison=null;_conflictStatus=global::HybridMount.Services.Localization.UiText.Text("Text_0BAA8611E301");NotifyConflict();
        try{var compared=await _queue.CompareConflictAsync(id,owner.Token);if(!_disposed&&_dialogJob==id&&_comparisonCancel==owner){_comparison=compared;_conflictStatus=global::HybridMount.Services.Localization.UiText.Text("Text_6DB506073EE6",compared.ObservedUtc.ToLocalTime().ToString("G"));}}
        catch(OperationCanceledException){if(!_disposed&&_comparisonCancel==owner)_conflictStatus=global::HybridMount.Services.Localization.UiText.Text("Text_19094B4131D7");}
        catch(Exception error){if(!_disposed&&_comparisonCancel==owner)_conflictStatus=error.Message;_queue.ReportDiagnosticException("冲突处理","任务页面冲突操作未完成",error);_queue.ReportDiagnosticException("任务页面","任务页面操作未完成",error);}
        finally{_conflictBusy=false;if(!_disposed)NotifyConflict();}
    }
    /// <summary>直接选择调用独立条件取舍流程；失败使旧比较失效，禁止重复点击重发。</summary>
    public async Task<bool> ResolveConflictAsync(bool useLocal)
    {
        if(!(useLocal?CanResolveWithLocal:CanResolveWithRemote)||_comparison is not {} compared||_comparisonCancel is null)return false;
        _conflictBusy=true;_conflictStatus=useLocal?global::HybridMount.Services.Localization.UiText.Text("Text_3692B75B6E6E"):global::HybridMount.Services.Localization.UiText.Text("Text_B0F181CAFC5A");NotifyConflict();
        try{var result=await _queue.ResolveConflictAsync(compared,useLocal,_comparisonCancel.Token);if(!_disposed){Status=result;_conflictStatus=result;}return true;}
        catch(OperationCanceledException){if(!_disposed)_conflictStatus=global::HybridMount.Services.Localization.UiText.Text("Text_40BB5C6B64B7");return false;}
        catch(Exception error){if(!_disposed)_conflictStatus=error.Message;_queue.ReportDiagnosticException("冲突处理","任务页面冲突操作未完成",error);_queue.ReportDiagnosticException("任务页面","任务页面操作未完成",error);return false;}
        finally{_comparison=null;_conflictBusy=false;if(!_disposed){Refresh();NotifyConflict();}}
    }
    /// <summary>在 UI 线程订阅应用级任务；释放时解除订阅。</summary>
    public TasksViewModel(MountManager manager,IUiDispatcher dispatcher,TaskFocusService? focus=null):this(manager.TasksQueue,dispatcher,focus) { }
    public TasksViewModel(TasksQueueService queue,IUiDispatcher dispatcher,TaskFocusService? focus=null)
    {
        _dispatcher=dispatcher;_queue=queue;_focus=focus;
        _snapshots=new(dispatcher,ReadSnapshot,PublishSnapshot,SnapshotFailed);
        _queue.Changed+=Changed;if(focus is not null)focus.Changed+=FocusChanged;Refresh();ApplyFocus();
    }
    private void Changed(){if(!_disposed)_snapshots.Request(Volatile.Read(ref _query));}
    private void Refresh()
    {
        if(_disposed)return;var query=new SnapshotQuery(_issuesOnly,_conflictsOnly,_filterMount,_filterPaths.ToArray());
        Volatile.Write(ref _query,query);_snapshots.Request(query);
    }
    /// <summary>数据库批量读取、路径整理、速度计算与排序只由单一后台快照泵执行。</summary>
    private PageSnapshot ReadSnapshot(SnapshotQuery query)
    {
        var all=_queue.Operations.ToArray();
        var source=query.Issues||query.Conflicts?all.Where(x=>query.Conflicts?x.IssueKind==TaskIssueKind.Conflict:x.IssueKind!=TaskIssueKind.Normal)
            .Where(x=>(query.Mount is null||x.MountId==query.Mount)&&(query.Paths.Count==0||query.Paths.Any(path=>path.Length==0||x.ItemPath?.Equals(path,StringComparison.OrdinalIgnoreCase)==true||x.ItemPath?.StartsWith(path+"/",StringComparison.OrdinalIgnoreCase)==true))):all;
        var now=System.Diagnostics.Stopwatch.GetTimestamp();var snapshots=OperationDisplayOrder.Sort(source.Select(value=>_rates.Apply(value,now))).ToArray();
        _rates.Retain(snapshots.Select(value=>value.Id).ToHashSet(StringComparer.Ordinal));return new(snapshots,all,_queue.Mounts.ToArray());
    }
    private void PublishSnapshot(PageSnapshot snapshot)
    {
        if(_disposed)return;_allForActions=snapshot.All;_knownMounts=snapshot.Mounts;
        OnPropertyChanged(nameof(IssueViewTitle));OnPropertyChanged(nameof(HasIssueFilter));
        var structure=!Operations.Select(row=>row.Id).SequenceEqual(snapshot.Visible.Select(value=>value.Id),StringComparer.Ordinal);
        if(structure)Refreshing?.Invoke();TaskListItem.Reconcile(Operations,snapshot.Visible);
        if(_selectedJob is null&&HasIssueFilter)SelectedJobId=Operations.FirstOrDefault()?.Id;
        NotifyClearState();OnPropertyChanged(nameof(Selected));OnPropertyChanged(nameof(CanCancelSelected));if(structure)Refreshed?.Invoke();
    }
    private void SnapshotFailed(Exception error)
    {
        if(_disposed)return;if(StatusMessage.Update(global::HybridMount.Services.Localization.ResourceMessage.Raw(error.Message),PageMessageSeverity.Error))OnPropertyChanged(nameof(Status));
        _queue.ReportDiagnosticException("任务页面","任务页面操作未完成",error);
    }
    /// <summary>隔离回归等待已请求快照；实际发布仍使用注入的Dispatcher。</summary>
    internal Task SnapshotIdle=>_snapshots.WhenIdle;
    /// <summary>解除快照订阅；不取消任何后台工作。</summary>
    public void Dispose() { EndDialog();_disposed=true;_snapshots.Dispose();StatusMessage.Dispose();_queue.Changed-=Changed;if(_focus is not null)_focus.Changed-=FocusChanged; }
}
