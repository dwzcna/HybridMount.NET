namespace HybridMount.Models.Mounting;

/// <summary>远端事件统一投影；任务页和只读生命周期共用，不读取存储、不推断执行权限。</summary>
public static class RemoteTaskSnapshot
{
    public static MountOperationSnapshot Create(RemotePendingChange change, long bytes = 0) => new(
        change.Id, change.DownloadTask ? "download" : "remoteUpdate", change.State == "Pending" ? "Queued" : change.State,
        change.State == "Completed" ? 1 : 0, 1, change.Error is null ? [] : [new(change.Path, change.State, change.Error)],
        change.Error, change.MountId, change.Path, bytes, change.Entry?.Size ?? 0, change.Entry?.IsDirectory, change.Path)
        { AttemptId = change.AttemptId };
}
