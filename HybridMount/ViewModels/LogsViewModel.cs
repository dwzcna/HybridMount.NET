using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Diagnostics;
using System.Collections.ObjectModel;
namespace HybridMount.ViewModels;

/// <summary>持久日志的稳定分页快照；筛选启动新代次，旧完成和已清除事件不能覆盖页面。</summary>
public sealed class LogsViewModel : ObservableObject,IDisposable
{
    private readonly LoggerService _logger;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<DiagnosticEntry> _all=[];
    private bool _disposed,_reading,_clearing,_dialogBusy,_hasMore,_olderLoaded,_hasNew;
    private long _generation,_eventEpoch,_through,_latestSeen;
    private long? _next;
    private Task _readWork=Task.CompletedTask;
    private CancellationTokenSource? _readCancel;
    private string _query="",_severity="All";
    /// <summary>统计静默更新，显式刷新或操作错误重新打开消息。</summary>
    public PageStatusMessage StatusMessage {get;}=new();
    private DateTimeOffset? _fromDate,_toDate;
    public ObservableCollection<DiagnosticEntry> Entries {get;}=[];
    public string Status=>StatusMessage.Text;
    private void PublishStatus(string text,bool reopen,PageMessageSeverity severity=PageMessageSeverity.Information){if(!_disposed&&(reopen?StatusMessage.Show(text,severity):StatusMessage.Update(text,severity)))OnPropertyChanged(nameof(Status));}
    public string Query {get=>_query;set{if(!_disposed&&SetProperty(ref _query,value??""))_=RefreshAsync();}}
    public string Severity {get=>_severity;set{if(!_disposed&&(value is "All" or "Information" or "Warning" or "Error")&&SetProperty(ref _severity,value))_=RefreshAsync();}}
    /// <summary>本地日历的开始日，转换为UTC下界；空表示不限。</summary>
    public DateTimeOffset? FromDate {get=>_fromDate;set{if(!_disposed&&SetProperty(ref _fromDate,value))_=RefreshAsync();}}
    /// <summary>本地日历的结束日，查询包含此整天；导出记录实际UTC排他上界。</summary>
    public DateTimeOffset? ToDate {get=>_toDate;set{if(!_disposed&&SetProperty(ref _toDate,value))_=RefreshAsync();}}
    /// <summary>查询期间显示独立加载状态，不改变消息的关闭状态。</summary>
    public bool IsReading=>_reading;
    public bool CanInteract=>!_disposed&&!_clearing&&!_dialogBusy;
    public bool CanLoadMore=>CanInteract&&!_reading&&_hasMore;
    public LogsViewModel(LoggerService logger,IUiDispatcher dispatcher){_logger=logger;_dispatcher=dispatcher;_logger.EntryWritten+=OnEntry;}
    private static DateTimeOffset? Day(DateTimeOffset? value,bool after)=>value is {} date?new DateTimeOffset(DateTime.SpecifyKind(date.Date.AddDays(after?1:0),DateTimeKind.Local)).ToUniversalTime():null;
    /// <summary>打开保存对话框前捕获筛选及截止ID，随后修改控件不改变本次导出。</summary>
    public DiagnosticQuery CaptureExportQuery()=>new(_severity,_query,Day(_fromDate,false),Day(_toDate,true),ThroughId:_through==0?null:_through);
    public Task<DiagnosticExport> ExportAsync(DiagnosticQuery query,CancellationToken token=default)=>_logger.ExportAsync(query,token);
    /// <summary>重新捕获数据库快照并返回首屏；正在完成的旧查询只丢弃展示结果。</summary>
    public Task RefreshAsync(bool reopenStatus=true)=>StartRead(false,reopenStatus);
    /// <summary>按当前固定快照读取下一页，重复点击在读取期间无效。</summary>
    public Task LoadOlderAsync()=>CanLoadMore?StartRead(true,true):Task.CompletedTask;
    private Task StartRead(bool older,bool reopenStatus)
    {
        if(_disposed||_clearing)return Task.CompletedTask;
        _readCancel?.Cancel();var owner=new CancellationTokenSource();_readCancel=owner;var generation=++_generation;
        _reading=true;NotifyActions();
        return _readWork=ReadCoreAsync(CaptureExportQuery() with{ThroughId=older?_through:null,BeforeId=older?_next:null},older,generation,owner,reopenStatus);
    }
    private async Task ReadCoreAsync(DiagnosticQuery query,bool older,long generation,CancellationTokenSource owner,bool reopenStatus)
    {
        try
        {
            var page=await _logger.ReadAsync(query,owner.Token);
            if(_disposed||generation!=_generation)return;
            if(!older){_all.Clear();_olderLoaded=false;}else _olderLoaded=true;
            foreach(var item in page.Entries)if(!_all.Any(x=>x.Id==item.Id))_all.Add(item);
            _through=page.ThroughId;_next=page.NextBeforeId;_hasMore=page.HasMore;_hasNew=_latestSeen>_through;
            Apply();SetStatus(reopenStatus);
        }
        catch(OperationCanceledException)when(owner.IsCancellationRequested){}
        catch(Exception error){if(generation==_generation)ReportError(error,reopenStatus);}
        finally{owner.Dispose();if(_readCancel==owner)_readCancel=null;if(generation==_generation){_reading=false;NotifyActions();}}
    }
    private void NotifyActions(){if(_disposed)return;OnPropertyChanged(nameof(IsReading));OnPropertyChanged(nameof(CanInteract));OnPropertyChanged(nameof(CanLoadMore));}
    public bool TryBeginDialog(){if(!CanInteract)return false;_dialogBusy=true;NotifyActions();return true;}
    public void EndDialog(){_dialogBusy=false;NotifyActions();}
    /// <summary>代次与事件epoch双屏障；清除前事件不能在清除后重新插入，新的日志仍可在刷新后看到。</summary>
    public async Task ClearAsync()
    {
        if(_disposed||_clearing)return;_clearing=true;++_generation;++_eventEpoch;_readCancel?.Cancel();NotifyActions();
        try{await _logger.ClearAsync();++_eventEpoch;if(_disposed)return;_through=0;_latestSeen=0;_hasNew=false;_hasMore=false;_next=null;_all.Clear();Apply();}
        catch(Exception error){ReportError(error);}
        finally{_clearing=false;NotifyActions();}
        if(!_disposed)await RefreshAsync();
    }
    public void ReportError(Exception error,bool reopenStatus=true){if(_disposed)return;PublishStatus(global::HybridMount.Services.Localization.UiText.Text("Text_83BD080477A5",SafeDiagnostic.ExceptionSummary(error)),reopenStatus,PageMessageSeverity.Error);_logger.QueueException("日志","Error","日志页面操作未完成",error);}
    /// <summary>兼容复制当前显示行；导出调用异步完整筛选接口。</summary>
    public string ExportText()=>string.Join(Environment.NewLine,Entries.Select(x=>x.DisplayText));
    private void SetStatus(bool reopen=false)=>PublishStatus(global::HybridMount.Services.Localization.UiText.Text("Text_F14C813F211F",(_logger.LastError is {} error?global::HybridMount.Services.Localization.UiText.Text("Text_0DD2D51DBFDB",error):""),_all.Count,(_hasMore?global::HybridMount.Services.Localization.UiText.Text("Text_85A95E8DF55D"):""),(_hasNew?global::HybridMount.Services.Localization.UiText.Text("Text_1B47C4A7CF48"):""),(_logger.DroppedCount>0?global::HybridMount.Services.Localization.UiText.Text("Text_2DF4D1DB584B",_logger.DroppedCount):"")),reopen,_logger.LastError is not null?PageMessageSeverity.Error:_logger.DroppedCount>0?PageMessageSeverity.Warning:PageMessageSeverity.Information);
    private void Apply(){if(!_disposed)SnapshotCollections.Reconcile(Entries,_all.OrderByDescending(x=>x.Id).ToArray(),x=>x.Id);}
    private void OnEntry(object? sender,DiagnosticEntry entry)
    {
        var epoch=_eventEpoch;
        _dispatcher.TryEnqueue(()=>
        {
            if(_disposed||_clearing||epoch!=_eventEpoch)return;
            _latestSeen=Math.Max(_latestSeen,entry.Id);
            if(_reading||_olderLoaded){_hasNew=entry.Id>_through;SetStatus();return;}
            if(_all.Any(x=>x.Id==entry.Id)||!CaptureExportQuery().Matches(entry))return;
            _all.Insert(0,entry);_all.Sort((a,b)=>b.Id.CompareTo(a.Id));
            if(_all.Count>100){_all.RemoveRange(100,_all.Count-100);_hasMore=true;}
            _through=Math.Max(_through,entry.Id);_next=_hasMore?_all[^1].Id:null;Apply();SetStatus();NotifyActions();
        });
    }
    public void Dispose(){if(_disposed)return;_disposed=true;StatusMessage.Dispose();++_generation;++_eventEpoch;_readCancel?.Cancel();_logger.EntryWritten-=OnEntry;}
}
