using HybridMount.Commons.Tasks;
using HybridMount.Models.Mounting;
using HybridMount.Models.Tasks;
namespace HybridMount.Services.Tasks;

/// <summary>应用寿命的只读轮次统计。逐条接纳已提交事件、以常量成本维护计数；不依赖任务页历史和UI刷新。</summary>
public sealed class TaskOverviewService : ITaskOverview, IAsyncDisposable
{
    private sealed record Seen(long Revision, long Attempt, bool Terminal);
    private readonly object _gate = new();
    private readonly ITaskLifecycleSource _source;
    private readonly Func<IReadOnlyDictionary<Guid, MountDefinition>> _definitions;
    private readonly Dictionary<string, Seen> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskLifecycleEntry> _members = new(StringComparer.Ordinal);
    private readonly List<TaskLifecycleCommit> _initialCommits = [];
    private readonly Task _initialization;
    private bool _initialized, _disposed, _seeding, _seedFailed, _retrying;
    private Task _recovery = Task.CompletedTask;
    private string? _startupError;
    private long _round, _order;
    private int _ended, _succeeded, _failed, _canceled, _skipped;
    private string? _error;
    public event Action? Changed;

    /// <summary>订阅先于后台启动快照；按水位重放期间提交，避免极短任务在初始化时丢失。</summary>
    public TaskOverviewService(ITaskLifecycleSource source, Func<IReadOnlyDictionary<Guid, MountDefinition>>? definitions = null)
    {
        _source = source;
        _definitions = definitions ?? (() => new Dictionary<Guid, MountDefinition>());
        source.TaskCommitted += Committed;
        _initialization = Task.Run(Initialize);
    }
    public Task WhenInitialized => _initialization;

    private void Initialize()
    {
        TaskLifecycleCommit? seed = null;
        Exception? failure = null;
        try { seed = _source.ReadUnfinishedTasks(); }
        catch (Exception error) { failure = error; }
        lock (_gate)
        {
            if (_disposed) return;
            // 水位前的已观察终态也要保留；启动快照只补未结束项，不能吞掉快任务。
            _seeding = true;
            foreach (var commit in _initialCommits.Where(commit => seed is null || commit.Revision <= seed.Revision).OrderBy(commit => commit.Revision)) Apply(commit);
            if (seed is not null) Apply(seed with { Entries = seed.Entries.Where(entry => !entry.IsTerminal && !entry.Removed).ToArray() });
            _seeding = false;
            foreach (var commit in _initialCommits.Where(commit => seed is not null && commit.Revision > seed.Revision).OrderBy(commit => commit.Revision)) Apply(commit);
            _initialCommits.Clear();
            _initialized = true;
            if (failure is not null) { _seedFailed = true; _startupError = SafeError(failure); }
        }
        RaiseChanged();
    }

    private void Committed(TaskLifecycleCommit commit)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_initialized) { _initialCommits.Add(commit); return; }
            Apply(commit);
            if (_seedFailed && !_retrying) { _retrying = true; _recovery = Task.Run(RetryInitialSnapshot); }
        }
        RaiseChanged();
    }

    /// <summary>初读失败后由后续业务提交唤醒单次后台补读；补齐前持续提示，绝不把部分统计冒充完整空闲。</summary>
    private void RetryInitialSnapshot()
    {
        TaskLifecycleCommit? seed = null; Exception? failure = null;
        try { seed = _source.ReadUnfinishedTasks(); } catch (Exception error) { failure = error; }
        lock (_gate)
        {
            _retrying = false;
            if (_disposed) return;
            if (seed is not null)
            {
                _seeding = true;
                Apply(seed with { Entries = seed.Entries.Where(entry => !entry.IsTerminal && !entry.Removed).ToArray() });
                _seeding = false; _seedFailed = false; _startupError = null;
            }
            else _startupError = SafeError(failure!);
        }
        RaiseChanged();
    }
    internal Task WhenRecovered { get { lock (_gate) return _recovery; } }

    /// <summary>提交批次内先移除被合并成员，再更新业务行，最后判断闭轮；不把瞬间中间状态当作新一轮。</summary>
    private void Apply(TaskLifecycleCommit commit)
    {
        if (commit.Error is not null) { _error = commit.Error; return; }
        _error = null;
        var updates = commit.Entries.Where(entry => !_seen.TryGetValue(entry.Task.Id, out var seen)
            || commit.Revision > seen.Revision && (entry.Removed || entry.Attempt >= seen.Attempt)).ToArray();
        var closed = _members.Count == _ended;
        var starts = updates.Any(entry => !entry.Removed && (!entry.IsTerminal
            || !_seen.TryGetValue(entry.Task.Id, out var seen) || entry.Attempt > seen.Attempt));
        if (closed && starts && (!_seeding || _round == 0)) StartRound();
        foreach (var entry in updates.OrderByDescending(entry => entry.Removed))
        {
            var id = entry.Task.Id;
            _seen.TryGetValue(id, out var seen);
            _seen[id] = new(commit.Revision, Math.Max(entry.Attempt, seen?.Attempt ?? 0), entry.IsTerminal);
            if (entry.Removed)
            {
                if (_members.Remove(id, out var removed)) Count(removed, -1);
                continue;
            }
            if (_members.TryGetValue(id, out var previous)) Count(previous, -1);
            else if (entry.IsTerminal && seen is { Terminal: true } && entry.Attempt == seen.Attempt) continue;
            var order = entry.Task.SubmissionOrder > 0 ? entry.Task.SubmissionOrder : previous?.Task.SubmissionOrder ?? ++_order;
            var compact = entry with { Task = entry.Task with { SubmissionOrder = order,
                RelativeItemPath = entry.Task.RelativeItemPath ?? entry.Task.ItemPath ?? entry.Task.Results?.LastOrDefault()?.Path,
                Results = Array.Empty<MountItemResult>() } };
            _members[id] = compact;
            Count(compact, 1);
        }
    }
    private void StartRound()
    {
        _members.Clear(); _ended = _succeeded = _failed = _canceled = _skipped = 0; ++_round;
    }
    private void Count(TaskLifecycleEntry entry, int delta)
    {
        if (!entry.IsTerminal) return;
        _ended += delta;
        switch (entry.Task.State)
        {
            case "Completed": _succeeded += delta; break;
            case "Canceled": _canceled += delta; break;
            case "Ignored": case "Superseded": _skipped += delta; break;
            default: _failed += delta; break;
        }
    }

    /// <summary>纯内存快照；只排序当前执行项，最多保留三行，名称来自已加载的内存配置。</summary>
    public TaskOverviewSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                var running = _members.Values.Where(entry => !entry.IsTerminal && OperationDisplayOrder.Group(entry.Task.State) == 0)
                    .Select(entry => entry.Task).ToArray();
                var definitions = _definitions();
                var rows = OperationDisplayOrder.Sort(running).Take(3).Select(task => task with
                { MountName = task.MountId is { } id && definitions.TryGetValue(id, out var definition) ? definition.Name : task.MountName }).ToArray();
                return new(_initialized, _round, _members.Count, _ended, _succeeded, _failed, _canceled, _skipped,
                    running.Length, Array.AsReadOnly(rows), _error ?? _startupError);
            }
        }
    }
    private static string SafeError(Exception error) => $"{error.GetType().Name} (0x{error.HResult:X8})";
    private void RaiseChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Task overview subscriber: {0}", SafeError(error)); }
    }
    /// <summary>业务所有者排空后取消订阅；不取消任何任务，拒绝迟到提交。</summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _source.TaskCommitted -= Committed;
        await _initialization.ConfigureAwait(false);
        Task recovery; lock (_gate) recovery = _recovery;
        await recovery.ConfigureAwait(false);
    }
}
