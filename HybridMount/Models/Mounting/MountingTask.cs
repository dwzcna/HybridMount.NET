namespace HybridMount.Models.Mounting;

/// <summary>P0最高、P5最低；枚举数值按降序调度，不以等待时间提高后台任务优先级。</summary>
public enum MountingPriority { Maintenance=100,Thumbnail=200,Observation=300,Transaction=400,User=500,Demand=600 }
/// <summary>仅内存中的调度描述，不替代上传、远端应用或改名的持久恢复记录。</summary>
public sealed record MountingTask(string Id,Guid MountId,long Generation,MountingPriority Priority,DateTimeOffset SubmitTime,
    string Operation,string? Path=null,string? EntryId=null,string? Version=null) : Common.IMountingTask
{
    public IReadOnlyList<string> Dependencies {get;init;}=[];
}
/// <summary>一个当前网络步骤的只读快照；原始错误来自业务记录，等待原因不会覆盖它。</summary>
public sealed record MountingQueueSnapshot(MountingTask Task,MountingPriority EffectivePriority,string Phase,string? WaitingReason,string? BlockingTaskId,long Sequence);
