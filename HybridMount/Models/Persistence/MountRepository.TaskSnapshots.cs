using System.Diagnostics;
using System.Text.Json;
namespace HybridMount.Models.Persistence;

/// <summary>一次任务页读取的不可变业务视图；仅用于展示，执行授权必须重新点查。</summary>
internal sealed class TaskStateSnapshot
{
    internal required IReadOnlyList<MountOperationSnapshot> History {get;init;}
    internal required IReadOnlyList<LocalUpload> Uploads {get;init;}
    internal required IReadOnlyList<RemotePendingChange> Remote {get;init;}
    internal required IReadOnlyList<(Guid Mount,MountEntry Entry)> Deleted {get;init;}
    internal required IReadOnlySet<string> ProtocolBlockedUploads {get;init;}
    internal readonly Dictionary<string,LocalUpload> ById=new(StringComparer.Ordinal);
    private readonly Dictionary<Guid,Dictionary<string,List<RemotePendingChange>>> _remote=[];
    private readonly Dictionary<Guid,Dictionary<string,MountEntry>> _deleted=[];
    internal void Index()
    {
        foreach(var item in Uploads)ById[item.Id]=item;
        foreach(var item in Remote.Where(MountRepository.BlocksLocalPath))
        {
            if(!_remote.TryGetValue(item.MountId,out var paths))_remote[item.MountId]=paths=new(StringComparer.OrdinalIgnoreCase);
            if(!paths.TryGetValue(item.Path,out var values))paths[item.Path]=values=[];values.Add(item);
        }
        foreach(var (mount,entry) in Deleted)
        {if(!_deleted.TryGetValue(mount,out var paths))_deleted[mount]=paths=new(StringComparer.OrdinalIgnoreCase);paths[entry.SourcePath]=entry;}
    }
    internal static IEnumerable<string> Ancestors(string path)
    {while(true){yield return path;if(path.Length==0)yield break;path=RelativePaths.Parent(path);}}
    internal RemotePendingChange? Blocker(Guid mount,string path,string id)
    {
        if(_remote.TryGetValue(mount,out var paths))foreach(var ancestor in Ancestors(path))
            if(paths.TryGetValue(ancestor,out var values)&&values.FirstOrDefault(x=>x.Id!=id) is {} blocker)return blocker;
        return null;
    }
    internal MountEntry? DeletedAncestor(Guid mount,string path)
    {if(_deleted.TryGetValue(mount,out var paths))foreach(var ancestor in Ancestors(path))if(paths.TryGetValue(ancestor,out var entry))return entry;return null;}
}

public sealed partial class MountRepository
{
    /// <summary>最近任务读取的只读诊断，不发远端请求；查询数不随任务行数倍增。</summary>
    internal (long Rows,double Milliseconds,int Queries) LastTaskRead {get;private set;}
    internal TaskStateSnapshot ReadTaskState()
    {
        var started=Stopwatch.GetTimestamp();List<(long Order,string Json)> history=[];List<string> uploads=[],remote=[];
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
            using(var rows=query.ExecuteReader())while(rows.Read())hints.Add(rows.GetString(0));tx.Commit();
        }
        var result=new TaskStateSnapshot
        {
            History=history.Select(x=>{var item=JsonSerializer.Deserialize<MountOperationSnapshot>(x.Json)! with{SubmissionOrder=x.Order};return item.State is "Queued" or "Running" or "Canceling"?item with{State="Interrupted",Error="上次进程已结束；修改操作未被自动重放。"}:item;}).ToArray(),
            Uploads=uploads.Select(x=>JsonSerializer.Deserialize<LocalUpload>(x)??throw new IOException("上传记录损坏。")).ToArray(),
            Remote=remote.Select(x=>JsonSerializer.Deserialize<RemotePendingChange>(x)??throw new IOException("远端记录损坏。")).ToArray(),Deleted=deleted,ProtocolBlockedUploads=hints
        };
        result.Index();LastTaskRead=(history.Count+uploads.Count+remote.Count+deleted.Count,Stopwatch.GetElapsedTime(started).TotalMilliseconds,5);return result;
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
