using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Diagnostics;
using Microsoft.UI.Dispatching;
using System.Collections.ObjectModel;

namespace HybridMount.ViewModels;

/// <summary>维护最近一百条日志的 UI 快照；事件仅通过创建线程的调度器更新集合。</summary>
public sealed class LogsViewModel : ObservableObject, IDisposable
{
    private readonly LoggerService _logger;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly List<DiagnosticEntry> _all = [];
    private bool _disposed;
    private string _query = "", _severity = "All", _status = "";
    /// <summary>当前筛选后的只读展示行。</summary>
    public ObservableCollection<DiagnosticEntry> Entries { get; } = [];
    /// <summary>查询失败和日志存储故障提示。</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    /// <summary>按来源与内容进行不区分大小写的查询。</summary>
    public string Query { get => _query; set { if (SetProperty(ref _query, value)) Filter(); } }
    /// <summary>All、Information、Warning、Error 中的一个筛选值。</summary>
    public string Severity { get => _severity; set { if (SetProperty(ref _severity, value)) Filter(); } }
    /// <summary>订阅持久化结果；退出时必须释放。</summary>
    public LogsViewModel(LoggerService logger) { _logger = logger; _logger.EntryWritten += OnEntry; }
    /// <summary>在所有已提交写入之后刷新数据库快照。</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var entries = await _logger.ReadAsync();
            if (_disposed) return;
            _all.Clear(); _all.AddRange(entries); Filter();
            Status = _logger.LastError ?? $"最近 {_all.Count} 条日志（最多 100 条）。";
        }
        catch (Exception error) { if (!_disposed) Status = $"日志读取失败：{error.Message}"; }
    }
    /// <summary>清空当前产品数据库中的日志，不触及任何用户文件。</summary>
    public async Task ClearAsync() { await _logger.ClearAsync(); await RefreshAsync(); }
    /// <summary>生成当前筛选结果，供复制或导出；不读取凭据或注册表。</summary>
    public string ExportText() => string.Join(Environment.NewLine, Entries.Select(entry => entry.DisplayText));
    private void OnEntry(object? sender, DiagnosticEntry entry) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed || _all.Any(item => item.Id == entry.Id)) return;
        _all.Insert(0, entry);
        if (_all.Count > 100) _all.RemoveRange(100, _all.Count - 100);
        Filter();
    });
    private void Filter()
    {
        if (_disposed) return;
        Entries.Clear();
        foreach (var entry in _all.Where(entry => (_severity == "All" || entry.Severity == _severity)
            && (string.IsNullOrWhiteSpace(_query) || entry.DisplayText.Contains(_query, StringComparison.OrdinalIgnoreCase)))) Entries.Add(entry);
    }
    /// <summary>解除跨线程事件；已经排队的回调会检查释放标志。</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _logger.EntryWritten -= OnEntry; }
}
