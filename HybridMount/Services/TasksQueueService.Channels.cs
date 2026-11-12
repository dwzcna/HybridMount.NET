namespace HybridMount.Services;

public sealed partial class TasksQueueService
{
    /// <summary>在业务边界选通道；修改内部的容量、认证和属性核对通过同一 FlowId 继承，不猜测 HTTP 方法。</summary>
    private static CloudOperationChannel ChannelFor(string operation)=>operation switch
    {
        "upload" or "excludedRename" or "delete" or "move" or "rename" or "conflictResolution"=>CloudOperationChannel.Upload,
        "download" or "keepOnDevice" or "remoteUpdate"=>CloudOperationChannel.Download,
        "thumbnail"=>CloudOperationChannel.Thumbnail,
        _=>CloudOperationChannel.Metadata
    };
    private static bool SameFlow(MountingTask a,MountingTask b)=>a.MountId==b.MountId&&a.Generation==b.Generation&&
        ((a.FlowId!=Guid.Empty&&b.FlowId!=Guid.Empty?a.FlowId==b.FlowId:a.Id==b.Id)||SharedDownload(a,b));
    /// <summary>只有同一连接、同一路径和明确相同内容版本的只读下载才能共用一个文件工作流；远端应用的排他保护不借用。</summary>
    private static bool SharedDownload(MountingTask a,MountingTask b)=>a.Channel==CloudOperationChannel.Download&&b.Channel==CloudOperationChannel.Download&&
        !a.ExclusivePath&&!b.ExclusivePath&&a.Version is not null&&a.Version==b.Version&&a.Path is not null&&a.Path==b.Path&&
        (a.EntryId is null||b.EntryId is null||a.EntryId==b.EntryId);
    private bool CanRun(MountingTask task,bool workflow)=>!_active.Values.Any(lease=>Blocks(lease,task,workflow));
    private static bool Contains(string parent,string child)=>parent.Length==0||child.Equals(parent,StringComparison.Ordinal)||
        child.StartsWith(parent.TrimEnd('/')+"/",StringComparison.Ordinal);
    private static bool PathConflict(MountingTask a,MountingTask b)
    {
        // 独立元数据不读写本地正文；让其共享生产者继续完成，避免文件工作流等待自身锁外查询。
        if(a.Channel==CloudOperationChannel.Metadata&&!a.ExclusivePath||b.Channel==CloudOperationChannel.Metadata&&!b.ExclusivePath)return false;
        if(!(a.ExclusivePath||b.ExclusivePath||a.Channel==CloudOperationChannel.Upload||b.Channel==CloudOperationChannel.Upload))return false;
        if(a.EntryId is not null&&a.EntryId==b.EntryId)return true;
        foreach(var left in new[]{a.Path,a.DestinationPath}.OfType<string>())
            foreach(var right in new[]{b.Path,b.DestinationPath}.OfType<string>())
                if(left==right||a.Subtree&&Contains(left,right)||b.Subtree&&Contains(right,left))return true;
        return false;
    }
    private static bool Blocks(NetworkLease lease,MountingTask task,bool workflow)
    {
        if(lease.Task.MountId!=task.MountId)return false;
        if(SameFlow(lease.Task,task))return !workflow&&!lease.IsWorkflow; // 上一响应必须消费或关闭。
        return lease.Task.Channel==task.Channel||PathConflict(lease.Task,task);
    }
}
