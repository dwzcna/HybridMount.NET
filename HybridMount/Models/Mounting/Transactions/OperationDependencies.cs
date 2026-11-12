namespace HybridMount.Models.Mounting.Transactions;

/// <summary>每次执行前从现有持久记录重建依赖，不新增SQLite列或把失败当作完成。</summary>
internal static class OperationDependencies
{
    internal static bool UploadReady(IEnumerable<LocalUpload> all,LocalUpload item)=>!all.Any(parent=>parent.Id!=item.Id&&parent.IsDirectory&&parent.SourcePath!=item.SourcePath&&parent.State!="Completed"&&UploadRules.SourceContains(parent.SourcePath,item.SourcePath));
    internal static bool RemoteReady(IEnumerable<RemotePendingChange> all,RemotePendingChange item)
    {
        var pending=all.Where(x=>x.Id!=item.Id&&x.State is not("Completed" or "Superseded")).ToArray();
        if(item.Entry is null)return !pending.Any(x=>UploadRules.SourceContains(item.Path,x.Path)&&x.Path!=item.Path);
        return !pending.Any(x=>x.Path!=item.Path&&x.Entry is {IsDirectory:true}&&UploadRules.SourceContains(x.Path,item.Path));
    }
}
