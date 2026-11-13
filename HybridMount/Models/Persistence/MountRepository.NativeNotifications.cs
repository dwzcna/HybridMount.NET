using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
#if HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>仅隔离测试在事务已写入、提交前注入数据库失败，验证回滚和重试。</summary>
    internal Action? NativeNotificationCommittingForTests{get;set;}
#endif
    /// <summary>改名子树同事务更新；重试只更新原路径或确认已完成的目标路径；索引已被更晚观察推进时返回空，由管理器补扫，不回写旧路径。</summary>
    internal LocalRenameNotice? ApplyNativeRename(Guid mount,string id,string oldPath,string target)
    {
        if(oldPath.Length==0||target.Length==0)throw new ArgumentException("改名不能改变挂载根。");
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();
            var entry=ReadNotificationEntryCore(db,tx,mount,id);
            if(entry is null)return null;
            if(entry.LocalPath.Equals(target,StringComparison.Ordinal))return new(oldPath,target,entry with{LocalPath=oldPath});
            if(!entry.LocalPath.Equals(oldPath,StringComparison.OrdinalIgnoreCase))return null;
            using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="UPDATE entries SET local=$target||substr(local,length($old)+1),protected=1 WHERE mount=$m AND (local COLLATE HM_PATH=$old OR (local COLLATE HM_PATH >= $start AND local COLLATE HM_PATH < $end))";
            command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$target",target);command.Parameters.AddWithValue("$old",oldPath);command.Parameters.AddWithValue("$start",oldPath+"/");command.Parameters.AddWithValue("$end",oldPath+"0");command.ExecuteNonQuery();
#if HYBRIDMOUNT_HEADLESS_TEST
            NativeNotificationCommittingForTests?.Invoke();
#endif
            tx.Commit();return new(oldPath,target,entry);
        }
    }
    /// <summary>删除保护按实际索引子树一次事务保存；失败全部回滚，远端已删除记录保持原语义。</summary>
    internal void ApplyNativeDeletion(Guid mount,string id)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();
            var entry=ReadNotificationEntryCore(db,tx,mount,id);if(entry is null)return;
            using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="UPDATE entries SET deleted=1,protected=1 WHERE mount=$m AND ($path='' OR local COLLATE HM_PATH=$path OR (local COLLATE HM_PATH >= $start AND local COLLATE HM_PATH < $end)) AND coalesce((SELECT presence FROM remote_presence p WHERE p.mount=entries.mount AND p.id=entries.id),0)<>$removed";
            command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$path",entry.LocalPath);command.Parameters.AddWithValue("$start",entry.LocalPath+"/");command.Parameters.AddWithValue("$end",entry.LocalPath+"0");command.Parameters.AddWithValue("$removed",(int)RemotePresence.Deleted);command.ExecuteNonQuery();
#if HYBRIDMOUNT_HEADLESS_TEST
            NativeNotificationCommittingForTests?.Invoke();
#endif
            tx.Commit();
        }
    }
    /// <summary>通知事务内按挂载和条目ID精确读取；不打开新连接或释放事务外的快照。</summary>
    private static MountEntry? ReadNotificationEntryCore(SqliteConnection db,SqliteTransaction tx,Guid mount,string id)
    {
        using var command=db.CreateCommand();command.Transaction=tx;
        command.CommandText="SELECT e.id,e.source,e.local,e.version,e.directory,e.deleted,e.protected,e.materialized,coalesce(p.presence,0) FROM entries e LEFT JOIN remote_presence p ON p.mount=e.mount AND p.id=e.id WHERE e.mount=$m AND e.id=$id";
        command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$id",id);using var row=command.ExecuteReader();
        return row.Read()?new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetInt64(4)!=0,row.GetInt64(5)!=0,row.GetInt64(6)!=0,row.GetInt64(7)!=0,(RemotePresence)row.GetInt64(8)):null;
    }
}
