using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>调用者必须持根锁；只把网络／队列等待移到锁外，异常和取消均先恢复原锁所有权。</summary>
    private static async Task<T> OutsideRootAsync<T>(Runtime root,Func<Task<T>> network)
    {root.Gate.Release();try{return await network().ConfigureAwait(false);}finally{await root.Gate.WaitAsync().ConfigureAwait(false);}}
    private static Task OutsideRootAsync(Runtime root,Func<Task> network)=>OutsideRootAsync(root,async()=>{await network().ConfigureAwait(false);return true;});
    /// <summary>任务页批量只读调度信息；不探测协议或写入记录，避免按行查询数据库。</summary>
    internal IReadOnlyDictionary<string,(MountingPriority Priority,DateTimeOffset? Retry)> TaskSchedules()
    {
        var result=_repository.Uploads().ToDictionary(x=>x.Id,x=>(x.ExplicitRequest?MountingPriority.User:MountingPriority.Transaction,x.NextAttemptUtc));
        foreach(var root in _roots.Values)foreach(var pending in RemotePendingFor(root))result[pending.Id]=(MountingPriority.Transaction,root.RemoteSync?.StagingRetries.NextRetryUtc(pending.Id));
        foreach(var rename in RenameIntents())result[rename.Id]=(rename.RetryRequested||rename.ReadOnlyRequested?MountingPriority.User:MountingPriority.Transaction,null);
        return result;
    }
}
