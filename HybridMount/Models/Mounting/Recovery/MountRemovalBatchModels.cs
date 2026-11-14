namespace HybridMount.Models.Mounting.Recovery;

/// <summary>只读确认快照；完整定义仅用于确认后的配置重验，不包含凭据。</summary>
internal sealed record MountRemovalBatchPlan(IReadOnlyList<MountDefinition> Mounts);
/// <summary>逐根结果与任务链接；失败不会中断后续根，不把部分完成计为成功。</summary>
internal sealed record MountRemovalBatchItem(Guid MountId,string Name,string Path,string State,string? TaskId,string? Error=null);
internal sealed record MountRemovalBatchResult(IReadOnlyList<MountRemovalBatchItem> Items)
{
    internal int Succeeded=>Items.Count(item=>item.State=="Completed");
    internal int Failed=>Items.Count(item=>item.State is "Failed" or "PartialFailure");
    internal int Skipped=>Items.Count-Succeeded-Failed;
}
