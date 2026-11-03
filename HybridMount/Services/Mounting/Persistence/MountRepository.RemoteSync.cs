using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;
namespace HybridMount.Services.Mounting;

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
        cmd.Parameters.AddWithValue("$m",mount.ToString("N"));cmd.Parameters.AddWithValue("$s",scope);cmd.Parameters.AddWithValue("$p",path);cmd.Parameters.AddWithValue("$j",json);cmd.Parameters.AddWithValue("$id",(object?)id??DBNull.Value);cmd.ExecuteNonQuery();
    }
    /// <summary>事件、远端观测和检查点在同一事务持久化；本地占用或中断不导致游标前进后丢事件。</summary>
    public void CommitRemoteBatch(Guid mount,string scope,RemoteCollectionCheckpoint checkpoint,IReadOnlyList<RemoteShadowEntry> changed,IReadOnlyList<string> deleted,IReadOnlySet<string>? silent=null,string? scan=null,string? hint=null)
    {
        lock(_gate)
        {
            using var db=Open();using var transaction=db.BeginTransaction();
            var oldPending=RemotePending(mount,scope);
            foreach(var path in changed.Select(x=>x.Entry.Path).Concat(deleted))
                foreach(var pending in oldPending.Where(x=>(x.Path==path||deleted.Contains(path)&&x.Path.StartsWith(path+"/",StringComparison.Ordinal))&&x.State is not("Completed" or "Superseded" or "Committing" or "Hydrating")))
                    SavePendingCore(db,transaction,pending with{State="Superseded",Error=null});
            foreach(var shadow in changed)
            {
                if(!shadow.Entry.IsDirectory)
                    RemoteCommand(db,transaction,"DELETE FROM remote_shadow WHERE mount=$m AND scope=$s AND substr(path,1,length($p)+1)=$p||'/';DELETE FROM remote_collections WHERE mount=$m AND scope=$s AND (path=$p OR substr(path,1,length($p)+1)=$p||'/')",mount,scope,shadow.Entry.Path,"");
                RemoteCommand(db,transaction,"INSERT INTO remote_shadow VALUES($m,$s,$p,$j) ON CONFLICT(mount,scope,path) DO UPDATE SET json=$j",mount,scope,shadow.Entry.Path,JsonSerializer.Serialize(shadow));
                if(silent?.Contains(shadow.Entry.Path)!=true)SavePendingCore(db,transaction,new(Guid.NewGuid().ToString("N"),mount,scope,shadow.Entry.Path,shadow.Entry));
            }
            foreach(var path in deleted)
            {
                RemoteCommand(db,transaction,"DELETE FROM remote_shadow WHERE mount=$m AND scope=$s AND (path=$p OR substr(path,1,length($p)+1)=$p||'/');DELETE FROM remote_collections WHERE mount=$m AND scope=$s AND (path=$p OR substr(path,1,length($p)+1)=$p||'/')",mount,scope,path,"");
                SavePendingCore(db,transaction,new(Guid.NewGuid().ToString("N"),mount,scope,path,null));
            }
            RemoteCommand(db,transaction,"INSERT INTO remote_collections VALUES($m,$s,$p,$j) ON CONFLICT(mount,scope,path) DO UPDATE SET json=$j",mount,scope,checkpoint.Path,JsonSerializer.Serialize(checkpoint));
            if(hint is not null)MarkS3HintCore(db,transaction,mount,scope,hint);
            if(scan is not null)
            {using var done=db.CreateCommand();done.Transaction=transaction;done.CommandText="UPDATE remote_scans SET state='Completed' WHERE id=$id AND mount=$m AND scope=$s";done.Parameters.AddWithValue("$id",scan);done.Parameters.AddWithValue("$m",mount.ToString("N"));done.Parameters.AddWithValue("$s",scope);if(done.ExecuteNonQuery()!=1)throw new IOException("远端扫描身份丢失，未提交快照。");}
            transaction.Commit();
        }
    }
    private static void SavePendingCore(SqliteConnection db,SqliteTransaction transaction,RemotePendingChange pending)
        =>RemoteCommand(db,transaction,"INSERT INTO remote_pending VALUES($id,$m,$s,$p,$j) ON CONFLICT(id) DO UPDATE SET json=$j",pending.MountId,pending.Scope,pending.Path,JsonSerializer.Serialize(pending),pending.Id);
    /// <summary>提交阶段先持久化，重启仍可使用原快照和本地身份核对，不依赖任务历史重放。</summary>
    public void SaveRemotePending(RemotePendingChange pending)
    {
        lock(_gate){using var db=Open();using var tx=db.BeginTransaction();SavePendingCore(db,tx,pending);tx.Commit();}
    }
    /// <summary>原生版本已切换后，将映射版本和恢复阶段一同提交，上传扫描始终看到屏障。</summary>
    public void CommitRemotePlaceholder(RemotePendingChange pending,MountEntry? entry)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();
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
            cmd.CommandText="SELECT 1 WHERE EXISTS (SELECT 1 FROM remote_pending WHERE mount=$m AND json_extract(json,'$.State') NOT IN ('Completed','Superseded') AND (path=$p OR substr($p,1,length(path)+1)=path||'/')) OR EXISTS (SELECT 1 FROM s3_event_hints WHERE mount=$m AND json_extract(json,'$.State')='Pending' AND (path=$p OR substr($p,1,length(path)+1)=path||'/'))";
            cmd.Parameters.AddWithValue("$m",mount.ToString("N"));cmd.Parameters.AddWithValue("$p",path);return cmd.ExecuteScalar() is not null;
        }
    }
}
