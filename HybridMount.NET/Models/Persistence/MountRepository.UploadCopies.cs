using HybridMount.Models.Mounting;

namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>在同一仓库锁内读取原记录、验证并保存副本引用，避免并发快照清理越过引用登记。</summary>
    internal LocalUpload CreateUploadCopy(string id,string destination,Action<LocalUpload> validate)
    {
        lock(_gate)
        {
            var original=Upload(id)??throw new IOException("上传记录不存在。");
            var copy=UploadCopySnapshot.Create(original,destination,validate);
            using var db=Open();return WriteUpload(db,copy);
        }
    }
    /// <summary>仓库锁内确认无上传引用再执行受控删除；forget仅用于已停止本挂载任务的明确移除收尾。</summary>
    internal void DeleteUnreferencedUploadSnapshot(string path,Action delete,bool forget=false)
    {
        lock(_gate)
        {
            if(!forget)
            {
                using var db=Open();using var query=db.CreateCommand();query.CommandText="SELECT 1 FROM uploads WHERE json_extract(json,'$.SnapshotPath')=$path COLLATE NOCASE LIMIT 1";query.Parameters.AddWithValue("$path",path);
                if(query.ExecuteScalar() is not null)return;
            }
            delete();
        }
    }
}
