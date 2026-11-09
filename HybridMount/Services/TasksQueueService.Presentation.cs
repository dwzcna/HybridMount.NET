using HybridMount.Common;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Services;

public sealed partial class TasksQueueService
{
    private MountManager? _backend;
    private MountManager Backend=>_backend??throw new InvalidOperationException("任务业务执行器尚未注册。");
    /// <summary>应用启动时注册唯一业务执行器；服务拥有任务页入口，业务层继续拥有条件写入和持久事务。</summary>
    internal void Attach(MountManager backend)
    {if(_backend is not null)throw new InvalidOperationException("任务业务执行器已经注册。");_backend=backend;backend.Changed+=BackendChanged;}
    private void BackendChanged()=>RaiseChanged();
    public bool IsStopping=>_stopping||_disposed||_backend?.IsStopping==true;
    public IReadOnlyList<MountSnapshot> Mounts=>Backend.Mounts;
    public IReadOnlyList<MountOperationSnapshot> SyncIssues=>EnrichAll(Backend.SyncIssues);
    public IReadOnlyList<MountOperationSnapshot> Conflicts=>EnrichAll(Backend.Conflicts);
    /// <summary>冲突对象按当前持久记录重建，不占调度名额；清除展示历史不能删除待决冲突。</summary>
    public IReadOnlyList<IMountingConflict> ConflictItems=>Backend.Conflicts.Where(x=>x.MountId is not null).Select(x=>(IMountingConflict)new Conflict(this,x.Id,x.MountId!.Value,x.ItemPath)).ToArray();
    public IReadOnlyList<MountOperationSnapshot> Operations
    {
        get
        {
            var steps=QueueSnapshots;var schedule=Backend.TaskSchedules();
            var persistent=Backend.Operations.Select(x=>Enrich(x,steps,schedule)).ToDictionary(x=>x.Id);
            foreach(var group in steps.GroupBy(x=>x.Task.Id))
            {
                if(persistent.ContainsKey(group.Key))continue;
                var current=group.OrderBy(x=>x.WaitingReason is null?0:1).ThenBy(x=>x.Sequence).First();var task=current.Task;
                persistent[task.Id]=Enrich(new(task.Id,task.Operation,current.WaitingReason is null?"Running":"Queued",0,1,[],null,task.MountId,task.Path){SubmissionOrder=task.SubmitTime.UtcTicks},steps,schedule);
            }
            return persistent.Values.ToArray();
        }
    }
    /// <summary>一次页面快照只批量读取一次业务调度信息，避免每行／每个范围块重新查询SQLite。</summary>
    private IReadOnlyList<MountOperationSnapshot> EnrichAll(IEnumerable<MountOperationSnapshot> snapshots)
    {var steps=QueueSnapshots;var schedule=Backend.TaskSchedules();return snapshots.Select(x=>Enrich(x,steps,schedule)).ToArray();}
    private static MountOperationSnapshot Enrich(MountOperationSnapshot snapshot,IReadOnlyList<MountingQueueSnapshot> queue,IReadOnlyDictionary<string,(MountingPriority Priority,DateTimeOffset? Retry)> schedule)
    {
        var steps=queue.Where(x=>x.Task.Id==snapshot.Id).ToArray();
        var current=steps.OrderBy(x=>x.WaitingReason is null?0:1).ThenBy(x=>x.Sequence).FirstOrDefault();
        schedule.TryGetValue(snapshot.Id,out var taskSchedule);
        var priority=current?.EffectivePriority??(schedule.ContainsKey(snapshot.Id)?taskSchedule.Priority:snapshot.Operation is "upload" or "remoteUpdate" or "excludedRename"?MountingPriority.Transaction:MountingPriority.User);
        return snapshot with{Priority=priority,NextRetryUtc=taskSchedule.Retry,QueuePhase=current?.Phase,SubmittedUtc=current?.Task.SubmitTime,WaitingReason=current?.WaitingReason??snapshot.WaitingReason,WaitingTaskId=current?.BlockingTaskId??snapshot.WaitingTaskId};
    }
    public MountOperationSnapshot? GetOperation(string id)=>Operations.FirstOrDefault(x=>x.Id==id)??Backend.GetOperation(id);
    public bool Cancel(string id)
    {
        if(_backend?.Cancel(id)==true)return true;
        CancelRegistration[] owners;lock(_sync)owners=_cancelActions.Where(x=>x.Id==id).ToArray();
        var canceled=false;foreach(var owner in owners)try{canceled=owner.Cancel()||canceled;}catch(ObjectDisposedException){/* 所有者恰好结束，不能取消已经释放的令牌。 */}return canceled;
    }
    public bool RetryUpload(string id)
    {
        var item=Backend.GetOperation(id);if(item?.MountId is not {} mount)return false;
        using var scope=EnterTask(mount,id,MountingPriority.User,"retry",item.ItemPath);return Backend.RetryUpload(id);
    }
    public bool RecheckOperation(string id)
    {
        var item=Backend.GetOperation(id);if(item?.MountId is not {} mount)return false;
        using var scope=EnterTask(mount,id,MountingPriority.User,"recheck",item.ItemPath);return Backend.RecheckOperation(id);
    }
    public string UploadAsCopy(string id,string path)=>Backend.UploadAsCopy(id,path);
    public string SyncIssueLocalPath(string id)=>Backend.SyncIssueLocalPath(id);
    public Task<int> ClearOperationHistoryAsync(TaskHistoryClearMode mode,CancellationToken token=default)=>Backend.ClearOperationHistoryAsync(mode,token);
    public void ReportDiagnosticException(string area,string message,Exception error)=>Backend.ReportDiagnosticException(area,message,error);
    public Task<ConflictComparison> CompareConflictAsync(string id,CancellationToken token=default)
    {
        var item=Backend.GetOperation(id)??throw new IOException("冲突任务不存在。");
        return new Conflict(this,id,item.MountId??throw new IOException("冲突缺少挂载身份。"),item.ItemPath).CompareAsync(token);
    }
    public Task<string> ResolveConflictAsync(ConflictComparison proof,bool useLocal,CancellationToken token=default)
        =>new Conflict(this,proof.TaskId,proof.Definition.Id,proof.Path).ResolveAsync(proof,useLocal,token);
    private sealed record Conflict(TasksQueueService Queue,string Id,Guid MountId,string? Path):IMountingConflict
    {
        public async Task<ConflictComparison> CompareAsync(CancellationToken token=default)
        {using var scope=Queue.EnterTask(MountId,Id,MountingPriority.User,"conflictCheck",Path);return await Queue.Backend.CompareConflictAsync(Id,token).ConfigureAwait(false);}
        public async Task<string> ResolveAsync(ConflictComparison proof,bool useLocal,CancellationToken token=default)
        {
            if(proof.TaskId!=Id||proof.Definition.Id!=MountId)throw new ArgumentException("冲突证明不属于当前任务。");
            using var scope=Queue.EnterTask(MountId,Id,MountingPriority.User,"conflictResolution",Path);
            return await Queue.Backend.ResolveConflictAsync(proof,useLocal,token).ConfigureAwait(false);
        }
    }
}
