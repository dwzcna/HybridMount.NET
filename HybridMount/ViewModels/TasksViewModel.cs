using HybridMount.UI.Binding;
using HybridMount.Models.Mounting.Conflicts;
using HybridMount.Models.UI;
using HybridMount.Commons.UI;
using HybridMount.Helpers.Threading;
using HybridMount.Models.Localization;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.Services.Navigation;
using HybridMount.Services.Scheduling;
using HybridMount.Services.Sync;
using HybridMount.UI.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
namespace HybridMount.ViewModels;

/// <summary>将应用级后台任务快照合并派发到 UI，不拥有或停止任务。</summary>
public sealed class TasksViewModel : ObservableObject, IDisposable
{
    private readonly TasksQueueService _queue;
    private readonly IUIDispatcher _dispatcher;
    private sealed record SnapshotQuery(bool Issues, bool Conflicts, Guid? Mount, IReadOnlyList<string> Paths);
    private sealed record PageSnapshot(MountOperationSnapshot[] Visible, MountOperationSnapshot[] All, MountSnapshot[] Mounts);
    private readonly BackgroundSnapshotPump<SnapshotQuery, PageSnapshot> _snapshots;
    private SnapshotQuery _query = new(false, false, null, Array.Empty<string>());
    private MountOperationSnapshot[] _allForActions = [];
    private MountSnapshot[] _knownMounts = [];
    private readonly TaskTransferRate _rates = new();
    private readonly TaskFocusService? _focus;
    private long _focusRevision, _dialogRevision;
    private bool _issuesOnly, _conflictsOnly;
    private ConflictComparison? _comparison;
    private CancellationTokenSource? _comparisonCancel;
    private bool _conflictBusy;
    private string _conflictStatus = "";
    private Guid? _filterMount;
    private IReadOnlyList<string> _filterPaths = [];
    private bool _disposed, _clearingHistory, _dialogBusy;
    private string? _publishedIssueTitle;
    private bool? _publishedIssueFilter, _publishedCancel;
    private (bool Completed, bool Failed, bool All)? _publishedClear;
    private string? _selectedJob, _dialogJob;
    private string _copyPath = "";
    /// <summary>本页可关闭提示；后台刷新不重新打开已关闭消息。</summary>
    public PageStatusViewModel StatusMessage { get; } = new();
    /// <summary>执行中、排队／等待和历史分组展示，同组使用稳定提交顺序。</summary>
    public ObservableCollection<TaskListItemViewModel> Operations { get; } = [];
    /// <summary>刷新后由页面按任务 ID 恢复选择。</summary>
    public event Action? Refreshing;
    public event Action? Refreshed;
    public bool CanClearCompleted => CanClear(TaskHistoryClearMode.Completed);
    public bool CanClearFailed => CanClear(TaskHistoryClearMode.Failed);
    public bool CanClearAll => CanClear(TaskHistoryClearMode.All);
    private bool CanClear(TaskHistoryClearMode mode) => !_disposed && !_queue.IsStopping && !_clearingHistory && _allForActions.Any(operation => TaskHistoryRules.Matches(mode, operation.State));
    /// <summary>普通进度不重复发布未变化的命令可用性，保留真实终态、选择和退出的即时反馈。</summary>
    private void NotifyClearState()
    {
        var next = (Completed: CanClearCompleted, Failed: CanClearFailed, All: CanClearAll);
        if (_publishedClear?.Completed != next.Completed) OnPropertyChanged(nameof(CanClearCompleted));
        if (_publishedClear?.Failed != next.Failed) OnPropertyChanged(nameof(CanClearFailed));
        if (_publishedClear?.All != next.All) OnPropertyChanged(nameof(CanClearAll));
        _publishedClear = next;
    }
    private void NotifyCancelState()
    {
        var next = CanCancelSelected;
        if (_publishedCancel != next) { _publishedCancel = next; OnPropertyChanged(nameof(CanCancelSelected)); }
    }
    /// <summary>页头范围与任务字段分开；挂载名和实际筛选变化才通知，不用每条快照驱动重排。</summary>
    private void PublishHeaderState()
    {
        var title = IssueViewTitle; var filter = HasIssueFilter;
        if (_publishedIssueTitle != title) { _publishedIssueTitle = title; OnPropertyChanged(nameof(IssueViewTitle)); }
        if (_publishedIssueFilter != filter) { _publishedIssueFilter = filter; OnPropertyChanged(nameof(HasIssueFilter)); }
    }
    public Task ClearCompletedAsync() => ClearHistoryAsync(TaskHistoryClearMode.Completed);
    public Task ClearFailedAsync() => ClearHistoryAsync(TaskHistoryClearMode.Failed);
    public Task ClearAllAsync() => ClearHistoryAsync(TaskHistoryClearMode.All);
    /// <summary>三种清理共享防重入；页面离开不取消已接纳工作，失败或退出不产生迟到更新。</summary>
    private async Task ClearHistoryAsync(TaskHistoryClearMode mode)
    {
        if (!CanClear(mode)) return;
        _clearingHistory = true; NotifyClearState();
        var label = mode == TaskHistoryClearMode.Completed ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Completed", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : mode == TaskHistoryClearMode.Failed ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("FailedAndPartiallyFailed", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Ended", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
        try
        {
            var count = await _queue.ClearOperationHistoryAsync(mode);
            if (!_disposed) Status = count == 0 ? string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NoHistoryEntriesCanBeClearedActiveTasksAndTasksAwaitingRecovery", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, label) : string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ClearedTaskHistoryEntriesActiveAndPendingTasksWereKept", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, count, label);
        }
        catch (OperationCanceledException) when (_queue.IsStopping) { if (!_disposed) Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TheApplicationIsExitingCleanupHasStopped", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; }
        catch (Exception error) { if (!_disposed) PublishStatus(string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CouldNotClear", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, error.Message), PageMessageSeverity.Error); _queue.ReportDiagnosticException("任务历史", "清除任务历史失败", error); }
        finally { _clearingHistory = false; if (!_disposed) { Refresh(); NotifyClearState(); } }
    }
    /// <summary>当前操作提示。</summary>
    /// <summary>服务原文只作为原文展示，避免与自有提示模板同文时被翻译。</summary>
    public void ReportError(string text) { if (!_disposed && StatusMessage.Show(global::HybridMount.Models.Localization.ResourceMessage.Raw(text), PageMessageSeverity.Error)) OnPropertyChanged(nameof(Status)); }
    public string Status { get => StatusMessage.Text; set => PublishStatus(value); }
    private void PublishStatus(string text, PageMessageSeverity severity = PageMessageSeverity.Information) { if (!_disposed && StatusMessage.Show(text, severity)) OnPropertyChanged(nameof(Status)); }
    /// <summary>三段筛选的唯一选择：0全部、1同步问题、2冲突；切换一次只发布一次任务快照。</summary>
    public int SelectedTaskFilter { get => _issuesOnly ? 1 : _conflictsOnly ? 2 : 0; set => SetTaskFilter(value); }
    public bool IssuesOnly { get => _issuesOnly; set { if (value || _issuesOnly) SetTaskFilter(value ? 1 : 0); } }
    public bool ConflictsOnly { get => _conflictsOnly; set { if (value || _conflictsOnly) SetTaskFilter(value ? 2 : 0); } }
    private void SetTaskFilter(int selected)
    {
        if (_disposed || selected is < 0 or > 2 || SelectedTaskFilter == selected) return;
        _issuesOnly = selected == 1; _conflictsOnly = selected == 2;
        OnPropertyChanged(nameof(IssuesOnly)); OnPropertyChanged(nameof(ConflictsOnly)); OnPropertyChanged(nameof(SelectedTaskFilter)); Refresh();
    }
    public string IssueViewTitle => _filterMount is { } id ? string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SyncIssues", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, (_knownMounts.FirstOrDefault(x => x.Definition.Id == id)?.Definition.Name ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Mount", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!)) : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SyncIssuesDetails", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public bool HasIssueFilter => _filterMount is not null || _filterPaths.Count > 0;
    public void ClearIssueFilter() { _filterMount = null; _filterPaths = []; PublishHeaderState(); Refresh(); }
    public async Task<string?> SelectedLocationAsync()
    {
        if (_disposed || _queue.IsStopping || Selected is not { Operation: "upload" or "remoteUpdate" or "excludedRename" or "exportConfiguration" } selected) { Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SelectASyncIssueFirst", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; return null; }
        try { return await _queue.RunClientActionAsync(() => _queue.TaskLocalPath(selected.Id)); } catch (Exception error) { PublishStatus(error.Message, PageMessageSeverity.Error); _queue.ReportDiagnosticException("任务页面", "任务页面操作未完成", error); return null; }
    }
    private void FocusChanged()
    {
        if (_disposed) return; _dispatcher.TryEnqueue(() => { if (!_disposed) ApplyFocus(); });
    }
    private string? _pendingTaskFocus;
    public long FocusRevision=>_focusRevision;
    public string? RequestedTaskId=>_focus?.Current?.TaskId;
    private void ApplyFocus()
    {
        if (_focus?.Current is not { } focus || focus.Revision == _focusRevision) return;
        _pendingTaskFocus=focus.TaskId;_focusRevision = focus.Revision; _filterMount = focus.MountId; _filterPaths = focus.Paths; _issuesOnly = focus.IssuesOnly; _conflictsOnly = false; OnPropertyChanged(nameof(ConflictsOnly));
        OnPropertyChanged(nameof(IssuesOnly)); OnPropertyChanged(nameof(SelectedTaskFilter)); PublishHeaderState(); Refresh(); SelectedJobId = focus.TaskId??Operations.FirstOrDefault()?.Id; Refreshed?.Invoke();
    }
    /// <summary>详情返回入口只面向当前仍有配置的连接任务；页面不直接查询业务执行器。</summary>
    public bool CanReturnToConfiguration(MountOperationSnapshot task)=>task.Operation=="connect"&&task.MountId is {} mount&&_knownMounts.Any(item=>item.Definition.Id==mount);
    public string? SelectedJobId { get => _selectedJob; set { if (!_disposed && SetProperty(ref _selectedJob, value)) NotifyCancelState(); } }
    public bool CanCancelSelected => !_disposed && !_queue.IsStopping && Selected is { State: not ("Completed" or "Canceled" or "Failed" or "Superseded" or "Interrupted" or "Canceling") } selected && selected.Operation != "freeSpace" && (selected.Operation != "capabilityProbe" || selected.State is "Running" or "Queued");
    public TaskListItemViewModel? SelectedRow => Operations.FirstOrDefault(x => x.Id == _selectedJob);
    public MountOperationSnapshot? Selected => SelectedRow?.Snapshot;
    public string CopyPath { get => _copyPath; set { if (!_disposed) SetProperty(ref _copyPath, value); } }
    public string CopyContentNotice => global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("IfAnUploadSnapshotExistsTheCopyUsesThatSnapshotAndLater", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public async Task CancelSelectedAsync()
    {
        if (_disposed || _queue.IsStopping) return;
        if (Selected is not { } selected) { Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SelectATaskFirst", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; return; }
        try { var result = await _queue.RunClientActionAsync(() => _queue.CancelSelected(selected)); if (!_disposed) Status = result ? selected.Operation == "excludedRename" ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CleanupCancellationRequestedCompletedCloudRenamesOrDestinationCopiesWillBeKept", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CancellationRequestedCompletedStepsAreKeptAndDeletedContentWillNotBe", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : _queue.GetOperation(selected.Id)?.State == "Completed" ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TaskAlreadyCompleted", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TaskChangedSelectAgain", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; } catch (Exception error) { PublishStatus(error.Message, PageMessageSeverity.Error); _queue.ReportDiagnosticException("任务页面", "任务页面操作未完成", error); }
    }
    public static bool CanReexport(MountOperationSnapshot task)=>task.Operation=="exportConfiguration"&&task.State is "Failed" or "Interrupted" or "Canceled";
    public async Task RetrySelectedAsync()
    {
        if (_disposed || _queue.IsStopping) return;
        if(Selected is {} export&&CanReexport(export)){_focus?.ShowExport(export.BackupProtected==true);return;}
        if (Selected is not { Operation: "upload" or "remoteUpdate" or "excludedRename" or "freeSpace" } selected) { Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("FirstSelectAnUploadRemoteUpdateOrExcludedRenameCleanupTask", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; return; }
        try { var result = await _queue.RunClientActionAsync(() => _queue.RetryUpload(selected.Id)); if (!_disposed) Status = result ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RetrySubmittedIfTheMountIsDisconnectedConnectItOnTheMounts", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ThisTaskCannotBeRetried", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; } catch (Exception error) { PublishStatus(error.Message, PageMessageSeverity.Error); _queue.ReportDiagnosticException("任务页面", "任务页面操作未完成", error); }
    }
    /// <summary>只读核对改名收尾，不授权再次删除或重新上传。</summary>
    public async Task RecheckSelectedAsync()
    {
        if (_disposed || _queue.IsStopping) return;
        if (Selected is not { Operation: "upload" or "remoteUpdate" or "excludedRename" or "freeSpace" } selected) { Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SelectAnUploadRemoteUpdateOrExcludedRenameCleanupTask", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; return; }
        try { var result = await _queue.RunClientActionAsync(() => _queue.RecheckOperation(selected.Id)); if (!_disposed) Status = result ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ReadOnlyCheckRequestedThisDoesNotAuthorizeOverwritingOrResending", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TheTaskIsRunningOrHasAlreadyEnded", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; } catch (Exception error) { PublishStatus(error.Message, PageMessageSeverity.Error); _queue.ReportDiagnosticException("任务页面", "任务页面操作未完成", error); }
    }
    public bool BeginCopy()
    {
        if (_disposed || _queue.IsStopping || _dialogBusy) return false;
        if (Selected is not { Operation: "upload" } selected) { Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SelectAnUploadTaskFirst", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; return false; }
        _dialogBusy = true; _dialogJob = selected.Id;
        var original = selected.ItemPath ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CopyNotice", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; var extension = Path.GetExtension(original); CopyPath = string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Copy", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, original[..(original.Length - extension.Length)], extension); return true;
    }
    /// <summary>捕获的原任务只可提交一次；选择切换不改变副本的来源，应用服务仍校验远端目标。</summary>
    public async Task SubmitCopyAsync()
    {
        if (_disposed || _queue.IsStopping || !_dialogBusy || _dialogJob is not { } id) return;
        _dialogJob = null; var path = CopyPath.Trim();
        try { var copy = await _queue.RunClientActionAsync(() => _queue.UploadAsCopy(id, path)); if (_disposed) return; SelectedJobId = copy; Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CopyTaskSubmittedAnExistingDestinationWillNotBeOverwritten", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; }
        catch (Exception error) { PublishStatus(error.Message, PageMessageSeverity.Error); _queue.ReportDiagnosticException("任务页面", "任务页面操作未完成", error); }
    }
    public async Task<MountOperationSnapshot?> BeginResultsAsync()
    {
        if (_disposed || _queue.IsStopping || _dialogBusy) return null;
        if (Selected is not { } selected) { Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SelectATaskFirst", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; return null; }
        _dialogBusy = true; var revision = ++_dialogRevision;
        try { var result = IssuesOnly ? selected : await _queue.RunClientActionAsync(() => _queue.GetOperation(selected.Id)) ?? selected; if (_disposed || !_dialogBusy || revision != _dialogRevision) return null; return result; }
        catch (Exception error) { if (revision != _dialogRevision || _disposed) return null; _dialogBusy = false; PublishStatus(error.Message, PageMessageSeverity.Error); _queue.ReportDiagnosticException("任务页面", "任务页面操作未完成", error); return null; }
    }
    /// <summary>关闭比较只取消本次准备，不取消原冲突；已开始的受保护写入由应用完成安全收尾。</summary>
    public void EndDialog() { ++_dialogRevision; _comparisonCancel?.Cancel(); _comparisonCancel?.Dispose(); _comparisonCancel = null; _comparison = null; _dialogBusy = false; _dialogJob = null; if (!_disposed) NotifyConflict(); }
    public string ConflictTitle => string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ResolveConflictDetails", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, (_comparison?.Path ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("File", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!));
    public string ConflictLocalText => _comparison?.LocalText ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NotCheckedYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public string ConflictRemoteText => _comparison?.RemoteText ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NotCheckedYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public string ConflictBaselineText => _comparison?.BaselineText ?? "";
    public string ConflictLocalRestriction => _comparison?.LocalRestriction ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CheckAgainFirst", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public string ConflictRemoteRestriction => _comparison?.RemoteRestriction ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CheckAgainFirst", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public string ConflictRecoveryText => _comparison?.RecoveryPath is { Length: > 0 } path ? string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("LocalRecoveryBackupKeptAt", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, path) : "";
    public string ConflictStatus => _conflictStatus;
    public bool CanResolveWithLocal => !_disposed && !_conflictBusy && _comparison?.CanUseLocal == true;
    public bool CanResolveWithRemote => !_disposed && !_conflictBusy && _comparison?.CanUseRemote == true;
    public bool CanRefreshConflict => !_disposed && !_conflictBusy;
    private void NotifyConflict() { foreach (var property in new[] { nameof(ConflictTitle), nameof(ConflictLocalText), nameof(ConflictRemoteText), nameof(ConflictBaselineText), nameof(ConflictLocalRestriction), nameof(ConflictRemoteRestriction), nameof(ConflictRecoveryText), nameof(ConflictStatus), nameof(CanResolveWithLocal), nameof(CanResolveWithRemote), nameof(CanRefreshConflict) }) OnPropertyChanged(property); }
    /// <summary>捕获原事务ID，选择切换不会把比较授权转移到其他文件。</summary>
    public async Task<bool> BeginConflictAsync()
    {
        if (_disposed || _queue.IsStopping || _dialogBusy || _conflictBusy) return false;
        if (Selected?.Operation == "excludedRename") { Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("UseCheckAgainRetryOrCancelCleanupForThisTaskAnExisting", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; return false; }
        if (Selected is not { Operation: "upload" or "remoteUpdate" } selected) { Status = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SelectAFileConflictFirst", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; return false; }
        var owner = new CancellationTokenSource(); _dialogBusy = true; _dialogJob = selected.Id; _comparisonCancel = owner; await RefreshConflictAsync(); return !_disposed && _dialogBusy && _dialogJob == selected.Id && _comparisonCancel == owner;
    }
    public async Task RefreshConflictAsync()
    {
        if (_disposed || _conflictBusy || !_dialogBusy || _dialogJob is not { } id || _comparisonCancel is null) return;
        var owner = _comparisonCancel; _conflictBusy = true; _comparison = null; _conflictStatus = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CheckingBothSidesWithoutMakingChanges", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; NotifyConflict();
        try { var compared = await _queue.CompareConflictAsync(id, owner.Token); if (!_disposed && _dialogJob == id && _comparisonCancel == owner) { _comparison = compared; _conflictStatus = string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CheckedAt", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, compared.ObservedUtc.ToLocalTime().ToString("G")); } }
        catch (OperationCanceledException) { if (!_disposed && _comparisonCancel == owner) _conflictStatus = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CheckingStopped", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; }
        catch (Exception error) { if (!_disposed && _comparisonCancel == owner) _conflictStatus = error.Message; _queue.ReportDiagnosticException("冲突处理", "任务页面冲突操作未完成", error); _queue.ReportDiagnosticException("任务页面", "任务页面操作未完成", error); }
        finally { _conflictBusy = false; if (!_disposed) NotifyConflict(); }
    }
    /// <summary>直接选择调用独立条件取舍流程；失败使旧比较失效，禁止重复点击重发。</summary>
    public async Task<bool> ResolveConflictAsync(bool useLocal)
    {
        if (!(useLocal ? CanResolveWithLocal : CanResolveWithRemote) || _comparison is not { } compared || _comparisonCancel is null) return false;
        _conflictBusy = true; _conflictStatus = useLocal ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("WaitingForTheCurrentTaskToFinishThenFreezingLocalContentAnd", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("WaitingForTheCurrentTaskToFinishThenBackingUpLocalContent", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; NotifyConflict();
        try { var result = await _queue.ResolveConflictAsync(compared, useLocal, _comparisonCancel.Token); if (!_disposed) { Status = result; _conflictStatus = result; } return true; }
        catch (OperationCanceledException) { if (!_disposed) _conflictStatus = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ProcessingStoppedSavedBackupsAndRecoveryStagesAreKept", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!; return false; }
        catch (Exception error) { if (!_disposed) _conflictStatus = error.Message; _queue.ReportDiagnosticException("冲突处理", "任务页面冲突操作未完成", error); _queue.ReportDiagnosticException("任务页面", "任务页面操作未完成", error); return false; }
        finally { _comparison = null; _conflictBusy = false; if (!_disposed) { Refresh(); NotifyConflict(); } }
    }
    /// <summary>在 UI 线程订阅应用级任务；释放时解除订阅。</summary>
    public TasksViewModel(MountManager manager, IUIDispatcher dispatcher, TaskFocusService? focus = null) : this(manager.TasksQueue, dispatcher, focus) { }
    public TasksViewModel(TasksQueueService queue, IUIDispatcher dispatcher, TaskFocusService? focus = null)
    {
        _dispatcher = dispatcher; _queue = queue; _focus = focus;
        _snapshots = new(dispatcher, ReadSnapshot, PublishSnapshot, SnapshotFailed);
        _queue.Changed += Changed; if (focus is not null) focus.Changed += FocusChanged; Refresh(); ApplyFocus();
    }
    private void Changed() { if (!_disposed) _snapshots.Request(Volatile.Read(ref _query)); }
    private void Refresh()
    {
        if (_disposed) return; var query = new SnapshotQuery(_issuesOnly, _conflictsOnly, _filterMount, _filterPaths.ToArray());
        Volatile.Write(ref _query, query); _snapshots.Request(query);
    }
    /// <summary>数据库批量读取、路径整理、速度计算与排序只由单一后台快照泵执行。</summary>
    private PageSnapshot ReadSnapshot(SnapshotQuery query)
    {
        var all = _queue.ReadOperations(query.Issues || query.Conflicts).ToArray();
        var source = query.Issues || query.Conflicts ? all.Where(x => query.Conflicts ? x.IssueKind == TaskIssueKind.Conflict : x.IssueKind != TaskIssueKind.Normal)
            .Where(x => (query.Mount is null || x.MountId == query.Mount) && (query.Paths.Count == 0 || query.Paths.Any(path => path.Length == 0 || x.ItemPath?.Equals(path, StringComparison.OrdinalIgnoreCase) == true || x.ItemPath?.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) == true))) : all;
        var now = System.Diagnostics.Stopwatch.GetTimestamp(); var snapshots = OperationDisplayOrder.Sort(source.Select(value => _rates.Apply(value, now))).ToArray();
        _rates.Retain(snapshots.Select(value => value.Id).ToHashSet(StringComparer.Ordinal)); return new(snapshots, all, _queue.Mounts.ToArray());
    }
    private void PublishSnapshot(PageSnapshot snapshot)
    {
        if (_disposed) return; _allForActions = snapshot.All; _knownMounts = snapshot.Mounts;
        PublishHeaderState();
        var structure = !Operations.Select(row => row.Id).SequenceEqual(snapshot.Visible.Select(value => value.Id), StringComparer.Ordinal);
        if (structure) Refreshing?.Invoke(); TaskListItemViewModel.Reconcile(Operations, snapshot.Visible);
        if (_selectedJob is null && HasIssueFilter) SelectedJobId = Operations.FirstOrDefault()?.Id;
        if(_pendingTaskFocus is {} target&&Operations.Any(row=>row.Id==target)){SelectedJobId=target;_pendingTaskFocus=null;Refreshed?.Invoke();}
        NotifyClearState(); OnPropertyChanged(nameof(Selected)); NotifyCancelState(); if (structure) Refreshed?.Invoke();
    }
    private void SnapshotFailed(Exception error)
    {
        if (_disposed) return; if (StatusMessage.Update(global::HybridMount.Models.Localization.ResourceMessage.Raw(error.Message), PageMessageSeverity.Error)) OnPropertyChanged(nameof(Status));
        _queue.ReportDiagnosticException("任务页面", "任务页面操作未完成", error);
    }
    /// <summary>隔离回归等待已请求快照；实际发布仍使用注入的Dispatcher。</summary>
    internal Task SnapshotIdle => _snapshots.WhenIdle;
    /// <summary>解除快照订阅；不取消任何后台工作。</summary>
    public void Dispose() { EndDialog(); _disposed = true; _snapshots.Dispose(); StatusMessage.Dispose(); _queue.Changed -= Changed; if (_focus is not null) _focus.Changed -= FocusChanged; }
}
