using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    private IReadOnlyList<MountEntry> SelectEntries(Guid mount,string predicate,IReadOnlyList<string?> values)
    {
        lock(_gate)
        {
            using var db=Open();using var query=db.CreateCommand();
            query.CommandText="SELECT e.id,e.source,e.local,e.version,e.directory,e.deleted,e.protected,e.materialized,COALESCE(p.presence,0) FROM entries e LEFT JOIN remote_presence p ON e.mount=p.mount AND e.id=p.id WHERE e.mount=$m AND "+predicate;
            query.Parameters.AddWithValue("$m",mount.ToString("N"));for(var i=0;i<values.Count;i++)query.Parameters.AddWithValue("$p"+i,(object?)values[i]??DBNull.Value);
            using var rows=query.ExecuteReader();var entries=new List<MountEntry>();while(rows.Read())entries.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetInt64(4)!=0,rows.GetInt64(5)!=0,rows.GetInt64(6)!=0,rows.GetInt64(7)!=0,(RemotePresence)rows.GetInt64(8)));return entries;
        }
    }
    /// <summary>执行前按路径索引重验最多512项，不重新加载整根索引。</summary>
    internal IReadOnlyList<MountEntry> EntriesBySources(Guid mount,IReadOnlyList<string> paths)
    {if(paths.Count==0)return [];if(paths.Count>512)throw new ArgumentOutOfRangeException(nameof(paths));return SelectEntries(mount,"e.source IN ("+string.Join(',',Enumerable.Range(0,paths.Count).Select(i=>"$p"+i))+")",paths);}
    internal MountEntry? EntryBySource(Guid mount,string path)=>SelectEntries(mount,"e.source=$p0",[path]).SingleOrDefault();
    /// <summary>按主键读取单项；没有映射身份时不打开数据库。</summary>
    internal MountEntry? EntryById(Guid mount, string? id) => id is null ? null : SelectEntries(mount, "e.id=$p0", [id]).SingleOrDefault();
    internal IReadOnlyList<MountEntry> EntriesInSourceSubtree(Guid mount,string path)=>SelectEntries(mount,"e.source >= $p0 AND e.source < $p2 AND (e.source=$p0 OR e.source >= $p1)",[path,path+"/",path+"0"]);
    /// <summary>按本地路径索引读取实际子树；原生改名删除通知不加载整个挂载。</summary>
    internal IReadOnlyList<MountEntry> EntriesInLocalSubtree(Guid mount,string path)=>SelectEntries(mount,"($p0='' OR (e.local COLLATE HM_PATH >= $p0 AND e.local COLLATE HM_PATH < $p2 AND (e.local COLLATE HM_PATH=$p0 OR e.local COLLATE HM_PATH >= $p1)))",[path,path+"/",path+"0"]);
    /// <summary>准备映射或发布结果一批提交；事务不执行CFAPI或远端I/O，旧表格式保持。</summary>
    internal void CommitDiscoveryBatch(Guid mount,IReadOnlyList<MountEntry> entries,IReadOnlyList<RemotePendingChange> pending,IReadOnlyList<MountOperationSnapshot> operations)
    {
        if(entries.Count>256||pending.Count>256||operations.Count>256||pending.Any(item=>item.MountId!=mount))throw new ArgumentException("发现批次身份或数量无效。");
        lock(_gate)
        {
            using var db=Open();using var transaction=db.BeginTransaction();
            foreach(var entry in entries)
            {
                SaveEntryCore(db,transaction,mount,entry);using var presence=db.CreateCommand();presence.Transaction=transaction;presence.CommandText="INSERT INTO remote_presence VALUES($m,$id,$p) ON CONFLICT(mount,id) DO UPDATE SET presence=$p";presence.Parameters.AddWithValue("$m",mount.ToString("N"));presence.Parameters.AddWithValue("$id",entry.Id);presence.Parameters.AddWithValue("$p",(int)RemotePresence.Present);presence.ExecuteNonQuery();
            }
            foreach(var item in pending)SavePendingCore(db,transaction,item);
            foreach(var item in operations){using var operation=db.CreateCommand();operation.Transaction=transaction;operation.CommandText="INSERT INTO operations VALUES($id,$j) ON CONFLICT(id) DO UPDATE SET json=$j";operation.Parameters.AddWithValue("$id",item.Id);operation.Parameters.AddWithValue("$j",JsonSerializer.Serialize(item));operation.ExecuteNonQuery();}
            transaction.Commit();
        }
    }
    /// <summary>冷占位没有任何本地范围的证明由会话提供；锁内重新核对旧版本及待应用屏障后才绑定已验证版本。</summary>
    internal bool BindColdObservedVersion(Guid mount,MountEntry expected,string version,bool allowColdBinding)
    {
        lock(_gate)
        {
            if(HasPendingRemote(mount,expected.SourcePath))return false;
            using var db=Open();using var update=db.CreateCommand();
            update.CommandText=allowColdBinding?"UPDATE entries SET version=$next WHERE mount=$m AND id=$id AND source=$p AND local=$l AND version=$old AND materialized=1 AND protected=0 AND deleted=0 AND NOT EXISTS(SELECT 1 FROM remote_presence p WHERE p.mount=entries.mount AND p.id=entries.id AND p.presence=2)":"SELECT 0";
            update.Parameters.AddWithValue("$m",mount.ToString("N"));update.Parameters.AddWithValue("$id",expected.Id);update.Parameters.AddWithValue("$p",expected.SourcePath);update.Parameters.AddWithValue("$l",expected.LocalPath);update.Parameters.AddWithValue("$old",expected.Version);update.Parameters.AddWithValue("$next",version);
            if(allowColdBinding&&update.ExecuteNonQuery()==1)return true;
            // 另一位首读可能已绑定同一已验证版本并开始填充范围，迟到等待者只领取该同版本结果。
            update.CommandText="SELECT 1 FROM entries e WHERE mount=$m AND id=$id AND source=$p AND local=$l AND version=$next AND materialized=1 AND protected=0 AND deleted=0 AND NOT EXISTS(SELECT 1 FROM remote_presence p WHERE p.mount=e.mount AND p.id=e.id AND p.presence=2)";return update.ExecuteScalar() is not null;
        }
    }
    /// <summary>按主键一次重新读取不超过256个事件，保持取消和阶段修改可见。</summary>
    internal IReadOnlyList<RemotePendingChange> RemotePendingByIds(IReadOnlyList<string> ids)
    {
        if(ids.Count==0)return [];if(ids.Count>256)throw new ArgumentOutOfRangeException(nameof(ids));
        lock(_gate){using var db=Open();using var query=db.CreateCommand();query.CommandText="SELECT json FROM remote_pending WHERE id IN ("+string.Join(',',Enumerable.Range(0,ids.Count).Select(i=>"$p"+i))+")";for(var i=0;i<ids.Count;i++)query.Parameters.AddWithValue("$p"+i,ids[i]);using var rows=query.ExecuteReader();var result=new List<RemotePendingChange>();while(rows.Read())result.Add(JsonSerializer.Deserialize<RemotePendingChange>(rows.GetString(0))??throw new IOException("远端事件损坏。"));return result;}
    }
    /// <summary>主键定位单个事件；用于执行前重新核验取消及迟到阶段。</summary>
    internal RemotePendingChange? FindRemotePending(string id)
    {lock(_gate){using var db=Open();using var query=db.CreateCommand();query.CommandText="SELECT json FROM remote_pending WHERE id=$id";query.Parameters.AddWithValue("$id",id);return query.ExecuteScalar() is string json?JsonSerializer.Deserialize<RemotePendingChange>(json):null;}}
    /// <summary>只按任务主键读取历史；交互操作不重建整个任务页。</summary>
    internal MountOperationSnapshot? FindOperation(string id)
    {
        string? json;
        lock(_gate)
        {
            using var db=Open();using var query=db.CreateCommand();
            query.CommandText="SELECT json FROM operations WHERE id=$id";query.Parameters.AddWithValue("$id",id);
            json=query.ExecuteScalar() as string;
        }
        return json is null?null:JsonSerializer.Deserialize<MountOperationSnapshot>(json);
    }

    /// <summary>每文件恢复按主键点查同挂载、条目和操作；保留旧版本／身份及尝试证据，由原执行器核验失败，不能以新任务覆盖未决恢复。</summary>
    internal MountOperationSnapshot? FindRecoverableFileOperation(string id,Guid mount,MountEntry entry,string operation)
    {
        var row=FindOperation(id);
        return row is not null&&row.MountId==mount&&row.OperationEntryId==entry.Id&&row.Operation==operation&&
            row.State is "Running" or "Deferred" or "Interrupted"
            ?row:null;
    }

    /// <summary>按祖先或子树索引核验持久依赖；执行和提交共用语义，跨作用域未完成提交仍构成屏障。</summary>
    internal bool RemoteDependenciesReady(RemotePendingChange item)
    {
        lock(_gate){using var db=Open();return RemoteDependenciesReadyCore(db,null,item);}
    }
    private static bool RemoteDependenciesReadyCore(SqliteConnection db,SqliteTransaction? transaction,RemotePendingChange item)
    {
        using var query=db.CreateCommand();query.Transaction=transaction;
        var ancestors=new List<string>();for(var path=RelativePaths.Parent(item.Path);;path=RelativePaths.Parent(path)){ancestors.Add(path);if(path.Length==0)break;}
        query.CommandText="SELECT 1 FROM remote_pending WHERE mount=$m AND id<>$id AND "+ActiveRemoteBarrierSql+" AND "+
            (item.Entry is null?(item.Path.Length==0?"length(path)>0":"path COLLATE HM_PATH >= $start AND path COLLATE HM_PATH < $end"):"path COLLATE HM_PATH IN (SELECT value FROM json_each($parents)) AND json_extract(json,'$.Entry.IsDirectory')=1")+" LIMIT 1";
        query.Parameters.AddWithValue("$m",item.MountId.ToString("N"));query.Parameters.AddWithValue("$id",item.Id);
        query.Parameters.AddWithValue("$start",item.Path+"/");query.Parameters.AddWithValue("$end",item.Path+"0");query.Parameters.AddWithValue("$parents",JsonSerializer.Serialize(ancestors));
        return query.ExecuteScalar() is null;
    }

    /// <summary>仅批量保存索引变化，不改变远端存在性或执行文件操作。</summary>
    internal void SaveEntries(IReadOnlyList<MountEntry> entries,Guid mount)
    {
        if(entries.Count>256)throw new ArgumentOutOfRangeException(nameof(entries));
        lock(_gate){using var db=Open();using var tx=db.BeginTransaction();foreach(var entry in entries)SaveEntryCore(db,tx,mount,entry);tx.Commit();}
    }

}
