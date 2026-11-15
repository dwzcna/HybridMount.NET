using System.Globalization;
using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>接纳时声明适用步骤；已有同代运行继续，显式重试才新增尝试。锁内不执行远端或UI操作。</summary>
    internal string BeginOperationRun(string task,string generation,IReadOnlyList<string> steps,bool retry=false)
    {
        if(steps.Count is <1 or >64||steps.Distinct(StringComparer.Ordinal).Count()!=steps.Count)throw new ArgumentException("任务步骤无效。");
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var query=db.CreateCommand();query.Transaction=tx;
            query.CommandText="SELECT id,attempt,generation,state FROM operation_runs WHERE task=$task ORDER BY attempt DESC LIMIT 1";query.Parameters.AddWithValue("$task",task);
            string? previous=null;var attempt=0;string? previousGeneration=null,state=null;
            using(var row=query.ExecuteReader())if(row.Read()){previous=row.GetString(0);attempt=row.GetInt32(1);previousGeneration=row.GetString(2);state=row.GetString(3);}
            if(previous is not null&&!retry&&generation==previousGeneration&&state is "Running" or "Completed")return previous;
            if(previous is not null&&state=="Running")EndOperationCore(db,tx,previous,"Interrupted","任务代次已变化；保留原执行证据。");
            var id=Guid.NewGuid().ToString("N");query.Parameters.Clear();query.CommandText="INSERT INTO operation_runs VALUES($id,$task,$attempt,$generation,'Running',$now,NULL,NULL)";
            query.Parameters.AddWithValue("$id",id);query.Parameters.AddWithValue("$task",task);query.Parameters.AddWithValue("$attempt",attempt+1);query.Parameters.AddWithValue("$generation",generation);query.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));query.ExecuteNonQuery();
            for(var ordinal=0;ordinal<steps.Count;ordinal++)
            {
                query.Parameters.Clear();query.CommandText="INSERT INTO operation_steps(run,ordinal,name,state) VALUES($run,$ordinal,$name,'Pending')";
                query.Parameters.AddWithValue("$run",id);query.Parameters.AddWithValue("$ordinal",ordinal);query.Parameters.AddWithValue("$name",steps[ordinal]);query.ExecuteNonQuery();
            }
            tx.Commit();return id;
        }
    }
    /// <summary>在实际阶段边界推进。只把已经开始的上一阶段标完成，未执行阶段不补造成功。</summary>
    internal void AdvanceOperation(string task,string name,string? evidence=null)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT id FROM operation_runs WHERE task=$task AND state='Running' ORDER BY attempt DESC LIMIT 1";command.Parameters.AddWithValue("$task",task);
            if(command.ExecuteScalar() is not string run)return;
            command.Parameters.Clear();command.Parameters.AddWithValue("$run",run);command.Parameters.AddWithValue("$name",name);command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
            command.CommandText="SELECT state FROM operation_steps WHERE run=$run AND name=$name";var current=command.ExecuteScalar() as string;
            if(current is null)throw new IOException("未登记的展示阶段："+name);
            if(current=="Running")return;
            command.CommandText="UPDATE operation_steps SET state='Completed',finished=$now WHERE run=$run AND state='Running';UPDATE operation_steps SET state='Running',started=COALESCE(started,$now),finished=NULL,reason=NULL,evidence=$e WHERE run=$run AND name=$name";command.Parameters.AddWithValue("$e",(object?)evidence??DBNull.Value);command.ExecuteNonQuery();tx.Commit();
        }
    }
    /// <summary>调用者按250ms合并真实传输进度；未开始或已经退出的阶段不接纳迟到更新。</summary>
    internal void UpdateOperationProgress(string task,string stage,long completed,long total)
    {
        if(completed<0||total<0||completed>total)return;
        lock(_gate){using var db=Open();using var query=db.CreateCommand();query.CommandText="UPDATE operation_steps SET completed=$completed,total=$total WHERE run=(SELECT id FROM operation_runs WHERE task=$task AND state='Running' ORDER BY attempt DESC LIMIT 1) AND name=$stage AND state='Running'";query.Parameters.AddWithValue("$completed",completed);query.Parameters.AddWithValue("$total",total);query.Parameters.AddWithValue("$task",task);query.Parameters.AddWithValue("$stage",stage);query.ExecuteNonQuery();}
    }
    internal void EndOperationRun(string task,string state,string? reason=null)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT id FROM operation_runs WHERE task=$task AND state='Running' ORDER BY attempt DESC LIMIT 1";command.Parameters.AddWithValue("$task",task);
            if(command.ExecuteScalar() is string run){EndOperationCore(db,tx,run,state,reason);tx.Commit();}
        }
    }
    private static void EndOperationCore(SqliteConnection db,SqliteTransaction tx,string run,string state,string? reason)
    {
        using var command=db.CreateCommand();command.Transaction=tx;
        command.CommandText="UPDATE operation_runs SET state=$state,finished=$now,reason=$reason WHERE id=$run;UPDATE operation_steps SET state=$state,finished=$now,reason=$reason WHERE run=$run AND state='Running';UPDATE operation_steps SET reason=COALESCE($reason,'后续步骤未执行；没有补造完成证据。') WHERE run=$run AND state='Pending'";
        command.Parameters.AddWithValue("$run",run);command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));command.Parameters.AddWithValue("$reason",(object?)reason??DBNull.Value);command.ExecuteNonQuery();
    }
    /// <summary>启动恢复仅标记展示中断，不重放业务请求；真实恢复仍由task_steps／业务日志负责。</summary>
    internal void RecoverOperationRuns()
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="SELECT id FROM operation_runs WHERE state='Running'";
            var ids=new List<string>();using(var rows=query.ExecuteReader())while(rows.Read())ids.Add(rows.GetString(0));
            foreach(var id in ids)EndOperationCore(db,tx,id,"Interrupted","进程已结束，实际结果由业务恢复核对。");tx.Commit();
        }
    }
    internal void DiscardUnpublishedTrace(string task)
    {
        lock(_gate){using var db=Open();using var tx=db.BeginTransaction();using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="DELETE FROM operation_steps WHERE run IN (SELECT id FROM operation_runs WHERE task=$task) AND NOT EXISTS(SELECT 1 FROM operations WHERE id=$task) AND NOT EXISTS(SELECT 1 FROM remote_task_batches WHERE id=$task);DELETE FROM operation_runs WHERE task=$task AND NOT EXISTS(SELECT 1 FROM operations WHERE id=$task) AND NOT EXISTS(SELECT 1 FROM remote_task_batches WHERE id=$task)";query.Parameters.AddWithValue("$task",task);query.ExecuteNonQuery();tx.Commit();}
    }
    internal int OperationRunCount(string task)
    {lock(_gate){using var db=Open();using var query=db.CreateCommand();query.CommandText="SELECT COUNT(*) FROM operation_runs WHERE task=$task";query.Parameters.AddWithValue("$task",task);return Convert.ToInt32(query.ExecuteScalar());}}
    /// <summary>详情每页最多64次尝试，单条联接查询读取全部步骤；不逐尝试重新打开数据库。</summary>
    internal IReadOnlyList<OperationRunSnapshot> OperationRuns(string task,int offset=0,int limit=64)
    {
        if(offset<0||limit is <1 or >64)throw new ArgumentOutOfRangeException(nameof(limit));
        lock(_gate)
        {
            using var db=Open();using var query=db.CreateCommand();query.CommandText="""
                SELECT r.id,r.attempt,r.generation,r.state,r.started,r.finished,r.reason,
                  s.ordinal,s.name,s.state,s.started,s.finished,s.reason,s.completed,s.total,s.evidence
                FROM (SELECT * FROM operation_runs WHERE task=$task ORDER BY attempt DESC LIMIT $limit OFFSET $offset) r
                LEFT JOIN operation_steps s ON s.run=r.id ORDER BY r.attempt DESC,s.ordinal
                """;query.Parameters.AddWithValue("$task",task);query.Parameters.AddWithValue("$limit",limit);query.Parameters.AddWithValue("$offset",offset);
            using var rows=query.ExecuteReader();var runs=new List<OperationRunSnapshot>();List<OperationStepSnapshot>? steps=null;string? current=null;
            DateTimeOffset? Time(int column)=>rows.IsDBNull(column)?null:DateTimeOffset.Parse(rows.GetString(column),CultureInfo.InvariantCulture);
            string? Text(int column)=>rows.IsDBNull(column)?null:rows.GetString(column);
            while(rows.Read())
            {
                var id=rows.GetString(0);
                if(current!=id)
                {
                    current=id;steps=[];runs.Add(new(id,task,rows.GetInt32(1),rows.GetString(2),rows.GetString(3),Time(4)!.Value,Time(5),Text(6),steps));
                }
                if(!rows.IsDBNull(7))steps!.Add(new(rows.GetInt32(7),rows.GetString(8),rows.GetString(9),Time(10),Time(11),Text(12),rows.GetInt64(13),rows.GetInt64(14),Text(15)));
            }
            return runs;
        }
    }
}
