namespace HybridMount.Models.Mounting;

/// <summary>仅作用于任务页的稳定排序，不改变上传或读取调度优先级。</summary>
public static class OperationDisplayOrder
{
    /// <summary>执行阶段优先，排队／等待其次，终态历史最后。</summary>
    public static int Group(string state)=>state switch
    {
        "Canceling" or "Running" or "Sending" or "RemoteCommitted" or "Downloading" or "Committing" or "Hydrating"=>0,
        "Deferred" or "Queued" or "Waiting" or "WaitingCapacity" or "Prepared" or "Pending" or "OutcomeUnknown" or "Conflict" or "AwaitingConfirmation"=>1,
        _=>2
    };
    /// <summary>同组按首次 SQLite 提交顺序，旧的无顺序输入保留相对位置；进度变化不参与排序。</summary>
    public static IReadOnlyList<MountOperationSnapshot> Sort(IEnumerable<MountOperationSnapshot> snapshots)=>snapshots
        .Select((item,index)=>(item,index)).OrderBy(x=>Group(x.item.State))
        .ThenBy(x=>x.item.SubmissionOrder>0?x.item.SubmissionOrder:long.MaxValue).ThenBy(x=>x.index).Select(x=>x.item).ToArray();
}
