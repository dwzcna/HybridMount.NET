using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;
using HybridMount.Services.Mounting.Persistence;

namespace HybridMount.Services.Mounting;

/// <summary>挂载定义保存在隔离的 HKCU 子键，运行映射和任务写入 SQLite；调用由进程内锁串行化。</summary>
public sealed partial class MountRepository
{
    private readonly IMountDefinitionStore _definitions;
    private readonly MountDatabase _database;
    private readonly object _gate = new();
    public string DataDirectory=>_database.DataDirectory;
    /// <summary>装配定义与运行索引存储；可注入隔离定义存储，所有 DB 事务继续由仓库串行拥有。</summary>
    public MountRepository(string dataDirectory,string? registryPath=null,IMountDefinitionStore? definitions=null)
    {
        _database=new MountDatabase(dataDirectory);_definitions=definitions??new RegistryMountDefinitionStore(registryPath);
    }
    private SqliteConnection Open()=>_database.Open();
    public IReadOnlyList<MountDefinition> LoadDefinitions(){lock(_gate)return _definitions.LoadDefinitions();}
    public void SaveDefinition(MountDefinition definition){lock(_gate)_definitions.SaveDefinition(definition);}
    public long CacheLimitBytes {get{lock(_gate)return _definitions.CacheLimitBytes;}set{if(value<0)throw new ArgumentOutOfRangeException(nameof(value));lock(_gate)_definitions.CacheLimitBytes=value;}}
    public void DeleteDefinition(Guid id){lock(_gate)_definitions.DeleteDefinition(id);}
    /// <summary>只清除已完成移除的挂载映射；任务历史和模拟源内容保留。</summary>
    public void DeleteEntries(Guid mount)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();
            command.CommandText="DELETE FROM entries WHERE mount=$m;DELETE FROM uploads WHERE mount=$m;DELETE FROM content_baselines WHERE mount=$m;DELETE FROM remote_presence WHERE mount=$m;DELETE FROM remote_collections WHERE mount=$m;DELETE FROM remote_shadow WHERE mount=$m;DELETE FROM remote_pending WHERE mount=$m;DELETE FROM remote_scans WHERE mount=$m;DELETE FROM s3_event_hints WHERE mount=$m;DELETE FROM s3_event_watermarks WHERE mount=$m";command.Parameters.AddWithValue("$m",mount.ToString("N"));command.ExecuteNonQuery();
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
    /// <summary>只清除成功完成的展示历史；保留上传基线、恢复日志和索引，原子排除已经重新入队的同 ID 工作。</summary>
    /// <returns>本次从整个历史表清除的记录数，包含当前列表历史上限之外的已完成记录。</returns>
    public int ClearCompletedOperations()
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();
            command.CommandText="""
                DELETE FROM operations WHERE json_extract(json,'$.State')='Completed'
                AND NOT EXISTS (SELECT 1 FROM uploads u WHERE u.id=operations.id AND json_extract(u.json,'$.State')<>'Completed')
                AND NOT EXISTS (SELECT 1 FROM remote_pending r WHERE r.id=operations.id AND json_extract(r.json,'$.State')<>'Completed')
                """;
            return command.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<MountOperationSnapshot> LoadOperations()
    {
        lock (_gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT rowid,json FROM operations WHERE json_extract(json,'$.State') NOT IN ('Completed','Canceled','Ignored','Superseded','Failed','PartialFailure','Interrupted') OR rowid IN (SELECT rowid FROM operations WHERE json_extract(json,'$.State') IN ('Completed','Canceled','Ignored','Superseded','Failed','PartialFailure','Interrupted') ORDER BY rowid DESC LIMIT 100) ORDER BY rowid DESC";
            using var row = cmd.ExecuteReader(); var result = new List<MountOperationSnapshot>();
            while (row.Read()) { var item = JsonSerializer.Deserialize<MountOperationSnapshot>(row.GetString(1))! with{SubmissionOrder=row.GetInt64(0)}; result.Add(item.State is "Queued" or "Running" ? item with { State = "Interrupted", Error = "上次进程已结束；修改操作未被自动重放。" } : item); }
            return result;
        }
    }
}
