using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    private IReadOnlyList<T> RemoteRows<T>(string table,Guid mount,string scope)
    {
        lock(_gate)
        {
            using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT json FROM {table} WHERE mount=$m AND scope=$s ORDER BY rowid";
            cmd.Parameters.AddWithValue("$m",mount.ToString("N"));cmd.Parameters.AddWithValue("$s",scope);using var reader=cmd.ExecuteReader();var result=new List<T>();
            while(reader.Read())result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))??throw new IOException("远端同步日志损坏，已保留现场。"));return result;
        }
    }
    public IReadOnlyList<RemoteCollectionCheckpoint> RemoteCollections(Guid mount,string scope)=>RemoteRows<RemoteCollectionCheckpoint>("remote_collections",mount,scope);
    public IReadOnlyList<RemoteShadowEntry> RemoteShadow(Guid mount,string scope)=>RemoteRows<RemoteShadowEntry>("remote_shadow",mount,scope);
    public IReadOnlyList<RemotePendingChange> RemotePending(Guid mount,string scope)=>RemoteRows<RemotePendingChange>("remote_pending",mount,scope);
    public IReadOnlyList<RemotePendingChange> AllRemotePending(Guid mount)
    {
        lock(_gate)
        {
            using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT json FROM remote_pending WHERE mount=$m ORDER BY rowid";cmd.Parameters.AddWithValue("$m",mount.ToString("N"));
            using var row=cmd.ExecuteReader();var result=new List<RemotePendingChange>();while(row.Read())result.Add(JsonSerializer.Deserialize<RemotePendingChange>(row.GetString(0))!);return result;
        }
    }
    private static void RemoteCommand(SqliteConnection db,SqliteTransaction transaction,string sql,Guid mount,string scope,string path,string json,string? id=null)
    {
        using var cmd=db.CreateCommand();cmd.Transaction=transaction;cmd.CommandText=sql;
        cmd.Parameters.AddWithValue("$m",mount.ToString("N"));cmd.Parameters.AddWithValue("$s",scope);cmd.Parameters.AddWithValue("$p",path);cmd.Parameters.AddWithValue("$start",path+"/");cmd.Parameters.AddWithValue("$end",path+"0");cmd.Parameters.AddWithValue("$j",json);cmd.Parameters.AddWithValue("$id",(object?)id??DBNull.Value);cmd.ExecuteNonQuery();
    }
    /// <summary>事件、远端观测和检查点在同一事务持久化；本地占用或中断不导致游标前进后丢事件。</summary>
    public void CommitRemoteBatch(Guid mount,string scope,RemoteCollectionCheckpoint checkpoint,IReadOnlyList<RemoteShadowEntry> changed,IReadOnlyList<string> deleted,IReadOnlySet<string>? silent=null,string? scan=null,string? hint=null)
        =>CommitDiscoveredBatch(mount,scope,checkpoint,changed,deleted,silent,scan,hint);
    /// <summary>返回本次事务实际创建的事件，供有界发布使用；调用方不能重放未提交的发现。</summary>
    internal IReadOnlyList<RemotePendingChange> CommitDiscoveredBatch(Guid mount,string scope,RemoteCollectionCheckpoint checkpoint,IReadOnlyList<RemoteShadowEntry> changed,IReadOnlyList<string> deleted,IReadOnlySet<string>? silent=null,string? scan=null,string? hint=null,IReadOnlyDictionary<string,RemoteShadowEntry>? repeated=null)
    {
        lock(_gate)
        {
            using var db=Open();using var transaction=db.BeginTransaction();
            var created=new List<RemotePendingChange>();
            // 当前事务内按批次捕获证据；不能只凭扫描开始时的内存影子吞掉本地变化或恢复任务。
            if(repeated is {Count:>0})
            {
                var paths=JsonSerializer.Serialize(repeated.Keys);
                Dictionary<string,RemoteShadowEntry> current;
                var active=new Dictionary<string,List<RemotePendingChange>>(StringComparer.Ordinal);
                using(var query=db.CreateCommand())
                {
                    query.Transaction=transaction;query.CommandText="SELECT json FROM remote_shadow WHERE mount=$m AND scope=$s AND path IN (SELECT value FROM json_each($paths))";
                    query.Parameters.AddWithValue("$m",mount.ToString("N"));query.Parameters.AddWithValue("$s",scope);query.Parameters.AddWithValue("$paths",paths);
                    using var rows=query.ExecuteReader();current=new(StringComparer.Ordinal);
                    while(rows.Read()){var item=JsonSerializer.Deserialize<RemoteShadowEntry>(rows.GetString(0))!;current[item.Entry.Path]=item;}
                }
                using(var query=db.CreateCommand())
                {
                    query.Transaction=transaction;query.CommandText="SELECT json FROM remote_pending WHERE mount=$m AND scope=$s AND path IN (SELECT value FROM json_each($paths)) AND json_extract(json,'$.State') NOT IN ('Completed','Superseded')";
                    query.Parameters.AddWithValue("$m",mount.ToString("N"));query.Parameters.AddWithValue("$s",scope);query.Parameters.AddWithValue("$paths",paths);
                    using var rows=query.ExecuteReader();while(rows.Read()){var item=JsonSerializer.Deserialize<RemotePendingChange>(rows.GetString(0))!;if(!active.TryGetValue(item.Path,out var list))active[item.Path]=list=[];list.Add(item);}
                }
                changed=changed.Where(item=>!repeated.TryGetValue(item.Entry.Path,out var expected)||
                    !current.TryGetValue(item.Entry.Path,out var actual)||actual!=expected||
                    !(active.GetValueOrDefault(item.Entry.Path)?.Any(p=>p.Entry?.Version==item.Entry.Version&&p.Entry.IsDirectory==item.Entry.IsDirectory)==true||ConfirmedSilent(db,transaction,mount,item.Entry))).ToArray();
            }
            // 路径索引定位待退役事件，不逐个文件重新装载整根JSON；本事务内再核验静默回显候选。
            var deletedPaths=deleted.ToHashSet(StringComparer.Ordinal);
            foreach(var path in changed.Select(x=>x.Entry.Path).Concat(deleted))
            {
                using var retire=db.CreateCommand();retire.Transaction=transaction;
                retire.CommandText="UPDATE remote_pending SET json=json_set(json,'$.State','Superseded','$.Error',NULL) WHERE mount=$m AND scope=$s AND "+
                    (deletedPaths.Contains(path)?"path >= $p AND path < $end AND (path=$p OR path >= $start)":"path=$p")+" AND json_extract(json,'$.State') NOT IN ('Completed','Superseded','Committing','Hydrating')";
                retire.Parameters.AddWithValue("$m",mount.ToString("N"));retire.Parameters.AddWithValue("$s",scope);retire.Parameters.AddWithValue("$p",path);retire.Parameters.AddWithValue("$start",path+"/");retire.Parameters.AddWithValue("$end",path+"0");retire.ExecuteNonQuery();
            }
            foreach(var shadow in changed)
            {
                if(!shadow.Entry.IsDirectory)
                    RemoteCommand(db,transaction,"DELETE FROM remote_shadow WHERE mount=$m AND scope=$s AND path >= $start AND path < $end;DELETE FROM remote_collections WHERE mount=$m AND scope=$s AND path >= $p AND path < $end AND (path=$p OR path >= $start)",mount,scope,shadow.Entry.Path,"");
                RemoteCommand(db,transaction,"INSERT INTO remote_shadow VALUES($m,$s,$p,$j) ON CONFLICT(mount,scope,path) DO UPDATE SET json=$j",mount,scope,shadow.Entry.Path,JsonSerializer.Serialize(shadow));
                if(silent?.Contains(shadow.Entry.Path)!=true||!ConfirmedSilent(db,transaction,mount,shadow.Entry)){var pending=new RemotePendingChange(Guid.NewGuid().ToString("N"),mount,scope,shadow.Entry.Path,shadow.Entry);SavePendingCore(db,transaction,pending);created.Add(pending);}
            }
            foreach(var path in deleted)
            {
                RemoteCommand(db,transaction,"DELETE FROM remote_shadow WHERE mount=$m AND scope=$s AND path >= $p AND path < $end AND (path=$p OR path >= $start);DELETE FROM remote_collections WHERE mount=$m AND scope=$s AND path >= $p AND path < $end AND (path=$p OR path >= $start)",mount,scope,path,"");
                var pending=new RemotePendingChange(Guid.NewGuid().ToString("N"),mount,scope,path,null);SavePendingCore(db,transaction,pending);created.Add(pending);
            }
            RemoteCommand(db,transaction,"INSERT INTO remote_collections VALUES($m,$s,$p,$j) ON CONFLICT(mount,scope,path) DO UPDATE SET json=$j",mount,scope,checkpoint.Path,JsonSerializer.Serialize(checkpoint));
            if(hint is not null)foreach(var module in _protocolStorage)module.CompleteHint(this,db,transaction,mount,scope,hint);
            if(scan is not null)
            {using var done=db.CreateCommand();done.Transaction=transaction;done.CommandText="UPDATE remote_scans SET state='Completed' WHERE id=$id AND mount=$m AND scope=$s";done.Parameters.AddWithValue("$id",scan);done.Parameters.AddWithValue("$m",mount.ToString("N"));done.Parameters.AddWithValue("$s",scope);if(done.ExecuteNonQuery()!=1)throw new IOException("远端扫描身份丢失，未提交快照。");}
            transaction.Commit();return created;
        }
    }
    /// <summary>在同一提交事务内重验内存候选，避免扫描期间的本地上传／改名使旧索引错误吞掉远端事件。</summary>
    private static bool ConfirmedSilent(SqliteConnection db,SqliteTransaction transaction,Guid mount,SourceEntry entry)
    {
        using var query=db.CreateCommand();query.Transaction=transaction;
        query.CommandText="SELECT 1 FROM entries e WHERE mount=$m AND source=$p AND version=$v AND directory=$d AND materialized=1 AND NOT EXISTS(SELECT 1 FROM remote_presence p WHERE p.mount=e.mount AND p.id=e.id AND p.presence=$gone) LIMIT 1";
        query.Parameters.AddWithValue("$m",mount.ToString("N"));query.Parameters.AddWithValue("$p",entry.Path);query.Parameters.AddWithValue("$v",entry.Version);query.Parameters.AddWithValue("$d",entry.IsDirectory);query.Parameters.AddWithValue("$gone",(int)RemotePresence.Deleted);return query.ExecuteScalar() is not null;
    }
    private static void SavePendingCore(SqliteConnection db,SqliteTransaction transaction,RemotePendingChange pending)
        =>RemoteCommand(db,transaction,"INSERT INTO remote_pending VALUES($id,$m,$s,$p,$j) ON CONFLICT(id) DO UPDATE SET json=$j",pending.MountId,pending.Scope,pending.Path,JsonSerializer.Serialize(pending),pending.Id);
    /// <summary>提交阶段先持久化，重启仍可使用原快照和本地身份核对，不依赖任务历史重放。</summary>
    public void SaveRemotePending(RemotePendingChange pending)
    {
        lock(_gate){using var db=Open();using var tx=db.BeginTransaction();SavePendingCore(db,tx,pending);tx.Commit();}
    }
    /// <summary>扫描与应用并行时，在同一事务内检查终态再保存；迟到准备不得复活被取代的事件。</summary>
    internal RemotePendingChange SaveRemotePendingCurrent(RemotePendingChange pending,bool retry=false,Func<RemotePendingChange,MountOperationSnapshot>? snapshot=null,MountEntry? present=null)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();var current=ReadPendingCore(db,tx,pending.Id)??throw new IOException("远端事件已丢失，未发布迟到结果。");
            if(current is not null)
            {
                ValidatePendingIdentity(current,pending);
                if(current.State is "Superseded" or "Completed"&&current.State!=pending.State)return current;
                if(!retry&&current.State=="Canceled"&&pending.State is not("Completed" or "Superseded" or "Canceled"))return current;
                // 仅刚进入不可逆阶段时重建依赖；已经提交的阶段仍须安全收尾。
                if(pending.State=="Committing"&&current.State is not("Committing" or "Hydrating"))
                {
                    if(!RemoteDependenciesReadyCore(db,tx,pending))throw new SourceConflictException("必要父项或子项尚未完成，未开始本地提交。");
                }
            }
            if(present is not null)
            {
                using var presence=db.CreateCommand();presence.Transaction=tx;
                presence.CommandText="INSERT INTO remote_presence SELECT mount,id,1 FROM entries WHERE mount=$m AND id=$id AND source=$p AND local=$l AND version=$v AND materialized=1 ON CONFLICT(mount,id) DO UPDATE SET presence=1";
                presence.Parameters.AddWithValue("$m",pending.MountId.ToString("N"));presence.Parameters.AddWithValue("$id",present.Id);presence.Parameters.AddWithValue("$p",present.SourcePath);presence.Parameters.AddWithValue("$l",present.LocalPath);presence.Parameters.AddWithValue("$v",present.Version);
                if(presence.ExecuteNonQuery()!=1)throw new SourceConflictException("索引已变化，未完成旧版本核对。");
            }
            SavePendingCore(db,tx,pending);
            if(snapshot is not null)
            {
                using var history=db.CreateCommand();history.Transaction=tx;history.CommandText="INSERT INTO operations VALUES($id,$j) ON CONFLICT(id) DO UPDATE SET json=$j";
                history.Parameters.AddWithValue("$id",pending.Id);history.Parameters.AddWithValue("$j",JsonSerializer.Serialize(snapshot(pending)));history.ExecuteNonQuery();
            }
            tx.Commit();return pending;
        }
    }
    private static RemotePendingChange? ReadPendingCore(SqliteConnection db,SqliteTransaction transaction,string id)
    {
        using var command=db.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT json FROM remote_pending WHERE id=$id";command.Parameters.AddWithValue("$id",id);
        return command.ExecuteScalar() is string json?JsonSerializer.Deserialize<RemotePendingChange>(json):null;
    }
    private static void ValidatePendingIdentity(RemotePendingChange current,RemotePendingChange proposed,bool content=false)
    {if(current.MountId!=proposed.MountId||current.Scope!=proposed.Scope||current.Path!=proposed.Path||content&&current.Entry?.Version!=proposed.Entry?.Version)throw new SourceConflictException("远端事件身份或内容版本已变化，未提交旧结果。");}
    /// <summary>原生版本已切换后，将映射版本和恢复阶段一同提交，上传扫描始终看到屏障。</summary>
    public void CommitRemotePlaceholder(RemotePendingChange pending,MountEntry? entry)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();
            var current=ReadPendingCore(db,tx,pending.Id)??throw new IOException("远端提交事件已丢失。");ValidatePendingIdentity(current,pending,content:true);
            if(current.State=="Superseded"||current.State=="Completed"&&pending.State!="Completed"||current.State=="Canceled"&&pending.State=="Committing")
                throw new SourceConflictException("远端事件已结束或被新扫描取代，未提交旧版本。");
            if(entry is not null)
            {
                using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE entries SET version=$v WHERE mount=$m AND id=$id";
                cmd.Parameters.AddWithValue("$v",entry.Version);cmd.Parameters.AddWithValue("$m",pending.MountId.ToString("N"));cmd.Parameters.AddWithValue("$id",entry.Id);
                if(cmd.ExecuteNonQuery()!=1)throw new IOException("原生版本切换的索引身份丢失，保留恢复状态。");
            }
            SavePendingCore(db,tx,pending);tx.Commit();
        }
    }
    /// <summary>上传扫描与发送前共用持久屏障，避免同步提供程序的版本切换被当成本地新编辑。</summary>
    public bool HasPendingRemote(Guid mount,string path)
    {
        lock(_gate)
        {
            using var db=Open();using var cmd=db.CreateCommand();
            cmd.CommandText="SELECT 1 WHERE EXISTS (SELECT 1 FROM remote_pending WHERE mount=$m AND json_extract(json,'$.State') NOT IN ('Completed','Superseded') AND NOT (json_extract(json,'$.State')='Canceled' AND coalesce(json_extract(json,'$.MetadataOnly'),0)=1) AND (path=$p OR substr($p,1,length(path)+1)=path||'/'))"+string.Concat(_protocolStorage.Select(module=>module.PendingPredicate is {} predicate?" OR ("+predicate+")":""));
            cmd.Parameters.AddWithValue("$m",mount.ToString("N"));cmd.Parameters.AddWithValue("$p",path);return cmd.ExecuteScalar() is not null;
        }
    }
    /// <summary>新凭据作用域不重放旧提示；不可逆本地提交保留证据和保护屏障，其他旧作用域批次退役。</summary>
    internal void ActivateRemoteScope(Guid mount,string scope)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();
            command.CommandText=string.Concat(_protocolStorage.Select(module=>module.ActivateScopeSql))+"UPDATE remote_pending SET json=json_set(json,'$.State','Superseded') WHERE mount=$m AND scope<>$s AND json_extract(json,'$.State') NOT IN ('Completed','Superseded','Committing','Hydrating')";
            command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$s",scope);command.ExecuteNonQuery();
        }
    }

}
