using HybridMount.Models.Mounting.Capabilities;
using HybridMount.Models.Protocols.S3;
using HybridMount.Infrastructure.Protocols.Capabilities;
using HybridMount.Infrastructure.Protocols.S3;
using HybridMount.Models.Mounting;
using HybridMount.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Controls.Mounting;

/// <summary>窗口线程一次读取的不可变草稿；后台工作不访问控件，不保存认证材料。</summary>
internal sealed record CapabilityProbeDraft(FileSourceConfiguration Source, SourceCredentials Credentials,
    SqsConfiguration? Sqs = null, SourceCredentials? QueueCredentials = null, string? OneTimePassword = null);

/// <summary>显式检测的UI生命周期；应用拥有真实工作，关闭只取消接纳并解除展示，清理继续由应用收尾。</summary>
internal sealed class ProtocolCapabilityProbeBinding : IDisposable
{
    private readonly ContentDialog _owner;
    private readonly ProtocolCapabilitiesView _view;
    private readonly ProtocolCapabilityDialogBinding _capabilities;
    private readonly MountsViewModel _model;
    private readonly Func<CapabilityProbeDraft> _read;
    private readonly Func<bool> _validate;
    private readonly Action _refresh;
    private readonly Action<string> _error;
    private CancellationTokenSource? _cancel;
    private bool _disposed;
    private long _historyGeneration;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _progressTimer;
    private CapabilityProbeRun? _pendingProgress;
    private int _progressQueued;
    private long _activeRevision;
    internal bool IsRunning => _cancel is not null;

    internal ProtocolCapabilityProbeBinding(ContentDialog owner, ProtocolCapabilitiesView view,
        ProtocolCapabilityDialogBinding capabilities, MountsViewModel model, Func<CapabilityProbeDraft> read,
        Func<bool> validate, Action refresh, Action<string> error)
    {
        _owner = owner; _view = view; _capabilities = capabilities; _model = model;
        _progressTimer = owner.DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(250);
        _progressTimer.IsRepeating = false;
        _progressTimer.Tick += (_, _) =>
        {
            Interlocked.Exchange(ref _progressQueued, 0);
            var value = Interlocked.Exchange(ref _pendingProgress, null);
            if (!_disposed && IsRunning && _capabilities.DraftRevision == _activeRevision && value is not null) _view.ShowProbe(value);
        };
        _read = read; _validate = validate; _refresh = refresh; _error = error;
        view.Actions.SetHandlers(() => StartAsync(null), () => _cancel?.Cancel(), run => StartAsync(run), problem => error(problem.Message));
        view.HistoryRequested += History;
        owner.Closed += Closed;
        owner.Closing += Closing;
    }

    internal void Refresh(bool available)
    {
        if (!_disposed) _view.SetProbeAvailability(available || IsRunning, IsRunning);
    }

    /// <summary>同步捕获并校验草稿后再启动应用任务；迟到结果通过原草稿代次门禁，取消不产生新的成功证据。</summary>
    private async Task StartAsync(Guid? cleanup)
    {
        if (_disposed || IsRunning || !_validate()) return;
        CapabilityProbeDraft draft;
        try { draft = _read(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { _error(error.Message); return; }
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        _capabilities.BeginTest();
        var revision = _capabilities.DraftRevision;
        _activeRevision = revision;
        _view.ShowProbe(null);
        _refresh();
        var progress = new ProbeProgress(value =>
        {
            Volatile.Write(ref _pendingProgress, value);
            if (Interlocked.Exchange(ref _progressQueued, 1) == 0)
                _owner.DispatcherQueue.TryEnqueue(() => { if (!_disposed && IsRunning) _progressTimer.Start(); });
        });
        try
        {
            var result = await _model.DetectCapabilitiesAsync(draft.Source, draft.Credentials, draft.Sqs,
                draft.QueueCredentials, draft.OneTimePassword, progress, cancel.Token, cleanup);
            if (!_disposed && ReferenceEquals(_cancel, cancel) && _capabilities.DraftRevision == revision)
            {
                if (!result.Run.Stage.StartsWith("Canceled", StringComparison.Ordinal)) _capabilities.CompleteTest(result.Snapshot);
                else _capabilities.CancelTest();
                _view.ShowProbe(result.Run);
            }
        }
        catch (OperationCanceledException) { if (!_disposed) _capabilities.CancelTest(); }
        catch (Exception error) { if (!_disposed) _error(error.Message); }
        finally
        {
            _progressTimer.Stop();
            Interlocked.Exchange(ref _pendingProgress, null);
            Interlocked.Exchange(ref _progressQueued, 0);
            _cancel = null;
            // 秘密引用不进入控制器字段；应用任务收尾后草稿局部自然释放。
            if (!_disposed) { _refresh(); if (_capabilities.DraftRevision == revision) await LoadHistoryAsync(draft.Source, draft.Credentials); }
        }
    }

    /// <summary>用户显式展开时只读本地记录；不发网络请求，不自动重跑检测或清理。</summary>
    internal async Task LoadHistoryAsync(FileSourceConfiguration source, SourceCredentials credentials)
    {
        var generation = ++_historyGeneration;
        var revision = _capabilities.DraftRevision;
        try
        {
            var history = await _model.ReadCapabilityProbesAsync(source, credentials, CancellationToken.None);
            if (!_disposed && generation == _historyGeneration && revision == _capabilities.DraftRevision) _view.ShowProbeHistory(history);
        }
        catch (Exception error) { if (!_disposed && generation == _historyGeneration) _error(error.Message); }
    }

    private async void History(object? sender, EventArgs args)
    {
        if (_disposed || IsRunning) return;
        try { var draft = _read(); await LoadHistoryAsync(draft.Source, draft.Credentials); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { /* 空草稿仍可阅读实现清单。 */ }
    }
    private void Closing(ContentDialog sender, ContentDialogClosingEventArgs args) => _cancel?.Cancel();
    private void Closed(ContentDialog sender, ContentDialogClosedEventArgs args) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancel?.Cancel();
        _progressTimer.Stop();
        _view.Actions.Dispose(); _view.HistoryRequested -= History;
        _owner.Closed -= Closed; _owner.Closing -= Closing;
    }
    /// <summary>后台只发布不可变快照，窗口线程每250毫秒合并显示。</summary>
    private sealed class ProbeProgress(Action<CapabilityProbeRun> publish) : IProgress<CapabilityProbeRun>
    {
        public void Report(CapabilityProbeRun value) => publish(value);
    }

}
