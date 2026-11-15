using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Commons.Tasks;
using HybridMount.Commons.UI;
using HybridMount.Models;
using HybridMount.Models.Tasks;
using HybridMount.Services.Navigation;
using HybridMount.UI.Binding;
using HybridMount.UI.Localization;
using System.Collections.ObjectModel;
using HybridMount.Models.Localization;
namespace HybridMount.ViewModels;

/// <summary>首页只绑定应用级概览并请求导航；UI发布按250ms合并，隐藏后停止发布，不干预后台统计或执行。</summary>
public sealed class HomeViewModel : ObservableObject, IDisposable
{
    private readonly string _version;
    private readonly INavigationService _navigation;
    private readonly ITaskOverview _overview;
    private readonly TaskFocusService _focus;
    private readonly BackgroundSnapshotPump<long, TaskOverviewSnapshot> _snapshots;
    private volatile bool _visible, _disposed;
    private long _generation;
    private TaskOverviewSnapshot _snapshot = TaskOverviewSnapshot.Empty;
    private string? _readError;
    public ObservableCollection<TaskListItemViewModel> RunningTasks { get; } = [];
    public HomeViewModel(string version, INavigationService navigation, ITaskOverview overview, TaskFocusService focus, IUIDispatcher dispatcher)
    {
        _version = version; _navigation = navigation; _overview = overview; _focus = focus;
        _snapshots = new(dispatcher, _ => overview.Current, Publish, error =>
        {
            if (!_visible || _disposed) return;
            _readError = $"{error.GetType().Name} (0x{error.HResult:X8})";
            NotifyTexts();
        });
        overview.Changed += OverviewChanged;
    }
    public string VersionText => ResourceMessage.Create("Version", _version).Render();
    public bool HasRound => _snapshot.HasRound;
    public double ProgressPercent => _snapshot.Percent;
    public string ProgressText => HasRound ? ResourceMessage.Create("HomeOverviewProgress", _snapshot.Ended, _snapshot.Total).Render() : "";
    public string ResultsText => HasRound ? ResourceMessage.Create("HomeOverviewResults", _snapshot.Succeeded, _snapshot.Failed, _snapshot.Canceled, _snapshot.Skipped).Render() : "";
    public string MoreRunningText => _snapshot.AdditionalRunning > 0 ? ResourceMessage.Create("HomeOverviewMoreRunning", _snapshot.AdditionalRunning).Render() : "";
    public string UpdateError => (_readError ?? _snapshot.Error) is { } error ? ResourceMessage.Create("HomeOverviewUpdateFailed", error).Render() : "";
    public string StateText => !_snapshot.Initialized ? ResourceMessage.Create("HomeOverviewLoading").Render() : !HasRound
        ? ResourceMessage.Create(string.IsNullOrEmpty(UpdateError) ? "HomeOverviewEmpty" : "HomeOverviewUnavailable").Render()
        : _snapshot.IsClosed ? ResourceMessage.Create(_snapshot.Succeeded == _snapshot.Total ? "HomeOverviewAllSucceeded" : "HomeOverviewEnded").Render()
        : _snapshot.RunningCount == 0 ? ResourceMessage.Create("HomeOverviewWaiting").Render() : ResourceMessage.Create("HomeOverviewRunning", _snapshot.RunningCount).Render();

    /// <summary>由页面导航及窗口可见性调用；旧派发在发布时再次核对可见状态。</summary>
    public void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible) return;
        _visible = visible; ++_generation;
        if (visible) _snapshots.Request(_generation);
    }
    private void OverviewChanged() { if (_visible && !_disposed) _snapshots.Request(_generation); }
    private void Publish(TaskOverviewSnapshot snapshot)
    {
        if (_disposed || !_visible) return;
        _snapshot = snapshot; _readError = null;
        TaskListItemViewModel.Reconcile(RunningTasks, snapshot.RunningTasks.Select(task => task.MountId is null
            ? task with { MountName = ResourceMessage.Create("HomeApplicationTasks").Render() } : task).ToArray());
        NotifyTexts();
    }
    public void RefreshLanguage()
    {
        if (_disposed) return;
        if (_visible) _snapshots.Request(_generation);
        NotifyTexts(); OnPropertyChanged(nameof(VersionText));
    }
    private void NotifyTexts()
    {
        OnPropertyChanged(nameof(StateText)); OnPropertyChanged(nameof(HasRound)); OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(ResultsText)); OnPropertyChanged(nameof(MoreRunningText)); OnPropertyChanged(nameof(UpdateError));
    }
    internal Task WhenIdle => _snapshots.WhenIdle;
    public bool OpenMounts() => !_disposed && _navigation.Navigate(ApplicationPage.Mounts);
    public bool OpenSettings() => !_disposed && _navigation.Navigate(ApplicationPage.Settings);
    public bool OpenLogs() => !_disposed && _navigation.Navigate(ApplicationPage.Logs);
    public bool OpenTasks() => !_disposed && _focus.ShowAll();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _visible = false; _overview.Changed -= OverviewChanged; _snapshots.Dispose();
    }
}
