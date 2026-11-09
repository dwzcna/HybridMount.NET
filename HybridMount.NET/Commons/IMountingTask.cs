using HybridMount.Models.Mounting;
namespace HybridMount.Commons;

/// <summary>一个挂载业务任务的不可变身份；优先级越高越先执行，同级按UTC提交时间及稳定序号调度。</summary>
public interface IMountingTask
{
    string Id {get;}
    Guid MountId {get;}
    long Generation {get;}
    MountingPriority Priority {get;}
    DateTimeOffset SubmitTime {get;}
    string Operation {get;}
    string? Path {get;}
    string? EntryId {get;}
    string? Version {get;}
    IReadOnlyList<string> Dependencies {get;}
}
/// <summary>可恢复步骤执行器；返回true表示完成，false表示到达安全让路点；写入结果仍由原业务日志核对。</summary>
public interface IMountingTaskStep
{
    Task<bool> ExecuteStepAsync(CancellationToken token);
}
