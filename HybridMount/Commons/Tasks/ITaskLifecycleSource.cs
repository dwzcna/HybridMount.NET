using HybridMount.Models.Tasks;
namespace HybridMount.Commons.Tasks;

/// <summary>只读业务提交源。通知在提交成功后同步交付；订阅者只可处理内存，不访问仓库、UI或网络。</summary>
public interface ITaskLifecycleSource
{
    event Action<TaskLifecycleCommit>? TaskCommitted;
    /// <summary>后台读取当前未结束业务任务及提交水位；不包含旧终态历史，不发远端请求。</summary>
    TaskLifecycleCommit ReadUnfinishedTasks();
}
