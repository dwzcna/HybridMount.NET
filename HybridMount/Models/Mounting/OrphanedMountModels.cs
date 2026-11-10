namespace HybridMount.Models.Mounting;

/// <summary>仅包含本地身份的遗留根快照；ShellOnly候选只清注册，绝不清空目录。可选字段兼容旧日志。</summary>
public sealed record OrphanedMountCandidate(Guid MountId,string LocalPath,string RootIdentity,string SyncRootId,bool Registered,string? Error=null)
{
    // 新信息使用可选属性，保留已有构造与Deconstruct调用兼容。
    public bool ShellOnly {get;init;}
    public string? NamespaceId {get;init;}
    public string? ContentStamp {get;init;}
    public bool CanRemove=>Error is null;
    public string ActionText=>ShellOnly?"移除遗留入口":"清空并注销";
    public string Display=>LocalPath+"\n"+(Error??(ShellOnly?"仅剩 Shell 入口":Registered?"已核对本产品遗留同步根":"本地已清空，注销收尾待完成"));
}
/// <summary>离线移除的实际结果；部分删除不回滚，失败保留阶段供用户显式重试。</summary>
public sealed record OrphanedMountResult(bool Completed,int Deleted,string Stage,IReadOnlyList<string> Errors);
