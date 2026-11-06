using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountRepository
{
    internal LocalUpload CreateUploadCopy(string id,string destination,Action<LocalUpload> validate)
    {
        lock(_gate)
        {
            var original=Upload(id)??throw new IOException("上传记录不存在。");
            var copy=UploadCopySnapshot.Create(original,destination,validate);
            using var db=Open();return WriteUpload(db,copy);
        }
    }
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
