using HybridMount.Models.Mounting.Tasks;
using HybridMount.Infrastructure.Files;
using HybridMount.Models.Mounting;
using System.Diagnostics;
using System.Text.Json;
namespace HybridMount.Infrastructure.Persistence;

public sealed partial class MountRepository
{
    /// <summary>最近任务读取的只读诊断，不发远端请求；查询数不随任务行数倍增。</summary>
    internal (long Rows,double Milliseconds,int Queries) LastTaskRead {get;private set;}
    internal TaskStateSnapshot ReadTaskState()
    {
        var started=Stopwatch.GetTimestamp();List<(long Order,string Json)> history=[];List<string> uploads=[],remote=[];
        IReadOnlyList<MountOperationSnapshot> batches=[];HashSet<string> batchedIds=new(StringComparer.Ordinal);
        List<(Guid Mount,MountEntry Entry)> deleted=[];HashSet<string> hints=new(StringComparer.Ordinal);
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction(deferred:true);
            using var query=db.CreateCommand();query.Transaction=tx;
            query.CommandText="SELECT rowid,json FROM operations WHERE json_extract(json,'$.State') NOT IN ('Completed','Canceled','Ignored','Superseded','Failed','PartialFailure','Interrupted') OR rowid IN (SELECT rowid FROM operations WHERE json_extract(json,'$.State') IN ('Completed','Canceled','Ignored','Superseded','Failed','PartialFailure','Interrupted') ORDER BY rowid DESC LIMIT 100) ORDER BY rowid DESC";
            using(var rows=query.ExecuteReader())while(rows.Read())history.Add((rows.GetInt64(0),rows.GetString(1)));
            query.CommandText="SELECT json FROM uploads ORDER BY rowid DESC";using(var rows=query.ExecuteReader())while(rows.Read())uploads.Add(rows.GetString(0));
            query.CommandText="SELECT json FROM remote_pending ORDER BY rowid";using(var rows=query.ExecuteReader())while(rows.Read())remote.Add(rows.GetString(0));
            query.CommandText="SELECT e.mount,e.id,e.source,e.local,e.version,e.directory,e.deleted,e.protected,e.materialized FROM entries e JOIN remote_presence p ON p.mount=e.mount AND p.id=e.id WHERE p.presence=2";
            using(var rows=query.ExecuteReader())while(rows.Read())deleted.Add((Guid.Parse(rows.GetString(0)),new(rows.GetString(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.GetInt64(5)!=0,rows.GetInt64(6)!=0,rows.GetInt64(7)!=0,rows.GetInt64(8)!=0,RemotePresence.Deleted)));
            var predicates=_protocolStorage.Select(x=>x.PendingPredicate).Where(x=>x is not null).Select(x=>"("+x!.Replace("$m","u.mount").Replace("$p","json_extract(u.json,'$.SourcePath')")+")").ToArray();
            query.CommandText="SELECT u.id FROM uploads u WHERE "+(predicates.Length==0?"0":string.Join(" OR ",predicates));
            using(var rows=query.ExecuteReader())while(rows.Read())hints.Add(rows.GetString(0));
            batches=ReadRemoteBatchesCore(db,tx,limitHistory:true);
            query.CommandText="SELECT pending FROM remote_task_batch_members WHERE category<>'Download'";using(var rows=query.ExecuteReader())while(rows.Read())batchedIds.Add(rows.GetString(0));tx.Commit();
        }
        var result=new TaskStateSnapshot
        {
            History=history.Select(x=>{var item=JsonSerializer.Deserialize<MountOperationSnapshot>(x.Json)! with{SubmissionOrder=x.Order};return item.State is "Queued" or "Running" or "Canceling"?item with{State="Interrupted",Error="上次进程已结束；修改操作未被自动重放。"}:item;}).ToArray(),
            Uploads=uploads.Select(x=>JsonSerializer.Deserialize<LocalUpload>(x)??throw new IOException("上传记录损坏。")).ToArray(),
            Remote=remote.Select(x=>JsonSerializer.Deserialize<RemotePendingChange>(x)??throw new IOException("远端记录损坏。")).ToArray(),Deleted=deleted,ProtocolBlockedUploads=hints,RemoteBatches=batches,BatchedRemoteIds=batchedIds
        };
        result.Index();LastTaskRead=(history.Count+uploads.Count+remote.Count+deleted.Count,Stopwatch.GetElapsedTime(started).TotalMilliseconds,7);return result;
    }
    /// <summary>交互或执行点查相关祖先；不加载整挂载的JSON。</summary>
    internal IReadOnlyList<RemotePendingChange> RemoteAncestors(Guid mount,string path)
    {
        List<string> json=[];lock(_gate)
        {
            using var db=Open();using var query=db.CreateCommand();query.CommandText="SELECT json FROM remote_pending WHERE mount=$m AND path COLLATE HM_PATH IN (SELECT value FROM json_each($paths)) AND "+ActiveRemoteBarrierSql;
            query.Parameters.AddWithValue("$m",mount.ToString("N"));query.Parameters.AddWithValue("$paths",JsonSerializer.Serialize(TaskStateSnapshot.Ancestors(path)));using var rows=query.ExecuteReader();while(rows.Read())json.Add(rows.GetString(0));
        }
        return json.Select(x=>JsonSerializer.Deserialize<RemotePendingChange>(x)!).ToArray();
    }
    /// <summary>执行前查询同路径、祖先及必要子树上传；不以页面快照授予写入权。</summary>
    internal IReadOnlyList<LocalUpload> UploadsRelated(Guid mount,string path,bool subtree=true)
    {
        List<string> json=[];lock(_gate)
        {
            using var db=Open();using var query=db.CreateCommand();
            query.CommandText=path.Length==0&&subtree?"SELECT json FROM uploads WHERE mount=$m":"SELECT json FROM uploads WHERE mount=$m AND (json_extract(json,'$.SourcePath') COLLATE HM_PATH IN (SELECT value FROM json_each($parents))"+(subtree?" OR (json_extract(json,'$.SourcePath') COLLATE HM_PATH >= $start AND json_extract(json,'$.SourcePath') COLLATE HM_PATH < $end)":"")+")";
            query.Parameters.AddWithValue("$m",mount.ToString("N"));query.Parameters.AddWithValue("$parents",JsonSerializer.Serialize(TaskStateSnapshot.Ancestors(path)));
            query.Parameters.AddWithValue("$start",path+"/");query.Parameters.AddWithValue("$end",path+"0");using var rows=query.ExecuteReader();while(rows.Read())json.Add(rows.GetString(0));
        }
        return json.Select(x=>JsonSerializer.Deserialize<LocalUpload>(x)!).ToArray();
    }
    /// <summary>已删除祖先按源路径索引定位，仅返回现存的远端删除保护。</summary>
    internal MountEntry? DeletedSourceAncestor(Guid mount,string path)
    {
        lock(_gate)
        {
            using var db=Open();using var query=db.CreateCommand();
            query.CommandText="SELECT e.id,e.source,e.local,e.version,e.directory,e.deleted,e.protected,e.materialized FROM entries e JOIN remote_presence p ON p.mount=e.mount AND p.id=e.id WHERE e.mount=$m AND p.presence=2 AND e.source COLLATE HM_PATH IN (SELECT value FROM json_each($parents)) ORDER BY length(e.source) DESC LIMIT 1";
            query.Parameters.AddWithValue("$m",mount.ToString("N"));query.Parameters.AddWithValue("$parents",JsonSerializer.Serialize(TaskStateSnapshot.Ancestors(path)));using var row=query.ExecuteReader();
            return row.Read()?new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetInt64(4)!=0,row.GetInt64(5)!=0,row.GetInt64(6)!=0,row.GetInt64(7)!=0,RemotePresence.Deleted):null;
        }
    }

}
