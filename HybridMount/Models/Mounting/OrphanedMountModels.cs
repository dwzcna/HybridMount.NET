namespace HybridMount.Models.Mounting;

/// <summary>仅包含本地身份的遗留根快照；不推断服务器、凭据或可恢复的同步索引。</summary>
public sealed record OrphanedMountCandidate(Guid MountId,string LocalPath,string RootIdentity,string SyncRootId,bool Registered,string? Error=null)
{
    public bool CanRemove=>Error is null;
    public string Display=>LocalPath+"\n"+(Error??(Registered?"已核对本产品遗留同步根":"本地已清空，注销收尾待完成"));
}
/// <summary>离线清空的实际结果；部分删除不回滚，失败保留阶段供用户显式重试。</summary>
public sealed record OrphanedMountResult(bool Completed,int Deleted,string Stage,IReadOnlyList<string> Errors);
