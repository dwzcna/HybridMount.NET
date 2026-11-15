using System.Text.Json;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>接纳及适用步骤同事务落盘。认证证据取自真实请求的时间，不在接纳后伪造认证耗时。</summary>
    internal void AcceptApplicationTask(MountOperationSnapshot task,IReadOnlyList<string> steps,DateTimeOffset? authenticatedFrom=null,DateTimeOffset? authenticatedUntil=null)
    {
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;
            command.CommandText="INSERT INTO operations VALUES($id,$json)";command.Parameters.AddWithValue("$id",task.Id);command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(task));command.ExecuteNonQuery();
            var run=Guid.NewGuid().ToString("N");var now=DateTimeOffset.UtcNow;
            command.Parameters.Clear();command.CommandText="INSERT INTO operation_runs VALUES($run,$task,1,$task,'Running',$started,NULL,NULL)";
            command.Parameters.AddWithValue("$run",run);command.Parameters.AddWithValue("$task",task.Id);command.Parameters.AddWithValue("$started",(authenticatedFrom??now).ToString("O"));command.ExecuteNonQuery();
            for(var ordinal=0;ordinal<steps.Count;ordinal++)
            {
                var authenticated=steps[ordinal]=="OperationStageAuthenticate"&&authenticatedFrom is not null&&authenticatedUntil is not null;
                command.Parameters.Clear();command.CommandText="INSERT INTO operation_steps(run,ordinal,name,state,started,finished) VALUES($run,$ordinal,$name,$state,$started,$finished)";
                command.Parameters.AddWithValue("$run",run);command.Parameters.AddWithValue("$ordinal",ordinal);command.Parameters.AddWithValue("$name",steps[ordinal]);command.Parameters.AddWithValue("$state",authenticated?"Completed":"Pending");
                command.Parameters.AddWithValue("$started",authenticated?(object)authenticatedFrom!.Value.ToString("O"):DBNull.Value);command.Parameters.AddWithValue("$finished",authenticated?(object)authenticatedUntil!.Value.ToString("O"):DBNull.Value);command.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
