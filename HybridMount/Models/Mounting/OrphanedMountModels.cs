namespace HybridMount.Models.Mounting;

/// <summary>仅包含本地身份的遗留根快照；ShellOnly候选只清注册，绝不清空目录。可选字段兼容旧日志。</summary>
public sealed record OrphanedMountCandidate(Guid MountId,string LocalPath,string RootIdentity,string SyncRootId,bool Registered,string? Error=null)
{
    // 新信息使用可选属性，保留已有构造与Deconstruct调用兼容。
    public bool ShellOnly {get;init;}
    /// <summary>用户手动确认的孤立占位目录；只能进入专用临时恢复流程，旧候选默认false。</summary>
    public bool OrphanedDirectory {get;init;}
    public string? NamespaceId {get;init;}
    public string? ContentStamp {get;init;}
    public bool CanRemove=>Error is null;
    public string ActionText=>OrphanedDirectory?"清空孤立占位目录":ShellOnly?"移除遗留入口":"清空并注销";
    public string Display=>LocalPath+"\n"+(Error??(OrphanedDirectory?"孤立占位目录，原提供程序归属无法核验":ShellOnly?"仅剩 Shell 入口":Registered?"已核对本产品遗留同步根":"本地已清空，注销收尾待完成"));
}
/// <summary>离线移除的实际结果；部分删除不回滚，失败保留阶段供用户显式重试。</summary>
public sealed record OrphanedMountResult(bool Completed,int Deleted,string Stage,IReadOnlyList<string> Errors)
{
    /// <summary>保留原失败码；未发生错误时为空，兼容原构造与解构。</summary>
    public int? HResult {get;init;}
}
/// <summary>批量结果明确区分实际成功、失败、执行中取消与尚未开始。</summary>
public enum OrphanedMountItemState {Completed,Failed,Canceled,NotStarted}
/// <summary>逐根结果快照；错误与阶段对应实际执行，不将未开始项目记为已取消。</summary>
public sealed record OrphanedMountItemResult(OrphanedMountCandidate Root,OrphanedMountItemState State,int Deleted,string Stage,int? HResult,IReadOnlyList<string> Errors);
/// <summary>有界UI进度由页面合并；索引从1开始，不携带原生句柄。</summary>
public sealed record OrphanedMountBatchProgress(int Index,int Total,string LocalPath,string Stage);
/// <summary>批量终态保留逐根明细，可在关闭进度对话框后展示汇总。</summary>
public sealed record OrphanedMountBatchResult(IReadOnlyList<OrphanedMountItemResult> Items)
{
    public int Completed=>Items.Count(x=>x.State==OrphanedMountItemState.Completed);
    public int Failed=>Items.Count(x=>x.State==OrphanedMountItemState.Failed);
    public int Canceled=>Items.Count(x=>x.State==OrphanedMountItemState.Canceled);
    public int NotStarted=>Items.Count(x=>x.State==OrphanedMountItemState.NotStarted);
    public string Summary=>$"成功 {Completed} 项，失败 {Failed} 项，取消 {Canceled} 项，未开始 {NotStarted} 项。";
    /// <summary>生成用户可复制的阶段及错误清单；成功项也保留路径。</summary>
    public string Detail=>Summary+"\n\n"+string.Join("\n\n",Items.Select(x=>x.Root.LocalPath+"\n"+x.State+" · "+x.Stage+
        (x.HResult is {} code?$" · 0x{code:X8}":"")+(x.Errors.Count>0?"\n"+string.Join("\n",x.Errors):"")));
}
