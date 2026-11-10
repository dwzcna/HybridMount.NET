using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>创建请求发送前同步持久化临时项意图；范围和摘要由协议驱动校验。</summary>
    public void SaveTemporary(RemoteTemporaryItem item)
    {
        lock(_gate){using var db=Open();using var command=db.CreateCommand();command.CommandText="INSERT INTO remote_temporary VALUES($m,$s,$p,$j) ON CONFLICT(mount,scope,path) DO UPDATE SET json=$j";
            command.Parameters.AddWithValue("$m",item.MountId.ToString("N"));command.Parameters.AddWithValue("$s",item.Scope);command.Parameters.AddWithValue("$p",item.Path);command.Parameters.AddWithValue("$j",JsonSerializer.Serialize(item));command.ExecuteNonQuery();}
    }
    /// <summary>只返回此挂载及远端账号根的记录；不会复用另一服务器或用户的清理授权。</summary>
    public IReadOnlyList<RemoteTemporaryItem> TemporaryItems(Guid mount,string scope)
    {
        lock(_gate){using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT json FROM remote_temporary WHERE mount=$m AND scope=$s";command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$s",scope);
            using var reader=command.ExecuteReader();var result=new List<RemoteTemporaryItem>();while(reader.Read())result.Add(JsonSerializer.Deserialize<RemoteTemporaryItem>(reader.GetString(0))??throw new IOException("临时项记录损坏。"));return result;}
    }
    /// <summary>仅在远端已明确不存在或删除成功后完成记录；清理失败不影响用户文件上传结果。</summary>
    public void CompleteTemporary(Guid mount,string scope,string path)
    {
        lock(_gate){using var db=Open();using var command=db.CreateCommand();command.CommandText="DELETE FROM remote_temporary WHERE mount=$m AND scope=$s AND path=$p";command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$s",scope);command.Parameters.AddWithValue("$p",path);command.ExecuteNonQuery();}
    }
}
