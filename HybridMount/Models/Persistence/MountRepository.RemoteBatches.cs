using System.Globalization;
using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>必须与pending及检查点共用事务；空轮次不创建展示任务，不按时间推测批次。</summary>
    private static void AttachRemoteBatchCore(SqliteConnection db,SqliteTransaction tx,RemoteDetection detection,IReadOnlyList<RemotePendingChange> created)
    {
        if(created.Count==0)return;
        using var command=db.CreateCommand();command.Transaction=tx;
        command.CommandText="INSERT INTO remote_task_batches VALUES($id,$mount,$scope,$connection,'Running',$now,NULL,NULL,0,0) ON CONFLICT(id) DO NOTHING";
        command.Parameters.AddWithValue("$id",detection.Id);command.Parameters.AddWithValue("$mount",detection.MountId.ToString("N"));command.Parameters.AddWithValue("$scope",detection.Scope);command.Parameters.AddWithValue("$connection",detection.Connection);command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));command.ExecuteNonQuery();
        command.CommandText="SELECT 1 FROM remote_task_batches WHERE id=$id AND mount=$mount AND scope=$scope AND connection=$connection AND discovery IN ('Running','CancelRequested')";
        if(command.ExecuteScalar() is null)throw new IOException("远端检测批次身份或生命周期不匹配。");
        command.CommandText="SELECT COALESCE(MAX(ordinal),-1)+1 FROM remote_task_batch_members WHERE batch=$id";var ordinal=Convert.ToInt32(command.ExecuteScalar());
        command.CommandText="SELECT cancel_requested FROM remote_task_batches WHERE id=$id";var canceled=Convert.ToInt32(command.ExecuteScalar())!=0;
        foreach(var original in created)
        {
            var pending=original;
            if(pending.MountId!=detection.MountId||pending.Scope!=detection.Scope)throw new IOException("事件不属于本轮检测。");
            command.Parameters.Clear();command.CommandText="SELECT materialized FROM entries WHERE mount=$mount AND source=$path LIMIT 1";command.Parameters.AddWithValue("$mount",pending.MountId.ToString("N"));command.Parameters.AddWithValue("$path",pending.Path);var materialized=command.ExecuteScalar();var existed=materialized is not null;
            var category=pending.Entry is {IsDirectory:false}&&materialized is not null&&Convert.ToInt32(materialized)!=0?"Unclassified":"Placeholder";
            if(canceled&&category=="Placeholder"){pending=pending with{State="Canceled",Error="本轮占位批次已取消。"};SavePendingCore(db,tx,pending);}
            command.Parameters.Clear();command.CommandText="INSERT INTO remote_task_batch_members VALUES($pending,$batch,$ordinal,$path,$category,$state,$reason,$action)";command.Parameters.AddWithValue("$category",category);command.Parameters.AddWithValue("$action",pending.Entry is null?"Deleted":existed?"Updated":"Created");
            command.Parameters.AddWithValue("$pending",pending.Id);command.Parameters.AddWithValue("$batch",detection.Id);command.Parameters.AddWithValue("$ordinal",ordinal++);command.Parameters.AddWithValue("$path",pending.Path);command.Parameters.AddWithValue("$state",pending.State??"Unknown");command.Parameters.AddWithValue("$reason",(object?)pending.Error??DBNull.Value);command.ExecuteNonQuery();
        }
    }
    internal void FinishRemoteDetection(string id,string state,string? error=null)
    {
        lock(_gate)
        {
            using var db=Open();using var query=db.CreateCommand();query.CommandText="UPDATE remote_task_batches SET discovery=$state,finished=$now,error=$error WHERE id=$id AND discovery IN ('Running','CancelRequested')";
            query.Parameters.AddWithValue("$id",id);query.Parameters.AddWithValue("$state",state);query.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));query.Parameters.AddWithValue("$error",(object?)error??DBNull.Value);query.ExecuteNonQuery();
        }
    }
    /// <summary>分类和状态与业务事件同事务更新，展示取消不能绕过不可逆阶段。</summary>
    private static void UpdateRemoteMemberCore(SqliteConnection db,SqliteTransaction tx,RemotePendingChange pending)
    {
        using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="UPDATE remote_task_batch_members SET category=CASE WHEN $download=1 THEN 'Download' WHEN $known=1 THEN 'Placeholder' ELSE category END,state=$state,reason=$reason WHERE pending=$pending";
        query.Parameters.AddWithValue("$pending",pending.Id);query.Parameters.AddWithValue("$download",pending.DownloadTask);query.Parameters.AddWithValue("$known",pending.ClassificationKnown);query.Parameters.AddWithValue("$state",pending.State??"Unknown");query.Parameters.AddWithValue("$reason",(object?)pending.Error??DBNull.Value);query.ExecuteNonQuery();
    }
    private const string BatchSummarySql="""
        SELECT b.id,b.mount,b.discovery,b.started,b.error,COUNT(*),
          SUM(CASE WHEN COALESCE(json_extract(p.json,'$.State'),m.state)='Completed' THEN 1 ELSE 0 END),
          SUM(CASE WHEN COALESCE(json_extract(p.json,'$.State'),m.state) IN ('Completed','Canceled','Superseded') THEN 1 ELSE 0 END),
          SUM(CASE WHEN COALESCE(json_extract(p.json,'$.State'),m.state)='Canceled' THEN 1 ELSE 0 END),
          SUM(CASE WHEN COALESCE(json_extract(p.json,'$.State'),m.state) IN ('Conflict','Failed','Waiting','Interrupted') OR COALESCE(json_extract(p.json,'$.Error'),m.reason) IS NOT NULL THEN 1 ELSE 0 END)
        FROM remote_task_batches b JOIN remote_task_batch_members m ON m.batch=b.id AND m.category<>'Download'
        LEFT JOIN remote_pending p ON p.id=m.pending
        """;
    private static IReadOnlyList<MountOperationSnapshot> ReadRemoteBatchesCore(SqliteConnection db,SqliteTransaction? tx=null,string? id=null,Guid? mount=null,bool activeTracesOnly=false,bool limitHistory=false)
    {
        using var query=db.CreateCommand();query.Transaction=tx;query.CommandText=BatchSummarySql+" WHERE b.hidden=0"+(id is not null?" AND b.id=$id":mount is not null?" AND b.mount=$mount":"")+(activeTracesOnly?" AND EXISTS(SELECT 1 FROM operation_runs r WHERE r.task=b.id AND r.state='Running')":"")+" GROUP BY b.id"+(limitHistory?" HAVING b.discovery IN ('Running','CancelRequested') OR SUM(CASE WHEN COALESCE(json_extract(p.json,'$.State'),m.state) IN ('Completed','Canceled','Superseded') THEN 1 ELSE 0 END)<COUNT(*) OR b.id IN (SELECT id FROM remote_task_batches WHERE hidden=0 ORDER BY started DESC LIMIT 100)":"")+" ORDER BY b.started DESC";
        if(id is not null)query.Parameters.AddWithValue("$id",id);if(mount is not null)query.Parameters.AddWithValue("$mount",mount.Value.ToString("N"));using var rows=query.ExecuteReader();var result=new List<MountOperationSnapshot>();
        while(rows.Read())
        {
            var total=rows.GetInt32(5);var completed=rows.GetInt32(6);var terminal=rows.GetInt32(7);var canceled=rows.GetInt32(8);var failed=rows.GetInt32(9);var discovery=rows.GetString(2);
            var state=discovery is "Running" or "CancelRequested"?"Running":discovery!="Completed"?completed>0?"PartialFailure":"Interrupted":terminal==total?canceled==total?"Canceled":canceled>0?"PartialFailure":"Completed":failed>0?"PartialFailure":"Queued";
            result.Add(new(rows.GetString(0),"remoteBatch",state,completed,total,[],rows.IsDBNull(4)?null:rows.GetString(4),Guid.ParseExact(rows.GetString(1),"N"),IsDirectory:true){SubmissionOrder=DateTimeOffset.Parse(rows.GetString(3),CultureInfo.InvariantCulture).UtcTicks});
        }
        return result;
    }
    internal IReadOnlyList<MountOperationSnapshot> RemoteBatches(Guid mount,bool activeTracesOnly=false)
    {lock(_gate){using var db=Open();return ReadRemoteBatchesCore(db,mount:mount,activeTracesOnly:activeTracesOnly);}}
    internal MountOperationSnapshot? FindRemoteBatch(string id)
    {lock(_gate){using var db=Open();return ReadRemoteBatchesCore(db,id:id).SingleOrDefault();}}
    internal IReadOnlyList<RemoteBatchMember> RemoteBatchMembers(string id,int offset=0,int limit=256)
    {
        if(offset<0||limit is <1 or >256)throw new ArgumentOutOfRangeException(nameof(limit));
        lock(_gate)
        {
            using var db=Open();using var query=db.CreateCommand();query.CommandText="SELECT m.pending,m.ordinal,m.path,m.category,COALESCE(json_extract(p.json,'$.State'),m.state),COALESCE(json_extract(p.json,'$.Error'),m.reason),m.action FROM remote_task_batch_members m LEFT JOIN remote_pending p ON p.id=m.pending WHERE m.batch=$id AND m.category<>'Download' ORDER BY m.ordinal LIMIT $limit OFFSET $offset";
            query.Parameters.AddWithValue("$id",id);query.Parameters.AddWithValue("$limit",limit);query.Parameters.AddWithValue("$offset",offset);
            using var rows=query.ExecuteReader();var result=new List<RemoteBatchMember>();while(rows.Read())result.Add(new(rows.GetString(0),id,rows.GetInt32(1),rows.GetString(2),rows.GetString(3),rows.GetString(4),rows.IsDBNull(5)?null:rows.GetString(5),rows.GetString(6)));return result;
        }
    }
    /// <summary>批次取消/重试仅操作占位成员；成功项、独立下载和不可逆提交不受影响。</summary>
    internal IReadOnlyList<string> ChangeRemoteBatch(string id,bool retry)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var query=db.CreateCommand();query.Transaction=tx;
            if(!retry){query.CommandText="UPDATE remote_task_batches SET cancel_requested=1 WHERE id=$id";query.Parameters.AddWithValue("$id",id);query.ExecuteNonQuery();query.Parameters.Clear();}
            query.CommandText="SELECT p.json FROM remote_task_batch_members m LEFT JOIN remote_pending p ON p.id=m.pending WHERE m.batch=$id AND m.category='Placeholder' AND p.id IS NOT NULL AND COALESCE(json_extract(p.json,'$.State'),m.state) "+(retry?"IN ('Canceled','Failed','Waiting','Conflict','Interrupted')":"NOT IN ('Completed','Canceled','Superseded','Committing','Hydrating')");query.Parameters.AddWithValue("$id",id);
            var changes=new List<RemotePendingChange>();using(var rows=query.ExecuteReader())while(rows.Read())changes.Add(System.Text.Json.JsonSerializer.Deserialize<RemotePendingChange>(rows.GetString(0))!);
            if(retry){query.CommandText="UPDATE remote_task_batches SET cancel_requested=0,discovery=CASE WHEN discovery='Running' THEN 'Running' ELSE 'Completed' END,error=NULL WHERE id=$id";query.ExecuteNonQuery();}
            foreach(var item in changes)SavePendingCore(db,tx,item with{State=retry?"Pending":"Canceled",AttemptId=retry?Guid.NewGuid().ToString("N"):item.AttemptId,AttemptGeneration=retry?item.AttemptGeneration+1:item.AttemptGeneration,Error=retry?null:"已取消本轮占位更新；保留已完成项，请显式重试。"});
            tx.Commit();return changes.Select(item=>item.Id).ToArray();
        }
    }
    /// <summary>实际CFAPI分类与展示归属原子提交；先前批次取消只作用于确认的冷占位。</summary>
    internal RemotePendingChange ClassifyRemotePending(RemotePendingChange proposed,bool download)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();var current=ReadPendingCore(db,tx,proposed.Id)??throw new IOException("分类事件已丢失。");ValidatePendingIdentity(current,proposed);
            if(current.State is "Completed" or "Canceled" or "Superseded")return current;
            using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="SELECT b.cancel_requested FROM remote_task_batch_members m JOIN remote_task_batches b ON b.id=m.batch WHERE m.pending=$id";query.Parameters.AddWithValue("$id",current.Id);var canceled=Convert.ToInt32(query.ExecuteScalar()??0)!=0;
            var next=proposed with{DownloadTask=download,ClassificationKnown=true,AttemptId=download?proposed.AttemptId??Guid.NewGuid().ToString("N"):proposed.AttemptId,AttemptGeneration=download?Math.Max(1,proposed.AttemptGeneration):proposed.AttemptGeneration};
            if(!download&&canceled&&current.State is not("Committing" or "Hydrating"))next=next with{State="Canceled",Error="已取消本轮冷占位更新。"};
            SavePendingCore(db,tx,next);tx.Commit();return next;
        }
    }
    /// <summary>快速创建发送前接纳不可逆阶段，取消后尚未接纳的项不会进入CFAPI。</summary>
    internal IReadOnlySet<string> BeginColdCreationBatch(IReadOnlyList<string> ids)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();var accepted=new HashSet<string>(StringComparer.Ordinal);
            foreach(var id in ids)
            {
                var current=ReadPendingCore(db,tx,id);if(current?.State!="Pending")continue;
                SavePendingCore(db,tx,current with{State="Committing",ClassificationKnown=true});accepted.Add(id);
            }
            tx.Commit();return accepted;
        }
    }
    /// <summary>仅隐藏已结束且无恢复依赖的父历史，成员归属继续抑制内部卡片；不删除pending或快照。</summary>
    private static int ClearRemoteBatchHistoryCore(SqliteConnection db,TaskHistoryClearMode mode)
    {
        var candidates=ReadRemoteBatchesCore(db).Where(item=>mode==TaskHistoryClearMode.Completed?item.State=="Completed":mode==TaskHistoryClearMode.Failed?item.State is "Failed" or "PartialFailure" or "Interrupted":item.State is not("Running" or "Queued")).Select(item=>item.Id).ToArray();
        var count=0;using var command=db.CreateCommand();
        foreach(var id in candidates)
        {
            command.Parameters.Clear();command.CommandText="UPDATE remote_task_batches SET hidden=1 WHERE id=$id AND discovery NOT IN ('Running','CancelRequested') AND NOT EXISTS(SELECT 1 FROM remote_task_batch_members m LEFT JOIN remote_pending p ON p.id=m.pending WHERE m.batch=$id AND (COALESCE(json_extract(p.json,'$.State'),m.state) NOT IN ('Completed','Canceled','Superseded') OR json_extract(p.json,'$.SnapshotPath') IS NOT NULL AND COALESCE(json_extract(p.json,'$.State'),m.state) NOT IN ('Completed','Superseded')))";command.Parameters.AddWithValue("$id",id);count+=command.ExecuteNonQuery();
        }
        return count;
    }
    internal IReadOnlyList<string> BatchIdsForPending(IReadOnlyList<string> ids)
    {
        if(ids.Count==0)return [];
        lock(_gate){using var db=Open();using var query=db.CreateCommand();query.CommandText="SELECT DISTINCT batch FROM remote_task_batch_members WHERE category<>'Download' AND pending IN (SELECT value FROM json_each($ids))";query.Parameters.AddWithValue("$ids",System.Text.Json.JsonSerializer.Serialize(ids));using var rows=query.ExecuteReader();var result=new List<string>();while(rows.Read())result.Add(rows.GetString(0));return result;}
    }
    internal void RecoverRemoteDetections()
    {lock(_gate){using var db=Open();using var query=db.CreateCommand();query.CommandText="UPDATE remote_task_batches SET discovery='Interrupted',finished=$now,error='检测进程已结束；保留已提交成员，不用不完整列表推断删除。' WHERE discovery IN ('Running','CancelRequested')";query.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));query.ExecuteNonQuery();}}
}
