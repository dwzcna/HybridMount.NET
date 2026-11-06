using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;

namespace HybridMount.Services.Mounting;

public sealed partial class MountRepository
{
    /// <summary>启动时仅结束已无定义且无恢复日志的远端任务历史；不删除证据、不恢复发送，也不把未执行内容记为完成。</summary>
    internal void RecoverRemovedRemoteOperations(IReadOnlySet<Guid> activeMounts)
    {
        lock(_gate)
        {
            using var db=Open();using var transaction=db.BeginTransaction();
            RetireRemovedRemoteOperationsCore(db,transaction,item=>item.MountId is {} id&&!activeMounts.Contains(id));
            transaction.Commit();
        }
    }

    /// <summary>调用方持有仓库锁及事务；只修改失去实际工作的展示历史，仍有恢复日志的任务保持原状。</summary>
    private static void RetireRemovedRemoteOperationsCore(SqliteConnection db,SqliteTransaction transaction,Func<MountOperationSnapshot,bool> removed)
    {
        using var query=db.CreateCommand();query.Transaction=transaction;
        query.CommandText="SELECT json FROM operations WHERE json_extract(json,'$.Operation')='remoteUpdate' AND NOT EXISTS(SELECT 1 FROM remote_pending p WHERE p.id=operations.id)";
        var retired=new List<MountOperationSnapshot>();
        using(var reader=query.ExecuteReader())
            while(reader.Read())
            {
                var item=JsonSerializer.Deserialize<MountOperationSnapshot>(reader.GetString(0))!;
                if(TaskHistoryRules.IsHistory(item.State)||!removed(item))continue;
                const string reason="原挂载已移除，此远端更改任务已停止；未记为同步完成。";
                retired.Add(item with{State="Canceled",Error=reason,Results=[new(item.ItemPath??"","Canceled",reason)]});
            }
        foreach(var item in retired)
        {
            using var update=db.CreateCommand();update.Transaction=transaction;update.CommandText="UPDATE operations SET json=$j WHERE id=$id";
            update.Parameters.AddWithValue("$id",item.Id);update.Parameters.AddWithValue("$j",JsonSerializer.Serialize(item));update.ExecuteNonQuery();
        }
    }
}
