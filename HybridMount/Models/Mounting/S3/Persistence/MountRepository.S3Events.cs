using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountRepository : IRemoteHintJournal
{
    /// <summary>只把提示身份和路径投影给公共层，协议事件与消息正文仍留在模块。</summary>
    public IReadOnlyList<RemotePathHint> PendingRemoteHints(Guid mount,string scope)=>S3EventHints(mount,scope).Where(x=>x.State=="Pending").Select(x=>new RemotePathHint(x.Id,x.Path)).ToArray();
    public IReadOnlyList<S3EventHint> S3EventHints(Guid mount,string scope)=>RemoteRows<S3EventHint>("s3_event_hints",mount,scope);
    /// <summary>消息去重、同 Key 顺序及全部挂载提示在同一事务持久化；返回后才允许队列确认。无 Receipt 或原始正文。</summary>
    internal void PersistSqsMessage(string queue,string message,IReadOnlyList<S3EventHint> hints)
    {
        if(string.IsNullOrEmpty(message)||message.Length>256)throw new IOException("SQS 消息标识无效，未确认。");
        lock(_gate)
        {
            using var db=Open();using var tx=db.BeginTransaction();
            using(var seen=db.CreateCommand())
            {seen.Transaction=tx;seen.CommandText="SELECT 1 FROM sqs_messages WHERE queue=$q AND id=$id";seen.Parameters.AddWithValue("$q",queue);seen.Parameters.AddWithValue("$id",message);if(seen.ExecuteScalar() is not null)return;}
            foreach(var hint in hints)
            {
                using var query=db.CreateCommand();query.Transaction=tx;query.CommandText="SELECT sequence FROM s3_event_watermarks WHERE mount=$m AND scope=$s AND path=$p";
                query.Parameters.AddWithValue("$m",hint.MountId.ToString("N"));query.Parameters.AddWithValue("$s",hint.Scope);query.Parameters.AddWithValue("$p",hint.Path);
                if(query.ExecuteScalar() is string current&&!S3EventParser.Newer(hint.Sequence,current))continue;
                using(var supersede=db.CreateCommand())
                {supersede.Transaction=tx;supersede.CommandText="UPDATE s3_event_hints SET json=json_set(json,'$.State','Superseded') WHERE mount=$m AND scope=$s AND path=$p AND json_extract(json,'$.State')='Pending'";
                    supersede.Parameters.AddWithValue("$m",hint.MountId.ToString("N"));supersede.Parameters.AddWithValue("$s",hint.Scope);supersede.Parameters.AddWithValue("$p",hint.Path);supersede.ExecuteNonQuery();}
                RemoteCommand(db,tx,"INSERT INTO s3_event_hints VALUES($id,$m,$s,$p,$j)",hint.MountId,hint.Scope,hint.Path,JsonSerializer.Serialize(hint),hint.Id);
                using var mark=db.CreateCommand();mark.Transaction=tx;mark.CommandText="INSERT INTO s3_event_watermarks VALUES($m,$s,$p,$sequence) ON CONFLICT(mount,scope,path) DO UPDATE SET sequence=$sequence";
                mark.Parameters.AddWithValue("$m",hint.MountId.ToString("N"));mark.Parameters.AddWithValue("$s",hint.Scope);mark.Parameters.AddWithValue("$p",hint.Path);mark.Parameters.AddWithValue("$sequence",hint.Sequence);mark.ExecuteNonQuery();
            }
            using(var messageRow=db.CreateCommand())
            {messageRow.Transaction=tx;messageRow.CommandText="INSERT INTO sqs_messages VALUES($q,$id,$time);DELETE FROM sqs_messages WHERE queue=$q AND rowid NOT IN (SELECT rowid FROM sqs_messages WHERE queue=$q ORDER BY rowid DESC LIMIT 10000)";
                messageRow.Parameters.AddWithValue("$q",queue);messageRow.Parameters.AddWithValue("$id",message);messageRow.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToString("O"));messageRow.ExecuteNonQuery();}
            tx.Commit();
        }
    }
    internal void MarkS3HintCore(Microsoft.Data.Sqlite.SqliteConnection db,Microsoft.Data.Sqlite.SqliteTransaction tx,Guid mount,string scope,string id)
    {
        var prior=S3EventHints(mount,scope).SingleOrDefault(x=>x.Id==id&&x.State=="Pending");
        if(prior is null)throw new IOException("S3 事件已由更新提示替代，本次观测未提交。");
        RemoteCommand(db,tx,"UPDATE s3_event_hints SET json=$j WHERE id=$id",mount,scope,prior.Path,JsonSerializer.Serialize(prior with{State="Verified",Error=null}),id);
    }
}
