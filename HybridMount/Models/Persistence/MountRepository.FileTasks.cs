using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>事务完成后的内存唤醒；订阅者只能入队，不查询数据库或同步调用另一进程。</summary>
    internal event Action<Guid>? TaskJournalChanged;
    private void WakeTaskResults(Guid mount){if(TaskJournalChanged is {} handlers)foreach(Action<Guid> handler in handlers.GetInvocationList())try{handler(mount);}catch(Exception){}}

    /// <summary>持久接纳后才能发送回执；重发必须与原身份和正文一致，序号断档要求补扫。</summary>
    internal (bool Created,bool Gap) AcceptTaskIntent(NativeTaskIntent intent)
    {
        intent.Validate();
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT json FROM task_intents WHERE id=$id";command.Parameters.AddWithValue("$id",intent.TaskId);
            if(command.ExecuteScalar() is string prior)
            {
                if((JsonSerializer.Deserialize<NativeTaskIntent>(prior)! with{RescanRequired=false})!=(intent with{RescanRequired=false}))throw new InvalidDataException("重复任务意图的内容不同。");
                return(false,false);
            }
            command.CommandText="SELECT sequence FROM task_sequences WHERE mount=$m AND session=$s";
            command.Parameters.AddWithValue("$m",intent.MountId.ToString("N"));command.Parameters.AddWithValue("$s",intent.SessionId);
            var last=Convert.ToInt64(command.ExecuteScalar()??0L);var gap=intent.Sequence>last+1;
            command.CommandText="SELECT id FROM task_intents WHERE mount=$m AND session=$s AND sequence=$n";
            command.Parameters.AddWithValue("$n",intent.Sequence);
            if(command.ExecuteScalar() is string duplicate&&duplicate!=intent.TaskId)throw new InvalidDataException("同一事件序号使用了不同任务ID。");
            command.CommandText="INSERT INTO task_intents VALUES($id,$m,$s,$n,$p,$j,'Accepted')";
            command.Parameters.AddWithValue("$p",intent.Path);command.Parameters.AddWithValue("$j",JsonSerializer.Serialize(intent with{RescanRequired=gap}));command.ExecuteNonQuery();
            // 只推进连续接纳边界；高序号先到不能掩盖缺失意图，停止必须等完整交付。
            command.CommandText="SELECT sequence FROM task_intents WHERE mount=$m AND session=$s AND sequence>$last ORDER BY sequence";command.Parameters.AddWithValue("$last",last);
            var through=last;using(var rows=command.ExecuteReader())while(rows.Read()){var next=rows.GetInt64(0);if(next!=through+1)break;through=next;}
            command.Parameters["$n"].Value=through;
            command.CommandText="INSERT INTO task_sequences VALUES($m,$s,$n) ON CONFLICT(mount,session) DO UPDATE SET sequence=MAX(sequence,$n)";command.ExecuteNonQuery();
            tx.Commit();return(true,gap);
        }
    }
    /// <summary>停止监听屏障只核对已持久接纳序号；不把任务最终完成混为交付完成。</summary>
    internal long AcceptedTaskSequence(Guid mount,string session)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT sequence FROM task_sequences WHERE mount=$m AND session=$s";
            command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$s",session);return Convert.ToInt64(command.ExecuteScalar()??0L);
        }
    }
    internal IReadOnlyList<NativeTaskIntent> UnroutedTaskIntents(Guid mount)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT json FROM task_intents WHERE mount=$m AND state='Accepted' ORDER BY rowid LIMIT 256";
            command.Parameters.AddWithValue("$m",mount.ToString("N"));using var rows=command.ExecuteReader();var result=new List<NativeTaskIntent>();
            while(rows.Read())result.Add(JsonSerializer.Deserialize<NativeTaskIntent>(rows.GetString(0))!);return result;
        }
    }
    /// <summary>关联到持久主任务；没有实际工作时也返回明确无须处理，不能静默遗失意图。</summary>
    internal void BindTaskIntent(NativeTaskIntent intent,IReadOnlyCollection<string> tasks)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.Parameters.AddWithValue("$id",intent.TaskId);command.CommandText="UPDATE task_intents SET state='Routed' WHERE id=$id AND state='Accepted'";
            if(command.ExecuteNonQuery()==0)return;
            foreach(var task in tasks.Distinct(StringComparer.Ordinal))
            {
                command.CommandText="INSERT OR IGNORE INTO intent_tasks VALUES($id,$task)";command.Parameters.AddWithValue("$task",task);command.ExecuteNonQuery();command.Parameters.RemoveAt("$task");
            }
            QueueIntentResultCore(db,tx,intent,tasks.Count==0?"NoOp":"Queued",tasks.Count==0,"dispatch");
            tx.Commit();WakeTaskResults(intent.MountId);
        }
    }
    /// <summary>业务处理失败保留可重试意图；协议错误由上层单独封闭不可信通道。</summary>
    internal void DeferTaskIntent(NativeTaskIntent intent,string stage)
    {
        lock(_gate){using var db=Open();using var tx=db.BeginTransaction();QueueIntentResultCore(db,tx,intent,"Deferred",false,stage);tx.Commit();WakeTaskResults(intent.MountId);}
    }
    internal void FinishInterruptedTaskIntent(NativeTaskIntent intent)
    {
        lock(_gate){using var db=Open();using var tx=db.BeginTransaction();QueueIntentResultCore(db,tx,intent,"Interrupted",true,"connection-retired");tx.Commit();WakeTaskResults(intent.MountId);}
    }
    internal void RecoverFileTaskAttempts()
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="UPDATE task_attempts SET state='OutcomeUnknown' WHERE state='Running';UPDATE task_steps SET json=json_set(json,'$.State','OutcomeUnknown') WHERE json_extract(json,'$.State')='Running';UPDATE file_tasks SET json=json_set(json,'$.State','Interrupted') WHERE json_extract(json,'$.State')='Running'";
            command.ExecuteNonQuery();tx.Commit();
        }
    }
    internal FileTaskRecord? ReadFileTask(string id)
    {
        lock(_gate){using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT json FROM file_tasks WHERE id=$id";command.Parameters.AddWithValue("$id",id);return command.ExecuteScalar() is string json?JsonSerializer.Deserialize<FileTaskRecord>(json):null;}
    }
    /// <summary>新代次持久化后才可执行；旧代次回执只能写自己的步骤，不能覆盖最新主任务。</summary>
    internal bool PrepareFileTask(FileTaskRecord task,IReadOnlyList<string> steps)
    {
        if(steps.Count is <1 or >32)throw new ArgumentOutOfRangeException(nameof(steps));
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT json FROM file_tasks WHERE id=$id";command.Parameters.AddWithValue("$id",task.Id);
            if(command.ExecuteScalar() is string prior)
            {
                var old=JsonSerializer.Deserialize<FileTaskRecord>(prior)!;
                if(old.MountId!=task.MountId||old.Path!=task.Path||old.Generation>task.Generation)return false;
            }
            command.CommandText="INSERT INTO file_tasks VALUES($id,$m,$p,$j) ON CONFLICT(id) DO UPDATE SET json=$j,path=$p";
            command.Parameters.AddWithValue("$m",task.MountId.ToString("N"));command.Parameters.AddWithValue("$p",task.Path);command.Parameters.AddWithValue("$j",JsonSerializer.Serialize(task));command.ExecuteNonQuery();
            command.Parameters.Clear();command.CommandText="DELETE FROM task_dependencies WHERE task=$task";command.Parameters.AddWithValue("$task",task.Id);command.ExecuteNonQuery();
            foreach(var dependency in task.Dependencies??[])
            {
                if(dependency==task.Id)throw new InvalidDataException("任务不能依赖自身。");
                command.CommandText="INSERT INTO task_dependencies VALUES($task,$required)";command.Parameters.AddWithValue("$required",dependency);command.ExecuteNonQuery();command.Parameters.RemoveAt("$required");
            }
            for(var index=0;index<steps.Count;index++)
            {
                var step=new FileTaskStep(StepId(task,index),task.Id,index,steps[index],"Ready",index==0?null:StepId(task,index-1));
                command.Parameters.Clear();command.CommandText="INSERT OR IGNORE INTO task_steps VALUES($id,$task,$n,$j)";
                command.Parameters.AddWithValue("$id",step.Id);command.Parameters.AddWithValue("$task",task.Id);command.Parameters.AddWithValue("$n",index);command.Parameters.AddWithValue("$j",JsonSerializer.Serialize(step));command.ExecuteNonQuery();
            }
            tx.Commit();return true;
        }
    }
    internal static string StepId(FileTaskRecord task,int index)=>task.Id+":"+task.Generation+":"+index;
    internal IReadOnlyList<FileTaskStep> TaskSteps(string id)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT s.json FROM task_steps s JOIN file_tasks t ON t.id=s.task WHERE s.task=$id OR json_extract(t.json,'$.ParentId')=$id ORDER BY s.rowid";command.Parameters.AddWithValue("$id",id);
            using var rows=command.ExecuteReader();var result=new List<FileTaskStep>();while(rows.Read())result.Add(JsonSerializer.Deserialize<FileTaskStep>(rows.GetString(0))!);return result;
        }
    }
    /// <summary>执行开始前核对代次与已核验依赖；失败、取消和未知结果均不放行。</summary>
    internal string BeginTaskStep(FileTaskRecord task,int ordinal)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT json FROM file_tasks WHERE id=$id";command.Parameters.AddWithValue("$id",task.Id);
            var current=JsonSerializer.Deserialize<FileTaskRecord>((string?)command.ExecuteScalar()??throw new IOException("任务不存在。"))!;
            if(current.Generation!=task.Generation||current.SessionId!=task.SessionId)throw new OperationCanceledException("任务代次已变化。");
            command.CommandText="SELECT 1 FROM task_dependencies d LEFT JOIN file_tasks t ON t.id=d.required WHERE d.task=$id AND (t.id IS NULL OR t.mount<>(SELECT mount FROM file_tasks WHERE id=$id) OR json_extract(t.json,'$.State')<>'Completed') LIMIT 1";
            if(command.ExecuteScalar() is not null)throw new IOException("父任务尚未通过实际结果核验。");
            command.CommandText="SELECT json FROM task_steps WHERE id=$id";command.Parameters["$id"].Value=StepId(task,ordinal);
            var step=JsonSerializer.Deserialize<FileTaskStep>((string?)command.ExecuteScalar()??throw new IOException("任务步骤不存在。"))!;
            if(step.DependsOn is {} dependency)
            {
                command.Parameters["$id"].Value=dependency;
                if(command.ExecuteScalar() is not string json||JsonSerializer.Deserialize<FileTaskStep>(json)?.State!="Verified")throw new IOException("任务依赖尚未核验。");
            }
            var attempt=Guid.NewGuid().ToString("N");step=step with{State="Running",AttemptId=attempt,Error=null,HResult=null};
            WriteTaskStepCore(db,tx,step);
            command.Parameters.Clear();command.CommandText="INSERT INTO task_attempts(id,task,step,state,started) VALUES($id,$task,$step,'Running',$time)";
            command.Parameters.AddWithValue("$id",attempt);command.Parameters.AddWithValue("$task",task.Id);command.Parameters.AddWithValue("$step",step.Id);command.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToString("O"));command.ExecuteNonQuery();
            tx.Commit();return attempt;
        }
    }
    /// <summary>精确尝试比较后保存执行回执；迟到旧尝试不解除新步骤的屏障。</summary>
    internal void EndTaskStep(FileTaskRecord task,int ordinal,string attempt,TaskVerification result)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT json FROM task_steps WHERE id=$id";command.Parameters.AddWithValue("$id",StepId(task,ordinal));
            if(command.ExecuteScalar() is string json&&JsonSerializer.Deserialize<FileTaskStep>(json) is {} step&&step.AttemptId==attempt)
                WriteTaskStepCore(db,tx,step with{State=result.Verified?"Verified":result.State,Error=result.Stage,HResult=result.HResult});
            command.CommandText="UPDATE task_attempts SET state=$state,finished=$time WHERE id=$id AND state='Running'";
            command.Parameters["$id"].Value=attempt;command.Parameters.AddWithValue("$state",result.State);command.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToString("O"));command.ExecuteNonQuery();
            tx.Commit();
        }
    }
    private static void WriteTaskStepCore(SqliteConnection db,SqliteTransaction tx,FileTaskStep step)
    {
        using var command=db.CreateCommand();command.Transaction=tx;command.CommandText="UPDATE task_steps SET json=$j WHERE id=$id";
        command.Parameters.AddWithValue("$id",step.Id);command.Parameters.AddWithValue("$j",JsonSerializer.Serialize(step));command.ExecuteNonQuery();
    }
    /// <summary>当前业务状态与待发结果同事务保存；最终结果仍需原生确认，不能按发送成功清除。</summary>
    internal void SetFileTaskState(FileTaskRecord task,string state,string stage)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="SELECT json FROM file_tasks WHERE id=$id";command.Parameters.AddWithValue("$id",task.Id);
            if(command.ExecuteScalar() is not string json)return;var current=JsonSerializer.Deserialize<FileTaskRecord>(json)!;
            if(current.Generation!=task.Generation||current.SessionId!=task.SessionId)return;
            command.CommandText="UPDATE file_tasks SET json=$j WHERE id=$id";command.Parameters.AddWithValue("$j",JsonSerializer.Serialize(current with{State=state,UpdatedUtc=DateTimeOffset.UtcNow}));command.ExecuteNonQuery();
            command.CommandText="SELECT i.json FROM task_intents i JOIN intent_tasks b ON b.intent=i.id WHERE b.task=$id AND i.state<>'Finished'";
            var intents=new List<NativeTaskIntent>();using(var rows=command.ExecuteReader())while(rows.Read())intents.Add(JsonSerializer.Deserialize<NativeTaskIntent>(rows.GetString(0))!);
            foreach(var intent in intents)
            {
                command.Parameters.Clear();command.CommandText="SELECT t.json FROM file_tasks t JOIN intent_tasks b ON b.task=t.id WHERE b.intent=$id";command.Parameters.AddWithValue("$id",intent.TaskId);
                var states=new List<string>();using(var rows=command.ExecuteReader())while(rows.Read())states.Add(JsonSerializer.Deserialize<FileTaskRecord>(rows.GetString(0))!.State);
                command.CommandText="SELECT count(*) FROM intent_tasks WHERE intent=$id";var expected=Convert.ToInt32(command.ExecuteScalar());
                var final=expected==states.Count&&states.All(TaskStateTerminal);
                var outcome=final?(states.All(x=>x=="Completed")?"Completed":states.First(x=>x!="Completed")):state;
                QueueIntentResultCore(db,tx,intent,outcome,final,stage,task.Id);
            }
            tx.Commit();WakeTaskResults(task.MountId);
        }
    }
    internal static bool TaskStateTerminal(string state)=>state is "Completed" or "Canceled" or "Superseded" or "Failed" or "Conflict";
    private static void QueueIntentResultCore(SqliteConnection db,SqliteTransaction tx,NativeTaskIntent intent,string outcome,bool final,string stage,string? workflow=null)
    {
        // 同一未确认意图仅保留最新状态，合并进度；终态在同一事务内封闭意图。
        using var command=db.CreateCommand();command.Transaction=tx;
        command.CommandText="DELETE FROM task_outbox WHERE json_extract(json,'$.TaskId')=$intent";command.Parameters.AddWithValue("$intent",intent.TaskId);command.ExecuteNonQuery();
        var result=new NativeTaskResult(Guid.NewGuid().ToString("N"),intent.TaskId,intent.MountId,intent.SessionId,stage,outcome,final,workflow);
        command.CommandText="INSERT INTO task_outbox VALUES($id,$m,$j)";command.Parameters.AddWithValue("$id",result.ResultId);command.Parameters.AddWithValue("$m",intent.MountId.ToString("N"));command.Parameters.AddWithValue("$j",JsonSerializer.Serialize(result));command.ExecuteNonQuery();
        if(final){command.CommandText="UPDATE task_intents SET state='Finished' WHERE id=$intent";command.ExecuteNonQuery();}
    }
    internal IReadOnlyList<NativeTaskResult> PendingTaskResults(Guid mount)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT json FROM task_outbox WHERE mount=$m ORDER BY rowid LIMIT 256";command.Parameters.AddWithValue("$m",mount.ToString("N"));
            using var rows=command.ExecuteReader();var result=new List<NativeTaskResult>();while(rows.Read())result.Add(JsonSerializer.Deserialize<NativeTaskResult>(rows.GetString(0))!);return result;
        }
    }
    internal void AcknowledgeTaskResult(Guid mount,string resultId)
    {
        lock(_gate){using var db=Open();using var command=db.CreateCommand();command.CommandText="DELETE FROM task_outbox WHERE id=$id AND mount=$m";command.Parameters.AddWithValue("$id",resultId);command.Parameters.AddWithValue("$m",mount.ToString("N"));command.ExecuteNonQuery();}
    }
}
