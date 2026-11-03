using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Diagnostics;
using System.Collections.ObjectModel;

namespace HybridMount.ViewModels;

/// <summary>维护最近一百条日志的 UI 快照；事件仅通过创建线程的调度器更新集合。</summary>
public sealed class LogsViewModel : ObservableObject, IDisposable
{
    private readonly LoggerService _logger;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<DiagnosticEntry> _all = [];
    private bool _disposed,_reading,_clearing,_dialogBusy;
    private Task _readWork=Task.CompletedTask;
    private string _query = "", _severity = "All", _status = "";
    /// <summary>当前筛选后的只读展示行。</summary>
    public ObservableCollection<DiagnosticEntry> Entries { get; } = [];
    /// <summary>查询失败和日志存储故障提示。</summary>
    public string Status { get => _status; private set {if(!_disposed)SetProperty(ref _status,value);} }
    /// <summary>按来源与内容进行不区分大小写的查询。</summary>
    public string Query { get => _query; set { if (!_disposed&&SetProperty(ref _query, value)) Filter(); } }
    /// <summary>All、Information、Warning、Error 中的一个筛选值。</summary>
    public string Severity { get => _severity; set { if (!_disposed&&(value is "All" or "Information" or "Warning" or "Error")&&SetProperty(ref _severity, value)) Filter(); } }
    /// <summary>订阅持久化结果；退出时必须释放。</summary>
    public LogsViewModel(LoggerService logger,IUiDispatcher dispatcher) { _dispatcher=dispatcher;_logger = logger; _logger.EntryWritten += OnEntry; }
    /// <summary>在所有已提交写入之后刷新数据库快照。</summary>
    public Task RefreshAsync()
    {
        if(_disposed)return Task.CompletedTask;if(_reading)return _readWork;
        _reading=true;return _readWork=ReadCoreAsync();
    }
    private async Task ReadCoreAsync()
    {
        try
        {
            var entries=await _logger.ReadAsync();if(_disposed)return;
            _all.Clear();_all.AddRange(entries);Filter();Status=_logger.LastError??$"最近 {_all.Count} 条日志（最多 100 条）。";
        }
        catch(Exception error){ReportError(error);}
        finally{_reading=false;}
    }
    public bool CanInteract=>!_disposed&&!_clearing&&!_dialogBusy;
    public bool TryBeginDialog(){if(!CanInteract)return false;_dialogBusy=true;OnPropertyChanged(nameof(CanInteract));return true;}
    public void EndDialog(){_dialogBusy=false;if(!_disposed)OnPropertyChanged(nameof(CanInteract));}
    /// <summary>确认后的业务清除防重入，等待旧读取结束，避免旧结果覆盖清空快照。</summary>
    public async Task ClearAsync()
    {
        if(_disposed||_clearing)return;_clearing=true;OnPropertyChanged(nameof(CanInteract));
        try{await _readWork;if(_disposed)return;await _logger.ClearAsync();if(!_disposed)await RefreshAsync();}
        catch(Exception error){ReportError(error);}
        finally{_clearing=false;if(!_disposed)OnPropertyChanged(nameof(CanInteract));}
    }
    public void ReportError(Exception error){if(_disposed)return;Status="日志操作失败："+error.Message;_logger.Queue("日志","Error",error.GetType().Name);}
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
        var filtered=_all.Where(entry=>(_severity=="All"||entry.Severity==_severity)&&(string.IsNullOrWhiteSpace(_query)||entry.DisplayText.Contains(_query,StringComparison.OrdinalIgnoreCase))).ToArray();
        SnapshotCollections.Reconcile(Entries,filtered,entry=>entry.Id);
    }
    /// <summary>解除跨线程事件；已经排队的回调会检查释放标志。</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _logger.EntryWritten -= OnEntry; }
}
