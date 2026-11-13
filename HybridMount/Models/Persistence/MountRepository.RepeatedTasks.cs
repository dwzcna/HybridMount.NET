using System.Text.Json;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>无需文件动作的重复观测按256项核对收尾；同一事务重验当前事件、索引、依赖和存在性。</summary>
    internal IReadOnlySet<string> CompleteRepeatedRemoteTasks(Guid mount,IReadOnlyList<RemotePendingChange> candidates,Func<string,string,bool> sameVersion)
    {
        if(candidates.Count>256)throw new ArgumentOutOfRangeException(nameof(candidates));
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();var completed=new HashSet<string>(StringComparer.Ordinal);
            foreach(var proposed in candidates)
            {
                var current=ReadPendingCore(db,tx,proposed.Id);
                if(current is null||current.MountId!=mount||current.State!="Pending"||current.Entry is not {} metadata||!RemoteDependenciesReadyCore(db,tx,current))continue;
                using var query=db.CreateCommand();query.Transaction=tx;
                query.CommandText="SELECT e.id,e.version,e.directory FROM entries e LEFT JOIN remote_presence p ON p.mount=e.mount AND p.id=e.id WHERE e.mount=$m AND e.source=$path AND e.materialized=1 AND e.deleted=0 AND e.protected=0 AND COALESCE(p.presence,0)<>2";
                query.Parameters.AddWithValue("$m",mount.ToString("N"));query.Parameters.AddWithValue("$path",current.Path);
                string id,version;bool directory;
                using(var row=query.ExecuteReader()){if(!row.Read())continue;id=row.GetString(0);version=row.GetString(1);directory=row.GetInt64(2)!=0;}
                if(directory!=metadata.IsDirectory||!sameVersion(version,metadata.Version))continue;
                current=current with{State="Completed",Error=null};SavePendingCore(db,tx,current);
                query.CommandText="INSERT INTO remote_presence VALUES($m,$entry,1) ON CONFLICT(mount,id) DO UPDATE SET presence=1";query.Parameters.AddWithValue("$entry",id);query.ExecuteNonQuery();
                var snapshot=new MountOperationSnapshot(current.Id,"remoteUpdate","Completed",1,1,[],null,mount,current.Path,metadata.Size,metadata.Size,metadata.IsDirectory,current.Path);
                query.CommandText="INSERT INTO operations VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json";query.Parameters.AddWithValue("$id",current.Id);query.Parameters.AddWithValue("$json",JsonSerializer.Serialize(snapshot));query.ExecuteNonQuery();
                query.CommandText="UPDATE file_tasks SET json=json_set(json,'$.State','Completed') WHERE id=$id AND mount=$m;UPDATE task_steps SET json=json_set(json,'$.State','Verified','$.Error',NULL,'$.HResult',NULL) WHERE task=$id";query.ExecuteNonQuery();completed.Add(current.Id);
            }
            tx.Commit();return completed;
        }
    }
}
