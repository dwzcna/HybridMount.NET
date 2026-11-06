namespace HybridMount.Models.Mounting;

/// <summary>上传意图、类型、变化与删除屏障的纯规则；不读文件、数据库或原生占位状态。</summary>
public static class UploadRules
{
    /// <summary>类型互换统一冲突；显式重试和扫描共用同一规则及提示。</summary>
    public const string TypeConflictMessage="本地对象、上传意图、快照或远端回执的文件/目录类型不一致；请恢复原类型后重试，或改名后上传。";
    /// <summary>校验当前对象、不可变快照与远端回执的类型；任何不一致均拒绝发送或提交。</summary>
    public static void ValidateTypes(LocalUpload intent,LocalFileStamp? local=null,bool? remoteDirectory=null)
    {
        if(local is not null&&local.IsDirectory!=intent.IsDirectory||remoteDirectory is not null&&remoteDirectory!=intent.IsDirectory)
            throw new SourceConflictException(TypeConflictMessage);
        if(intent.SnapshotStamp is not null)
        {
            var parts=intent.SnapshotStamp.Split(':');
            if(parts.Length!=6||!bool.TryParse(parts[5],out var directory)||directory!=intent.IsDirectory)
                throw new SourceConflictException(TypeConflictMessage);
        }
    }

    /// <summary>未纳入的旧改动不会因固定、属性或访问等元数据变化自动获得上传授权。</summary>
    public static bool ContentChanged(string observed,LocalFileStamp current)
    {
        var parts=observed.Split(':');if(parts.Length!=6||parts[0]!=current.Identity)return true;
        if(current.IsDirectory)return false;
        if(!long.TryParse(parts[2],out var length)||length!=current.Length||!long.TryParse(parts[3],out var time)||time!=current.LastWriteTime)return true;
        return long.TryParse(parts[1],out var usn)&&current.Usn!=usn&&(current.Reasons&0x77)!=0;
    }

    /// <summary>远端删除屏障和发送阶段判断，不把结果不明视为可安全重建。</summary>
    public const string RemoteDeletedMessage="远端已删除此路径或父目录；保留本地内容，禁止自动重新创建。请刷新核对或另存到其他路径。";
    public static bool SourceContains(string parent,string path)=>parent==path||path.StartsWith(parent+"/",StringComparison.Ordinal);
    public static bool InFlight(LocalUpload item)=>item.State is "Sending" or "RemoteCommitted" or "OutcomeUnknown";
    public static bool Unfinished(LocalUpload item)=>item.State is not("Completed" or "Canceled" or "Ignored");
    public static MountEntry? DeletedSourceAncestor(IEnumerable<MountEntry> entries,string path)
        =>entries.FirstOrDefault(entry=>entry.RemotePresence==RemotePresence.Deleted&&SourceContains(entry.SourcePath,path));
    public static bool InFlightOrCompleted(LocalUpload? item)=>item is not null&&(InFlight(item)||item.State is "Completed" or "Canceled" or "Ignored");
    /// <summary>重试只恢复可重试状态；已经发送的未知结果和回执仍先核对。</summary>
    public static bool CanRetry(LocalUpload item)=>item.State is "Failed" or "Conflict" or "Canceled" or "OutcomeUnknown" or "Waiting" or "Ignored" or "AwaitingConfirmation" or "WaitingCapacity"||item.State=="RemoteCommitted"&&!string.IsNullOrEmpty(item.Error);
    public static string RetryState(LocalUpload item)=>item.State=="OutcomeUnknown"?item.State:item.Receipt is not null?"RemoteCommitted":"Queued";
}
