using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountRepository
{
    /// <summary>相关持久事件整体比较；包含父子路径的屏障，迟到取消或新观测使旧授权失效。</summary>
    internal string ConflictPendingJson(Guid mount,string path)=>JsonSerializer.Serialize(AllRemotePending(mount)
        .Where(x=>x.State is not("Completed" or "Superseded")&&ConflictRelated(x.Path,path)).OrderBy(x=>x.Id,StringComparer.Ordinal));
    /// <summary>S3等协议尚未转为已核对事件的提示不能被直接取舍吞掉。</summary>
    internal bool HasConflictHints(Guid mount,string path)
    {
        var predicates=_protocolStorage.Select(x=>x.PendingPredicate).Where(x=>x is not null).ToArray();if(predicates.Length==0)return false;
        lock(_gate){using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT 1 WHERE "+string.Join(" OR ",predicates.Select(x=>"("+x+")"));cmd.Parameters.AddWithValue("$m",mount.ToString("N"));cmd.Parameters.AddWithValue("$p",path);return cmd.ExecuteScalar() is not null;}
    }
    internal static bool ConflictRelated(string left,string right)=>left.Equals(right,StringComparison.OrdinalIgnoreCase)||left.StartsWith(right+"/",StringComparison.OrdinalIgnoreCase)||right.StartsWith(left+"/",StringComparison.OrdinalIgnoreCase);
    /// <summary>仓库锁内同时校验索引、原任务和远端事件；无索引的新文件不会借用其他身份。</summary>
    private bool ConflictCurrent(LocalUpload? upload,MountEntry? entry,Guid mount,string path,string pending)
        =>(upload is null?!Uploads(mount).Any(x=>!x.IsCopy&&x.SourcePath==path):Upload(upload.Id)==upload)&&
          Entries(mount).FirstOrDefault(x=>entry is null?x.SourcePath==path:x.Id==entry.Id)==entry&&ConflictPendingJson(mount,path)==pending;
    /// <summary>先保存冲突处理意图，再读取快照；中断后扫描和发送仍看到持久屏障。</summary>
    internal LocalUpload BeginConflict(LocalUpload? captured,MountEntry? entry,LocalUpload intent,string pending)
    {
        lock(_gate)
        {
            if(!ConflictCurrent(captured,entry,intent.MountId,intent.SourcePath,pending))throw new SourceConflictException("比较后任务、索引或远端事件变化，请重新核对。");
            using var db=Open();return WriteUpload(db,intent);
        }
    }
    /// <summary>明确取舍仅退役用户确认过的精确事件；不消费后来出现的事件或其他路径提示。</summary>
    private static void FinishConflictEvents(Microsoft.Data.Sqlite.SqliteConnection db,Microsoft.Data.Sqlite.SqliteTransaction tx,LocalUpload intent)
    {
        foreach(var item in JsonSerializer.Deserialize<RemotePendingChange[]>(intent.Resolution!.PendingJson)??[])
            SavePendingCore(db,tx,item with{State="Superseded",Error="已按用户确认的冲突取舍处理；后续版本仍独立检测。"});
    }
    /// <summary>冻结快照和最新授权版本在同一事务交给原上传器；不提前改变索引版本或重复发送PUT。</summary>
    internal LocalUpload PublishConflictLocal(LocalUpload captured,LocalUpload prepared)
    {
        lock(_gate)
        {
            if(Upload(captured.Id)!=captured||ConflictPendingJson(captured.MountId,captured.SourcePath)!=captured.Resolution!.PendingJson)throw new SourceConflictException("冲突准备期间记录变化，未提交上传。");
            using var db=Open();using var tx=db.BeginTransaction();FinishConflictEvents(db,tx,captured);var result=WriteUpload(db,prepared,tx);tx.Commit();return result;
        }
    }
    /// <summary>已验证真实本地正文后原子提交索引、基线、上传终态及精确事件；调用方仍持保护引用。</summary>
    internal LocalUpload CommitConflictRemote(LocalUpload captured,LocalFileStamp finalStamp)
    {
        lock(_gate)
        {
            var resolution=captured.Resolution??throw new IOException("冲突恢复记录缺失。");
            if(Upload(captured.Id)!=captured||ConflictPendingJson(captured.MountId,captured.SourcePath)!=resolution.PendingJson)throw new SourceConflictException("替换期间任务或远端事件变化，备份和恢复屏障已保留。");
            using var db=Open();using var tx=db.BeginTransaction();
            using(var cmd=db.CreateCommand())
            {
                cmd.Transaction=tx;cmd.CommandText="INSERT INTO entries VALUES($m,$id,$s,$l,$v,0,0,0,1) ON CONFLICT(mount,id) DO UPDATE SET source=$s,local=$l,version=$v,directory=0,deleted=0,protected=0,materialized=1";
                cmd.Parameters.AddWithValue("$m",captured.MountId.ToString("N"));cmd.Parameters.AddWithValue("$id",resolution.Entry.Id);cmd.Parameters.AddWithValue("$s",captured.SourcePath);cmd.Parameters.AddWithValue("$l",captured.LocalPath);cmd.Parameters.AddWithValue("$v",resolution.Remote.Version);cmd.ExecuteNonQuery();
                cmd.CommandText="INSERT INTO remote_presence VALUES($m,$id,1) ON CONFLICT(mount,id) DO UPDATE SET presence=1";cmd.ExecuteNonQuery();
            }
            using(var cmd=db.CreateCommand())
            {
                cmd.Transaction=tx;cmd.CommandText="INSERT INTO content_baselines VALUES($m,$p,$v,$s) ON CONFLICT(mount,path,version) DO NOTHING";
                cmd.Parameters.AddWithValue("$m",captured.MountId.ToString("N"));cmd.Parameters.AddWithValue("$p",captured.SourcePath);cmd.Parameters.AddWithValue("$v",resolution.Remote.Version);cmd.Parameters.AddWithValue("$s",resolution.RemoteSha256!);cmd.ExecuteNonQuery();
            }
            FinishConflictEvents(db,tx,captured);
            var result=WriteUpload(db,captured with{State="Completed",EntryId=resolution.Entry.Id,ExpectedVersion=resolution.Remote.Version,BaselineSha256=resolution.RemoteSha256,
                ObservedStamp=finalStamp.Key,SnapshotPath=null,SnapshotSha256=null,SnapshotStamp=null,SnapshotIdentity=null,SnapshotGeneration=0,SnapshotAttemptId=null,
                Receipt=null,Resolution=null,ConflictRecoveryPath=resolution.PreviousRecoveryPath??resolution.BackupPath,CancelRequested=false,ExplicitRequest=false,Error=null,Bytes=resolution.Remote.Size,Total=resolution.Remote.Size},tx);
            tx.Commit();return result;
        }
    }
}
