using global::System.Diagnostics;
using global::System.Security.Principal;
using global::System.Threading.Channels;
using HybridMount.Models.Diagnostics;
using Microsoft.Data.Sqlite;
#if !HYBRIDMOUNT_HEADLESS_TEST
using HybridMount.Commons;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
#endif

namespace HybridMount.Services;

/// <summary>当前用户前端日志的通知、限频队列和 SQLite 存储。</summary>
/// <remarks>数据库与后台系统库独立；写入、读取和清除按队列顺序执行，清除不触及生命周期文本日志。</remarks>
public sealed partial class LoggerService : IAsyncDisposable
#if !HYBRIDMOUNT_HEADLESS_TEST
    , ILoggerService
#endif
{
    private abstract record Operation;
    private sealed record Append(PendingDiagnostic Entry) : Operation;
    private sealed record Read(TaskCompletionSource<IReadOnlyList<DiagnosticEntry>> Completion) : Operation;
    private sealed record Clear(TaskCompletionSource Completion) : Operation;
    private sealed record Query(DiagnosticQuery Filter,TaskCompletionSource<DiagnosticPage> Completion,bool Export=false) : Operation;
    private long _dropped,_pendingDropped;
    /// <summary>本会话累计未保存条目；后续成功或清除不会掩盖丢失事实，普通重复合并不计入。</summary>
    public long DroppedCount=>Interlocked.Read(ref _dropped);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Exception,object> _reportedExceptions=new();
    /// <summary>固定上下文与安全异常证据；correlation仅为任务标识，不接受路径、配置或原始错误文本。</summary>
    public void QueueException(string source,string severity,string context,Exception error,string? correlation=null)
    {
        // 服务所有者和页面可能看到同一个异常；只记录一次，不强引用异常或保存敏感Message。
        lock(_recentGate){if(_reportedExceptions.TryGetValue(error,out _))return;_reportedExceptions.Add(error,new object());}
        Queue(source,severity,context+"；"+SafeDiagnostic.ExceptionSummary(error)+(correlation is null?"":"；关联="+SafeDiagnostic.Field(correlation)));
    }
    private const int QueueCapacity = 512;
    // <summary>连续日志最多合并64条；不等待凑批、不越过读取或清除屏障。</summary>
    private const int AppendBatchCapacity = 64;
    private readonly Channel<Operation> _operations = Channel.CreateBounded<Operation>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Dictionary<string, DateTimeOffset> _recent = [];
    private readonly object _recentGate = new();
    private readonly Task _worker;
    private readonly string _path;
    private readonly Func<string, SqliteConnection> _connectionFactory;
    private readonly string _ownerSid;
    private bool _initialized;
    private string? _lastError,_hostError;
#if !HYBRIDMOUNT_HEADLESS_TEST
    private readonly DispatcherQueue? _dispatcher = DispatcherQueue.GetForCurrentThread();
    /// <summary>请求当前窗口显示一条日志通知；持久化与通知相互独立。</summary>
    public event EventHandler<LogNotificationEventArgs>? NotificationRequested;

    /// <summary>将日志入队并在创建本服务的 UI 线程发出界面通知。</summary>
    public void Log(string source, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Queue(source, severity.ToString(), message);
        var args = new LogNotificationEventArgs(source, message, severity);
        if (_dispatcher?.HasThreadAccess == true) NotificationRequested?.Invoke(this, args);
        else _dispatcher?.TryEnqueue(() => NotificationRequested?.Invoke(this, args));
    }
#endif

    /// <summary>默认存入当前用户的 HybridMount.NET 日志目录。</summary>
    public LoggerService() : this(Path.Combine(ApplicationPaths.LogsDirectory, "diagnostics.db")) { }

    /// <summary>允许隔离测试指定数据库路径，避免访问真实用户资料。</summary>
    /// <param name="path">本地 SQLite 数据库的绝对路径。</param>
    internal LoggerService(string path) : this(path, databasePath => new SqliteConnection(
        new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
    { }

    /// <summary>允许隔离测试观察真实连接；工厂在工作任务启动前冻结，不改变公开构造和队列行为。</summary>
    /// <param name="path">独立数据库绝对路径。</param>
    /// <param name="connectionFactory">创建由OpenAsync拥有、成功后交给调用方的真实SQLite连接。</param>
    internal LoggerService(string path, Func<string, SqliteConnection> connectionFactory)
    {
        _path = Path.GetFullPath(path);
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _ownerSid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法取得当前用户 SID");
        _worker = Task.Run(ProcessAsync);
    }

    /// <summary>前端日志成功持久化后发布，页面在自己的 UI 线程更新列表。</summary>
    public event EventHandler<DiagnosticEntry>? EntryWritten;
    /// <summary>最近一次本地写入错误，供诊断页显示。</summary>
    public string? LastError => Volatile.Read(ref _hostError)??Volatile.Read(ref _lastError);

    /// <summary>逐一通知订阅者；一个界面订阅异常不阻止其他页面看到已持久化记录，避免递归写日志。</summary>
    private void PublishEntry(DiagnosticEntry entry)
    {
        if(EntryWritten is not {} handlers)return;
        foreach(EventHandler<DiagnosticEntry> handler in handlers.GetInvocationList())try{handler(this,entry);}catch(Exception error){Debug.WriteLine(error.GetType().Name);}
    }
    /// <summary>同步接收界面日志；重复消息在三十秒内只入库一次。</summary>
    /// <param name="source">日志来源。</param><param name="severity">完整诊断级别。</param><param name="message">日志正文。</param>
    public void Queue(string source, string severity, string message)
    {
        source = string.IsNullOrWhiteSpace(source) ? "HybridMount.NET" : source.Trim();
        if (source.Length > 64) source = source[..64];
        severity = severity is "Information" or "Warning" or "Error" ? severity : "Information";
        message = string.IsNullOrWhiteSpace(message) ? "未提供诊断信息" : message.Trim();
        if (message.Length > 4096) message = message[..4096];
        var now = DateTimeOffset.UtcNow;
        var key = $"{source}\0{severity}\0{message}";
        lock (_recentGate)
        {
            foreach (var expired in _recent.Where(item => now - item.Value >= TimeSpan.FromSeconds(30))
                         .Select(item => item.Key).ToArray()) _recent.Remove(expired);
            if (_recent.ContainsKey(key)) return;
            if (!_operations.Writer.TryWrite(new Append(new PendingDiagnostic(source, severity, message))))
            { RecordDropped(1); Volatile.Write(ref _lastError, "诊断队列已满或关闭，此条日志未保存。"); return; }
            if (_recent.Count >= QueueCapacity)
                _recent.Remove(_recent.MinBy(item => item.Value).Key);
            _recent[key] = now;
        }
    }

    /// <summary>读取最近一百条已持久化日志；排在此前写入任务之后执行。</summary>
    /// <param name="token">等待结果时使用的取消令牌。</param>
    /// <returns>按 ID 倒序排列的日志。</returns>
    public async Task<IReadOnlyList<DiagnosticEntry>> ReadAsync(CancellationToken token = default)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<DiagnosticEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _operations.Writer.WriteAsync(new Read(completion), token);
        return await completion.Task.WaitAsync(token);
    }

    /// <summary>仅清除本地诊断行；队列中先前提交的日志也会在本次清除前处理。</summary>
    /// <param name="token">等待结果时使用的取消令牌。</param>
    public async Task ClearAsync(CancellationToken token = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = false;
        while (await _operations.Writer.WaitToWriteAsync(token))
        {
            lock (_recentGate)
            {
                if (!_operations.Writer.TryWrite(new Clear(completion))) continue;
                _recent.Clear(); queued = true; break;
            }
        }
        if (!queued) throw new ObjectDisposedException(nameof(LoggerService));
        await completion.Task.WaitAsync(token);
    }

    /// <summary>串行处理数据库请求，写入失败仅报告本地故障，避免递归写日志。</summary>
    private async Task ProcessAsync()
    {
        await foreach (var operation in _operations.Reader.ReadAllAsync())
        {
            try
            {
                await ImportHostsAsync(operation is Clear);
                await ReportDroppedAsync();
                switch (operation)
                {
                    case Append append:
                        var batch = new List<PendingDiagnostic>(AppendBatchCapacity) { append.Entry };
                        while (batch.Count < AppendBatchCapacity &&
                               _operations.Reader.TryPeek(out var next) && next is Append &&
                               _operations.Reader.TryRead(out var taken))
                            batch.Add(((Append)taken).Entry);
                        IReadOnlyList<DiagnosticEntry> entries;
                        try{entries=await AppendBatchAsync(batch);}catch{RecordDropped(batch.Count);throw;}
                        Volatile.Write(ref _lastError, null);
                        foreach (var entry in entries)
                        {
                            // <remarks>订阅者故障不影响已提交事务，也不阻止后续条目的通知。</remarks>
                            try { PublishEntry(entry); }
                            catch (Exception error) { Debug.WriteLine($"前端诊断日志通知失败：{error}"); }
                        }
                        break;
                    case Query query:
                        var page=await QueryCoreAsync(query.Filter,query.Export);Volatile.Write(ref _lastError,null);query.Completion.TrySetResult(page);
                        break;
                    case Read read:
                        var rows=await ReadCoreAsync();Volatile.Write(ref _lastError,null);read.Completion.TrySetResult(rows);
                        break;
                    case Clear clear:
                        await ClearCoreAsync();
                        Volatile.Write(ref _lastError, null);
                        clear.Completion.TrySetResult();
                        break;
                }
            }
            catch (Exception error)
            {
                Volatile.Write(ref _lastError, SafeDiagnostic.ExceptionSummary(error));
                Debug.WriteLine($"前端诊断日志存储失败：{error}");
                if(operation is Query query)query.Completion.TrySetException(error);
                if (operation is Read read) read.Completion.TrySetException(error);
                if (operation is Clear clear) clear.Completion.TrySetException(error);
            }
        }
        await ReportDroppedAsync();
    }

    /// <summary>创建当前用户专用表，不读取或迁移系统库中的旧前端日志。</summary>
    /// <remarks>连接在Open及schema完成前由本方法拥有；失败异步释放并保留原异常与堆栈，成功才移交调用方await using。</remarks>
    private async Task<SqliteConnection> OpenAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = _connectionFactory(_path);
        try
        {
            await connection.OpenAsync();
            connection.CreateFunction<string,string,bool>("hm_contains",(text,query)=>text.Contains(query,StringComparison.OrdinalIgnoreCase),isDeterministic:true);
            if (!_initialized)
            {
                await using var schema = connection.CreateCommand();
                schema.CommandText = """
                CREATE TABLE IF NOT EXISTS diagnostic_logs(
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    owner_sid TEXT NOT NULL,
                    recorded_utc TEXT NOT NULL,
                    severity TEXT NOT NULL,
                    source TEXT NOT NULL,
                    message TEXT NOT NULL);
                """;
                await schema.ExecuteNonQueryAsync();
                _initialized = true;
            }
            return connection;
        }
        catch
        {
            try { await connection.DisposeAsync(); }
            catch (Exception cleanupError)
            {
                // <remarks>次生释放故障独立诊断，不替换Open或schema的原失败。</remarks>
                Debug.WriteLine($"前端诊断连接释放失败：{cleanupError}");
            }
            throw;
        }
    }

    /// <summary>一批连续日志共用连接与事务，提交前不发布通知；裁剪仍保留三十天／一万条规则。</summary>
    /// <remarks>批内任一插入或裁剪失败时整批回滚，后续读取／清除继续按原队列顺序处理。</remarks>
    /// <param name="batch">不跨越队列屏障、最多64条的已接收日志。</param>
    /// <returns>已成功提交的诊断记录；失败时不返回部分成功记录。</returns>
    private async Task<IReadOnlyList<DiagnosticEntry>> AppendBatchAsync(IReadOnlyList<PendingDiagnostic> batch)
    {
        await using var db = await OpenAsync();
        await using var transaction = db.BeginTransaction();
        await using var insert = db.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO diagnostic_logs(owner_sid,recorded_utc,severity,source,message) VALUES($o,$t,$v,$s,$m) RETURNING id";
        insert.Parameters.AddWithValue("$o", _ownerSid);
        var time = insert.Parameters.Add("$t", SqliteType.Text);
        var severity = insert.Parameters.Add("$v", SqliteType.Text);
        var source = insert.Parameters.Add("$s", SqliteType.Text);
        var message = insert.Parameters.Add("$m", SqliteType.Text);
        var entries = new List<DiagnosticEntry>(batch.Count);
        foreach (var pending in batch)
        {
            var now = DateTimeOffset.UtcNow;
            time.Value = now.ToString("O");
            severity.Value = pending.Severity;
            source.Value = pending.Source;
            message.Value = pending.Message;
            var id = Convert.ToInt64(await insert.ExecuteScalarAsync());
            entries.Add(new DiagnosticEntry(id, _ownerSid, now, pending.Severity, pending.Source, pending.Message, "App"));
        }
        await using var prune = db.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = "DELETE FROM diagnostic_logs WHERE recorded_utc<$t OR id NOT IN (SELECT id FROM diagnostic_logs ORDER BY id DESC LIMIT 10000)";
        prune.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        await prune.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return entries;
    }

    /// <summary>查询页面现有的一页日志，保持原有一百条视口。</summary>
    private async Task<IReadOnlyList<DiagnosticEntry>> ReadCoreAsync()
    {
        await using var db = await OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT id,owner_sid,recorded_utc,severity,source,message FROM diagnostic_logs ORDER BY id DESC LIMIT 100";
        var entries = new List<DiagnosticEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            entries.Add(new(reader.GetInt64(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), "App"));
        return entries;
    }

    /// <summary>删除本地诊断行，保留数据库文件和同目录其他用户资料。</summary>
    private async Task ClearCoreAsync()
    {
        await using var db = await OpenAsync();
        await using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM diagnostic_logs";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>应用退出时处理完已接收的日志和请求，再释放工作任务。</summary>
    public async ValueTask DisposeAsync()
    {
        _operations.Writer.TryComplete();
        await _worker;
    }
}
