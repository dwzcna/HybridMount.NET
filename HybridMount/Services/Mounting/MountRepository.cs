using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace HybridMount.Services.Mounting;

/// <summary>挂载定义保存在隔离的 HKCU 子键，运行映射和任务写入 SQLite；调用由进程内锁串行化。</summary>
public sealed partial class MountRepository
{
    private readonly string _registry;
    private readonly object _gate = new();
    public string DataDirectory { get; }
    public MountRepository(string dataDirectory, string? registryPath = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory); Directory.CreateDirectory(DataDirectory);
        _registry = registryPath ?? ApplicationPaths.RegistryPath + @"\Mounts\" + (WinRegisterService.IsPackaged() ? "Packaged" : "Unpackaged");
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS entries(mount TEXT NOT NULL,id TEXT NOT NULL,source TEXT NOT NULL COLLATE NOCASE,
                local TEXT NOT NULL COLLATE NOCASE,version TEXT NOT NULL,directory INTEGER NOT NULL,deleted INTEGER NOT NULL,
                protected INTEGER NOT NULL,materialized INTEGER NOT NULL,PRIMARY KEY(mount,id));
            CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS uploads(id TEXT PRIMARY KEY,mount TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS uploads_mount ON uploads(mount);
            CREATE TABLE IF NOT EXISTS content_baselines(mount TEXT NOT NULL,path TEXT NOT NULL,version TEXT NOT NULL,sha256 TEXT NOT NULL,
                PRIMARY KEY(mount,path,version));
            CREATE TABLE IF NOT EXISTS remote_presence(mount TEXT NOT NULL,id TEXT NOT NULL,presence INTEGER NOT NULL,PRIMARY KEY(mount,id));
            CREATE TABLE IF NOT EXISTS remote_temporary(mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(mount,scope,path));
            PRAGMA user_version=4;
            """; cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "mounts.db"), Pooling = false }.ToString()); db.Open(); return db; }
    public IReadOnlyList<MountDefinition> LoadDefinitions()
    {
        lock (_gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registry); if (key is null) return [];
            var result = new List<MountDefinition>();
            foreach (var name in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(name);
                if (child?.GetValue("Definition") is not string json) continue;
                var definition = JsonSerializer.Deserialize<MountDefinition>(json) ?? throw new IOException("挂载配置为空。");
                if (!Enum.IsDefined(definition.RegistrationStage) || !Enum.IsDefined(definition.RemovalMode) || !Enum.IsDefined(definition.RemovalStage) || definition.Id == Guid.Empty || definition.Id.ToString("N") != name || !Path.IsPathFullyQualified(definition.LocalPath)) throw new IOException("挂载配置无效。");
                result.Add(definition);
            }
            return result;
        }
    }
    public void SaveDefinition(MountDefinition definition)
    {
        lock (_gate) { using var key = Registry.CurrentUser.CreateSubKey(_registry + "\\" + definition.Id.ToString("N"), true); key.SetValue("Definition", JsonSerializer.Serialize(definition)); key.SetValue("MountSchemaVersion", 7, RegistryValueKind.DWord); }
    }
    /// <summary>此仓库的全局缓存软上限；旧配置默认 10 GiB，0 为关闭。</summary>
    public long CacheLimitBytes
    {
        get { lock(_gate) { using var key=Registry.CurrentUser.OpenSubKey(_registry);return key?.GetValue("CacheLimitBytes") is long value&&value>=0?value:10L*1024*1024*1024; } }
        set { lock(_gate) { using var key=Registry.CurrentUser.CreateSubKey(_registry,true);key.SetValue("CacheLimitBytes",value,RegistryValueKind.QWord); } }
    }
    public void DeleteDefinition(Guid id) { lock (_gate) Registry.CurrentUser.DeleteSubKeyTree(_registry + "\\" + id.ToString("N"), false); }
    /// <summary>只清除已完成移除的挂载映射；任务历史和模拟源内容保留。</summary>
    public void DeleteEntries(Guid mount)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();
            command.CommandText="DELETE FROM entries WHERE mount=$m;DELETE FROM uploads WHERE mount=$m;DELETE FROM content_baselines WHERE mount=$m;DELETE FROM remote_presence WHERE mount=$m";command.Parameters.AddWithValue("$m",mount.ToString("N"));command.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<MountEntry> Entries(Guid mount)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT e.id,e.source,e.local,e.version,e.directory,e.deleted,e.protected,e.materialized,COALESCE(p.presence,0) FROM entries e LEFT JOIN remote_presence p ON e.mount=p.mount AND e.id=p.id WHERE e.mount=$m"; cmd.Parameters.AddWithValue("$m", mount.ToString("N"));
            using var row = cmd.ExecuteReader(); var result = new List<MountEntry>();
            while (row.Read()) result.Add(new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetInt64(4)!=0,row.GetInt64(5)!=0,row.GetInt64(6)!=0,row.GetInt64(7)!=0,(RemotePresence)row.GetInt64(8)));
            return result;
        }
    }
    public void SaveEntry(Guid mount, MountEntry entry)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO entries VALUES($m,$id,$s,$l,$v,$d,$x,$p,$c) ON CONFLICT(mount,id) DO UPDATE SET local=$l,version=$v,deleted=MAX(deleted,$x),protected=MAX(protected,$p),materialized=MAX(materialized,$c)";
            cmd.Parameters.AddWithValue("$m", mount.ToString("N")); cmd.Parameters.AddWithValue("$id", entry.Id); cmd.Parameters.AddWithValue("$s", entry.SourcePath); cmd.Parameters.AddWithValue("$l", entry.LocalPath); cmd.Parameters.AddWithValue("$v", entry.Version); cmd.Parameters.AddWithValue("$d", entry.IsDirectory); cmd.Parameters.AddWithValue("$x", entry.Deleted); cmd.Parameters.AddWithValue("$p", entry.Protected); cmd.Parameters.AddWithValue("$c", entry.Materialized); cmd.ExecuteNonQuery();
        }
    }
    public void SaveOperation(MountOperationSnapshot snapshot)
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO operations VALUES($id,$j) ON CONFLICT(id) DO UPDATE SET json=$j"; cmd.Parameters.AddWithValue("$id", snapshot.Id); cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(snapshot)); cmd.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<MountOperationSnapshot> LoadOperations()
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM operations ORDER BY rowid DESC LIMIT 100";
            using var row = cmd.ExecuteReader(); var result = new List<MountOperationSnapshot>();
            while (row.Read()) { var item = JsonSerializer.Deserialize<MountOperationSnapshot>(row.GetString(0))!; result.Add(item.State is "Queued" or "Running" ? item with { State = "Interrupted", Error = "上次进程已结束；修改操作未被自动重放。" } : item); }
            return result;
        }
    }
}
