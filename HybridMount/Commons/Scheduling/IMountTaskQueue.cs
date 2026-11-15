using HybridMount.Models.Mounting;
namespace HybridMount.Commons.Scheduling;

/// <summary>协议与原生消费者只借用调度能力；配额、轮转、依赖及取消所有权由应用调度器保留。</summary>
internal interface IMountTaskQueue
{
    IDisposable Enter(MountQueueBinding binding,MountingTask? task=null);
    Task<IMountNetworkLease> AcquireAsync(MountQueueBinding binding,MountingTask task,CancellationToken token);
    Task<IMountNetworkLease> AcquireCurrentWorkflowAsync(CancellationToken token,bool exclusive=false,bool subtree=false,string? destination=null);
    IDisposable ShareResource(MountingTask task,string resource);
    MountingPriority EffectivePriority(MountingTask task);
    bool IsCurrent(MountQueueBinding binding);
    IDisposable RegisterCancellation(string id,Func<bool> cancel);
    IDownloadProgress? BeginDownloadProgress(string id,string path,long total);
}
/// <summary>执行位的借用边界；Validate拒绝退役代次，Dispose仍由原租约幂等归还。</summary>
internal interface IMountNetworkLease:IDisposable
{
    CancellationToken StopToken {get;}
    void Validate();
}
/// <summary>只发布真实字节，不允许消费者控制任务状态或重放读取。</summary>
internal interface IDownloadProgress:IDisposable
{
    void Advance(long count);
}
