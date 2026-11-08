using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountRepository
{
    /// <summary>事务切换已确认的云端路径及版本；只修改捕获身份，不让新建同名文件继承旧基线。</summary>
    internal MountEntry RebindExcludedRename(ExcludedRenameIntent intent)
    {
        lock(_gate)
        {
            var receipt=intent.MoveReceipt??throw new IOException("缺少改名目标回执。");
            if(!intent.RemoteMoved&&!intent.CancelRequested||receipt.Path!=intent.DestinationPath||receipt.IsDirectory)throw new IOException("改名目标尚未确认。");
            var upload=intent.UploadId is {} uploadId?Upload(uploadId):null;
            var id=intent.EntryId??upload?.EntryId??intent.UploadId??intent.Id;
            var ids=new[]{intent.EntryId,upload?.EntryId,intent.UploadId,id}.Where(x=>x is not null).ToHashSet(StringComparer.Ordinal);
            var captured=Entries(intent.MountId).Where(x=>ids.Contains(x.Id)).ToArray();
            if(captured.Any(x=>x.SourcePath!=intent.SourcePath&&x.SourcePath!=receipt.Path||x.Version!=intent.ExpectedVersion&&x.Version!=intent.OriginalVersion&&x.Version!=receipt.Version))throw new SourceConflictException("改名关联索引已变化，未覆盖其他对象。");
            var entry=new MountEntry(id,receipt.Path,intent.LocalPath,receipt.Version,false,Protected:true,Materialized:true);
            using var db=Open();using var tx=db.BeginTransaction();
            foreach(var old in captured)
            {
                using var remove=db.CreateCommand();remove.Transaction=tx;remove.CommandText="DELETE FROM remote_presence WHERE mount=$m AND id=$id;DELETE FROM entries WHERE mount=$m AND id=$id";remove.Parameters.AddWithValue("$m",intent.MountId.ToString("N"));remove.Parameters.AddWithValue("$id",old.Id);remove.ExecuteNonQuery();
            }
            using(var cmd=db.CreateCommand())
            {
                cmd.Transaction=tx;cmd.CommandText="INSERT INTO entries VALUES($m,$id,$s,$l,$v,0,0,1,1)";cmd.Parameters.AddWithValue("$m",intent.MountId.ToString("N"));cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$s",receipt.Path);cmd.Parameters.AddWithValue("$l",intent.LocalPath);cmd.Parameters.AddWithValue("$v",receipt.Version);cmd.ExecuteNonQuery();
            }
            tx.Commit();return entry;
        }
    }
    /// <summary>完成后保留新路径及远端基线；普通本地排除文件的后续变化不会自动上载。</summary>
    internal void RetireExcludedRename(ExcludedRenameIntent intent)
    {
        lock(_gate)
        {
            if(!intent.LocalDetached)throw new IOException("本地内容尚未独立保存。");
            if(intent.MoveReceipt is not null)RebindExcludedRename(intent);
            if(intent.UploadId is {} id&&Upload(id) is {} upload&&!UploadRules.InFlight(upload)&&upload.ObservedStamp.Split(':')[0]==intent.FileId&&upload.State!="Completed")
                SaveUpload(upload with{State="Canceled",CancelRequested=true,Error="已改名并排除，原路径上传已收尾。"});
        }
    }
}
