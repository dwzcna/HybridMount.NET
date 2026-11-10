using HybridMount.Models.Mounting;
namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>在当前映射与版本仍匹配时提交远端状态；不会改变用户本地删除及修改保护。</summary>
    public bool SetRemotePresence(Guid mount,MountEntry expected,RemotePresence presence)
    {
        if(!Enum.IsDefined(presence))throw new ArgumentOutOfRangeException(nameof(presence));
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();
            command.CommandText="INSERT INTO remote_presence SELECT mount,id,$p FROM entries WHERE mount=$m AND id=$id AND source=$s AND local=$l AND version=$v ON CONFLICT(mount,id) DO UPDATE SET presence=$p";
            command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$id",expected.Id);command.Parameters.AddWithValue("$s",expected.SourcePath);
            command.Parameters.AddWithValue("$l",expected.LocalPath);command.Parameters.AddWithValue("$v",expected.Version);command.Parameters.AddWithValue("$p",(int)presence);return command.ExecuteNonQuery()!=0;
        }
    }
    /// <summary>确认本地清理后解除物化标记，保留远端删除记录；不生成用户删除意图。</summary>
    public void CompleteRemoteCleanup(Guid mount,MountEntry expected)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();
            command.CommandText="UPDATE entries SET materialized=0 WHERE mount=$m AND id=$id AND version=$v AND local=$l AND EXISTS(SELECT 1 FROM remote_presence p WHERE p.mount=entries.mount AND p.id=entries.id AND p.presence=$p)";
            command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$id",expected.Id);command.Parameters.AddWithValue("$v",expected.Version);command.Parameters.AddWithValue("$l",expected.LocalPath);command.Parameters.AddWithValue("$p",(int)RemotePresence.Deleted);command.ExecuteNonQuery();
        }
    }
}
