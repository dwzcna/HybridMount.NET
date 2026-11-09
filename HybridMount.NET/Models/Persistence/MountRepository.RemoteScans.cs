namespace HybridMount.Models.Persistence;

public sealed partial class MountRepository
{
    /// <summary>部分扫描重启后重做；扫描记录与最终快照事务共同给出明确完成证据。</summary>
    public void BeginRemoteScan(Guid mount,string scope,string id)
    {
        lock(_gate)
        {
            using var db=Open();using var command=db.CreateCommand();
            command.CommandText="UPDATE remote_scans SET state='Interrupted' WHERE mount=$m AND scope=$s AND state='Running';INSERT INTO remote_scans VALUES($id,$m,$s,'Running',$time)";
            command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$s",scope);command.Parameters.AddWithValue("$time",DateTimeOffset.UtcNow.ToString("O"));command.ExecuteNonQuery();
        }
    }
    public void FailRemoteScan(Guid mount,string scope,string id)
    {
        lock(_gate){using var db=Open();using var command=db.CreateCommand();command.CommandText="UPDATE remote_scans SET state='Interrupted' WHERE id=$id AND mount=$m AND scope=$s AND state='Running'";
            command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$m",mount.ToString("N"));command.Parameters.AddWithValue("$s",scope);command.ExecuteNonQuery();}
    }
}
