using System.Globalization;
using HybridMount.Commons.Tasks;
using HybridMount.Models.Mounting;
using HybridMount.Models.Tasks;
using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace HybridMount.Infrastructure.Persistence;

public sealed partial class MountRepository
{
    private long _taskCommitRevision;
    private readonly Dictionary<string, RemoteBatchCounter> _overviewBatches = new(StringComparer.Ordinal);
    internal long TaskLifecycleBatchLoads { get; private set; }
    private readonly HashSet<string> _pendingLifecycleIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingLifecycleBatches = new(StringComparer.Ordinal);
    public event Action<TaskLifecycleCommit>? TaskCommitted;
    private const string OverviewTerminalSql = "'Completed','Failed','PartialFailure','Canceled','Interrupted','Ignored','Superseded'";

    /// <summary>只在应用启动后台读取一次当前业务集合；旧终态不参与，历史条数不影响结果。</summary>
    public TaskLifecycleCommit ReadUnfinishedTasks()
    {
        lock (_gate)
        {
            try
            {
                using var db = Open();
                using var query = db.CreateCommand();
                query.CommandText = $"SELECT id FROM operations WHERE json_extract(json,'$.State') NOT IN ({OverviewTerminalSql}) UNION SELECT id FROM uploads WHERE json_extract(json,'$.State') NOT IN ({OverviewTerminalSql}) UNION SELECT id FROM remote_pending WHERE json_extract(json,'$.State') NOT IN ({OverviewTerminalSql})";
                var ids = new List<string>();
                using (var rows = query.ExecuteReader()) while (rows.Read()) ids.Add(rows.GetString(0));
                query.CommandText = $"SELECT id FROM remote_task_batches b WHERE hidden=0 AND (discovery IN ('Running','CancelRequested') OR EXISTS (SELECT 1 FROM remote_task_batch_members m LEFT JOIN remote_pending p ON p.id=m.pending WHERE m.batch=b.id AND m.category<>'Download' AND COALESCE(json_extract(p.json,'$.State'),m.state) NOT IN ({OverviewTerminalSql})))";
                var batches = new List<string>();
                using (var rows = query.ExecuteReader()) while (rows.Read()) batches.Add(rows.GetString(0));
                var entries = ReadLifecycleEntries(db, ids, batches).Where(entry => !entry.IsTerminal && !entry.Removed).ToArray();
                return new(_taskCommitRevision, Array.AsReadOnly(entries));
            }
            catch { _overviewBatches.Clear(); throw; }
        }
    }

    /// <summary>调用者持有仓库锁，事务已成功提交。点查同次受影响ID及父批次，不扫描历史；订阅者只能入队或更新内存。</summary>
    private void PublishTaskCommit(SqliteConnection db, IEnumerable<string> ids, IEnumerable<string>? batches = null, MountOperationSnapshot? historyHint = null)
    {
        if (TaskCommitted is not { } handlers) return;
        TaskLifecycleCommit commit;
        var revision = ++_taskCommitRevision;
        _pendingLifecycleIds.UnionWith(ids);
        if (batches is not null) _pendingLifecycleBatches.UnionWith(batches);
        try
        {
            commit = new(revision, ReadLifecycleEntries(db, _pendingLifecycleIds, _pendingLifecycleBatches, historyHint));
            _pendingLifecycleIds.Clear(); _pendingLifecycleBatches.Clear();
        }
        catch (Exception error)
        {
            _overviewBatches.Clear();
            commit = new(revision, Array.Empty<TaskLifecycleEntry>(), $"{error.GetType().Name} (0x{error.HResult:X8})");
        }
        foreach (Action<TaskLifecycleCommit> handler in handlers.GetInvocationList())
            try { handler(commit); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Task lifecycle subscriber: {0}", error.GetType().Name); }
    }

    /// <summary>统一业务展示归属：上传优先最新记录，远端成员按真实分类抑制，父批次只计一项。</summary>
    private IReadOnlyList<TaskLifecycleEntry> ReadLifecycleEntries(SqliteConnection db, IEnumerable<string> ids, IEnumerable<string>? batchIds, MountOperationSnapshot? historyHint = null)
    {
        var result = new List<TaskLifecycleEntry>();
        var batches = new HashSet<string>(batchIds ?? [], StringComparer.Ordinal);
        var changedMembers = new List<(string Batch, string Id, string Category, string State, bool Error)>();
        using var query = db.CreateCommand();
        query.CommandText = """
            SELECT wanted.value,o.rowid,CASE WHEN wanted.value=$hint THEN NULL ELSE o.json END,u.json,p.json,m.batch,m.category,
              COALESCE((SELECT MAX(attempt) FROM operation_runs r WHERE r.task=wanted.value),0),m.state,m.reason
            FROM json_each($ids) wanted
            LEFT JOIN operations o ON o.id=wanted.value
            LEFT JOIN uploads u ON u.id=wanted.value
            LEFT JOIN remote_pending p ON p.id=wanted.value
            LEFT JOIN remote_task_batch_members m ON m.pending=wanted.value
            """;
        query.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids.Distinct(StringComparer.Ordinal).ToArray()));
        query.Parameters.AddWithValue("$hint", (object?)historyHint?.Id ?? DBNull.Value);
        using (var rows = query.ExecuteReader())
        {
            while (rows.Read())
            {
                // 当前SaveOperation已拥有完整对象，避免批量结果JSON二次解析；其他ID仍点查已提交记录。
                var history = historyHint?.Id == rows.GetString(0) ? historyHint : rows.IsDBNull(2) ? null : JsonSerializer.Deserialize<MountOperationSnapshot>(rows.GetString(2));
                var upload = rows.IsDBNull(3) ? null : JsonSerializer.Deserialize<LocalUpload>(rows.GetString(3));
                var remote = rows.IsDBNull(4) ? null : JsonSerializer.Deserialize<RemotePendingChange>(rows.GetString(4));
                var task = upload?.ToSnapshot() ?? (remote is not null ? RemoteTaskSnapshot.Create(remote, history?.BytesTransferred ?? 0) : history);
                var merged = !rows.IsDBNull(5) && rows.GetString(6) != "Download";
                if (!rows.IsDBNull(5))
                {
                    batches.Add(rows.GetString(5));
                    changedMembers.Add((rows.GetString(5), rows.GetString(0), rows.GetString(6), remote?.State ?? rows.GetString(8),
                        remote is not null ? remote.Error is not null : !rows.IsDBNull(9)));
                }
                if (task is null) continue;
                task = task with { SubmissionOrder = rows.IsDBNull(1) ? task.SubmissionOrder : rows.GetInt64(1) };
                var attempt = Math.Max(rows.GetInt64(7), Math.Max(upload?.Generation ?? 0, remote?.AttemptGeneration ?? 0));
                result.Add(new(task, attempt, TaskHistoryRules.IsHistory(task.State), merged || task.Operation == "remoteScan"));
            }
        }
        // 活跃批次首读成员一次；之后每条已提交成员只做常量计数更新，避免每个文件重扫整批。
        ReadBatchLifecycle(db, batches, changedMembers, result);
        return Array.AsReadOnly(result.ToArray());
    }

    private void ReadBatchLifecycle(SqliteConnection db, IReadOnlyCollection<string> ids,
        IReadOnlyList<(string Batch, string Id, string Category, string State, bool Error)> changes, List<TaskLifecycleEntry> result)
    {
        if (ids.Count == 0) return;
        using var query = db.CreateCommand();
        query.CommandText = "SELECT id,mount,discovery,started,error,hidden,COALESCE((SELECT MAX(attempt) FROM operation_runs r WHERE r.task=b.id),0) FROM remote_task_batches b WHERE id IN (SELECT value FROM json_each($ids))";
        query.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
        var metadata = new Dictionary<string, (Guid Mount, string Discovery, long Order, string? Error, bool Hidden, long Attempt)>(StringComparer.Ordinal);
        using (var rows = query.ExecuteReader()) while (rows.Read()) metadata[rows.GetString(0)] = (Guid.ParseExact(rows.GetString(1), "N"), rows.GetString(2),
            DateTimeOffset.Parse(rows.GetString(3), CultureInfo.InvariantCulture).UtcTicks, rows.IsDBNull(4) ? null : rows.GetString(4), rows.GetInt64(5) != 0, rows.GetInt64(6));
        var missing = ids.Where(id => metadata.TryGetValue(id, out var meta) && !meta.Hidden && !_overviewBatches.ContainsKey(id)).ToArray();
        if (missing.Length > 0)
        {
            foreach (var id in missing) _overviewBatches[id] = new();
            query.Parameters.Clear(); query.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(missing));
            query.CommandText = "SELECT m.batch,m.pending,m.category,COALESCE(json_extract(p.json,'$.State'),m.state),COALESCE(json_extract(p.json,'$.Error'),m.reason) FROM remote_task_batch_members m LEFT JOIN remote_pending p ON p.id=m.pending WHERE m.batch IN (SELECT value FROM json_each($ids))";
            using var rows = query.ExecuteReader();
            while (rows.Read()) _overviewBatches[rows.GetString(0)].Set(rows.GetString(1), rows.GetString(2), rows.GetString(3), !rows.IsDBNull(4));
            TaskLifecycleBatchLoads += missing.Length;
        }
        foreach (var change in changes)
            if (_overviewBatches.TryGetValue(change.Batch, out var counter)) counter.Set(change.Id, change.Category, change.State, change.Error);
        foreach (var id in ids)
        {
            if (!metadata.TryGetValue(id, out var meta) || meta.Hidden || !_overviewBatches.TryGetValue(id, out var counts))
            {
                result.Add(new(new(id, "remoteBatch", "Superseded", 0, 0, [], null), 0, true, Removed: true));
                _overviewBatches.Remove(id); continue;
            }
            var discovering = meta.Discovery is "Running" or "CancelRequested";
            if (counts.Total == 0)
            {
                result.Add(new(new(id, "remoteBatch", "Superseded", 0, 0, [], null), meta.Attempt, true, Removed: true));
                // 纯下载批次仍缓存分类：在独立下载结束前，不因父项不可见而逐文件重新装载整批。
                if (!discovering && counts.ActiveMembers == 0) _overviewBatches.Remove(id);
                continue;
            }
            var state = discovering ? "Running" : meta.Discovery != "Completed" ? counts.Completed > 0 ? "PartialFailure" : "Interrupted"
                : counts.LegacyTerminal == counts.Total ? counts.Canceled == counts.Total ? "Canceled" : counts.Canceled > 0 ? "PartialFailure" : "Completed"
                : counts.Problems > 0 ? "PartialFailure" : "Queued";
            var terminal = !discovering && counts.Ended == counts.Total;
            if (!terminal && counts.Running > 0) state = "Running";
            result.Add(new(new(id, "remoteBatch", state, counts.Completed, counts.Total, [], meta.Error, meta.Mount, IsDirectory: true)
                { SubmissionOrder = meta.Order }, meta.Attempt, terminal));
            // 终态不需要常驻全部逐文件成员；显式重试时重新加载一次最新集合。
            if (terminal && counts.ActiveMembers == 0) _overviewBatches.Remove(id);
        }
    }

    /// <summary>仅缓存活跃父任务的真实成员，分类及旧新状态各加减一次；不持久化，不控制业务依赖。</summary>
    private sealed class RemoteBatchCounter
    {
        private readonly Dictionary<string, (string Category, string State, bool Error)> _members = new(StringComparer.Ordinal);
        internal int Total, Completed, LegacyTerminal, Canceled, Problems, Ended, Running, ActiveMembers;
        internal void Set(string id, string category, string state, bool error)
        {
            if (_members.TryGetValue(id, out var old)) Count(old, -1);
            var value = (Category: category, State: state, Error: error); _members[id] = value; Count(value, 1);
        }
        private void Count((string Category, string State, bool Error) item, int delta)
        {
            if (!TaskHistoryRules.IsHistory(item.State)) ActiveMembers += delta;
            if (item.Category == "Download") return;
            Total += delta;
            if (item.State == "Completed") Completed += delta;
            if (item.State is "Completed" or "Canceled" or "Superseded") LegacyTerminal += delta;
            if (item.State == "Canceled") Canceled += delta;
            if (item.Error || item.State is "Conflict" or "Failed" or "Waiting" or "Interrupted") Problems += delta;
            if (TaskHistoryRules.IsHistory(item.State)) Ended += delta;
            if (OperationDisplayOrder.Group(item.State) == 0) Running += delta;
        }
    }
}
