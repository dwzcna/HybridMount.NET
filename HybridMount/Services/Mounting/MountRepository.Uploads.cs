using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;

namespace HybridMount.Services.Mounting;

/// <summary>上传日志与内容基线；锁内使用新连接，避免多个监控事件覆盖在途任务的阶段。</summary>
public sealed partial class MountRepository
{
    public IReadOnlyList<LocalUpload> Uploads(Guid? mount=null)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();
            command.CommandText=mount is null?"SELECT json FROM uploads ORDER BY rowid DESC":"SELECT json FROM uploads WHERE mount=$m ORDER BY rowid DESC";
            if(mount is not null)command.Parameters.AddWithValue("$m",mount.Value.ToString("N"));
            using var reader=command.ExecuteReader();var rows=new List<LocalUpload>();
            while(reader.Read())rows.Add(JsonSerializer.Deserialize<LocalUpload>(reader.GetString(0))??throw new IOException("上传记录为空。"));return rows;
        }
    }
    public LocalUpload? Upload(string id)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT json FROM uploads WHERE id=$id";command.Parameters.AddWithValue("$id",id);
            return command.ExecuteScalar() is string json?JsonSerializer.Deserialize<LocalUpload>(json):null;
        }
    }
    private static void WriteUpload(SqliteConnection db,LocalUpload upload,SqliteTransaction? transaction=null)
    {
        using var command=db.CreateCommand();command.Transaction=transaction;
        command.CommandText="INSERT INTO uploads VALUES($id,$m,$p,$j) ON CONFLICT(id) DO UPDATE SET json=$j,path=$p";
        command.Parameters.AddWithValue("$id",upload.Id);command.Parameters.AddWithValue("$m",upload.MountId.ToString("N"));command.Parameters.AddWithValue("$p",upload.LocalPath);command.Parameters.AddWithValue("$j",JsonSerializer.Serialize(upload));command.ExecuteNonQuery();
    }
    public void SaveUpload(LocalUpload upload){lock(_gate){using var db=Open();WriteUpload(db,upload);}}
    /// <summary>在同一锁内读取并变换最新状态；返回空值表示任务已被移除。</summary>
    public LocalUpload? UpdateUpload(string id,Func<LocalUpload,LocalUpload> update)
    {
        lock(_gate)
        {
            var current=Upload(id);if(current is null)return null;
            var next=update(current);using var db=Open();using var transaction=db.BeginTransaction();
            if(current.State=="Completed"&&(next.State!="Completed"||next.Generation!=current.Generation))
            {
                // 新一代本地意图不能覆盖上一代已经成功的事实；历史 ID 保持合法 GUID，继续支持 IPC 查询。
                var archived=current.ToSnapshot() with{Id=Guid.NewGuid().ToString("N")};using var history=db.CreateCommand();history.Transaction=transaction;
                history.CommandText="INSERT INTO operations VALUES($id,$j)";history.Parameters.AddWithValue("$id",archived.Id);history.Parameters.AddWithValue("$j",JsonSerializer.Serialize(archived));history.ExecuteNonQuery();
            }
            WriteUpload(db,next,transaction);transaction.Commit();return next;
        }
    }
    /// <summary>同一不透明版本只能绑定第一份已验证摘要；后续差异不能悄悄覆盖基线。</summary>
    public void SaveBaseline(Guid mount,SourceContentBaseline baseline)
    {
        if(baseline.Sha256.Length!=64||!baseline.Sha256.All(Uri.IsHexDigit))throw new IOException("内容基线摘要无效。");
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();
            command.CommandText="INSERT INTO content_baselines VALUES($m,$p,$v,$s) ON CONFLICT(mount,path,version) DO NOTHING";
            command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$p",baseline.Path);command.Parameters.AddWithValue("$v",baseline.Version);command.Parameters.AddWithValue("$s",baseline.Sha256);command.ExecuteNonQuery();
        }
    }
    public string? Baseline(Guid mount,string path,string version)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT sha256 FROM content_baselines WHERE mount=$m AND path=$p AND version=$v";
            command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$p",path);command.Parameters.AddWithValue("$v",version);return command.ExecuteScalar() as string;
        }
    }
    /// <summary>明确提交远端版本、映射和上传日志；只有已验证的当前代次才解除本地保护。</summary>
    public LocalUpload CommitUploadedEntry(LocalUpload prepared,MountEntry entry,SourceEntry receipt,bool unchanged,bool missing=false)
    {
        lock(_gate)
        {
            var current=Upload(prepared.Id)??throw new IOException("上传记录已经移除。");
            var clean=unchanged&&current.Generation==prepared.SnapshotGeneration;
            var completed=clean||missing||current.IsCopy;
            var next=current with{EntryId=entry.Id,ExpectedVersion=receipt.Version,BaselineSha256=prepared.SnapshotSha256,
                State=completed?"Completed":current.CancelRequested?"Canceled":"Queued",Receipt=null,SnapshotPath=null,SnapshotSha256=null,
                SnapshotStamp=null,SnapshotIdentity=null,SnapshotGeneration=0,Bytes=completed?prepared.Total:0,Total=completed?prepared.Total:0,
                Error=missing?"远端已经上传；本地项已移除，未传播删除。":clean||current.IsCopy?null:"上传期间本地再次变化，保留新一代更改。",ExplicitRequest=false,LastCheckedUtc=null};
            using var db=Open();using var transaction=db.BeginTransaction();
            if(!current.IsCopy)
            {
                if(prepared.EntryId is not null&&prepared.EntryId!=entry.Id)
                {
                    using var previous=db.CreateCommand();previous.Transaction=transaction;previous.CommandText="UPDATE entries SET deleted=1,protected=1 WHERE mount=$m AND id=$id";
                    previous.Parameters.AddWithValue("$m",prepared.MountId.ToString("N"));previous.Parameters.AddWithValue("$id",prepared.EntryId);previous.ExecuteNonQuery();
                }
                using var command=db.CreateCommand();command.Transaction=transaction;
                command.CommandText="INSERT INTO entries VALUES($m,$id,$s,$l,$v,$d,$x,$p,1) ON CONFLICT(mount,id) DO UPDATE SET source=$s,local=$l,version=$v,directory=$d,deleted=$x,protected=$p,materialized=1";
                command.Parameters.AddWithValue("$m",prepared.MountId.ToString("N"));command.Parameters.AddWithValue("$id",entry.Id);command.Parameters.AddWithValue("$s",entry.SourcePath);command.Parameters.AddWithValue("$l",entry.LocalPath);
                command.Parameters.AddWithValue("$v",receipt.Version);command.Parameters.AddWithValue("$d",entry.IsDirectory);command.Parameters.AddWithValue("$x",missing);command.Parameters.AddWithValue("$p",!clean||missing);command.ExecuteNonQuery();
            }
            if(!current.IsCopy)
            {
                using var presence=db.CreateCommand();presence.Transaction=transaction;
                presence.CommandText="INSERT INTO remote_presence VALUES($m,$id,$p) ON CONFLICT(mount,id) DO UPDATE SET presence=$p";
                presence.Parameters.AddWithValue("$m",prepared.MountId.ToString("N"));presence.Parameters.AddWithValue("$id",entry.Id);presence.Parameters.AddWithValue("$p",(int)RemotePresence.Present);presence.ExecuteNonQuery();
            }
            WriteUpload(db,next,transaction);
            if(prepared.SnapshotSha256 is not null)
            {
                using var baseline=db.CreateCommand();baseline.Transaction=transaction;baseline.CommandText="INSERT INTO content_baselines VALUES($m,$p,$v,$s) ON CONFLICT(mount,path,version) DO NOTHING";
                baseline.Parameters.AddWithValue("$m",prepared.MountId.ToString("N"));baseline.Parameters.AddWithValue("$p",receipt.Path);baseline.Parameters.AddWithValue("$v",receipt.Version);baseline.Parameters.AddWithValue("$s",prepared.SnapshotSha256);baseline.ExecuteNonQuery();
            }
            transaction.Commit();return next;
        }
    }
}
