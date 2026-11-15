using HybridMount.Commons.Scheduling;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Scheduling;

/// <summary>只转发既有执行器；不得持锁跨await，接口不会创建第二套队列或改变租约所有权。</summary>
public sealed partial class TasksQueueService
{
    IDisposable IMountTaskQueue.Enter(MountQueueBinding binding,MountingTask? task)=>Enter(binding,task);
    async Task<IMountNetworkLease> IMountTaskQueue.AcquireAsync(MountQueueBinding binding,MountingTask task,CancellationToken token)=>
        await AcquireAsync(binding,task,token).ConfigureAwait(false);
    async Task<IMountNetworkLease> IMountTaskQueue.AcquireCurrentWorkflowAsync(CancellationToken token,bool exclusive,bool subtree,string? destination)=>
        await AcquireCurrentWorkflowAsync(token,exclusive,subtree,destination).ConfigureAwait(false);
    IDisposable IMountTaskQueue.ShareResource(MountingTask task,string resource)=>ShareResource(task,resource);
    MountingPriority IMountTaskQueue.EffectivePriority(MountingTask task)=>EffectivePriority(task);
    bool IMountTaskQueue.IsCurrent(MountQueueBinding binding)=>IsCurrent(binding);
    IDisposable IMountTaskQueue.RegisterCancellation(string id,Func<bool> cancel)=>RegisterCancellation(id,cancel);
    IDownloadProgress? IMountTaskQueue.BeginDownloadProgress(string id,string path,long total)=>BeginDownloadProgress(id,path,total);
}
