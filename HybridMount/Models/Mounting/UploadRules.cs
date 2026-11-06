namespace HybridMount.Models.Mounting;

/// <summary>上传意图、类型、变化与删除屏障的纯规则；不读文件、数据库或原生占位状态。</summary>
public static class UploadRules
{
    /// <summary>区分可重试网络／服务拒绝和永久故障；主动取消先由上传所有者处理。</summary>
    public static bool IsTransient(Exception error)=>error switch
    {
        SourceRequestRejectedException rejected=>TransientStatus(rejected.HttpStatus),
        SourceConnectionException connection=>connection.Diagnostic.Transient||connection.Diagnostic.Kind is ConnectionFailureKind.Network or ConnectionFailureKind.Timeout||connection.Diagnostic.HttpStatus is {} status&&TransientStatus(status),
        SourceOutcomeUnknownException=>true,
        System.Net.Http.HttpRequestException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException=>true,
        _=>false
    };
    private static bool TransientStatus(int status)=>status is 0 or 408 or 425 or 429 or 500 or 502 or 503 or 504;
    /// <summary>客户端指数退避最多五分钟；服务器Retry-After更长时遵循服务器等待，抖动可注入以确定性验证。</summary>
    public static TimeSpan TransientDelay(int failures,TimeSpan? retryAfter=null,double? jitter=null)
    {
        var seconds=3*Math.Pow(2,Math.Clamp(failures,0,7))*(0.8+0.4*Math.Clamp(jitter??Random.Shared.NextDouble(),0,1));
        return TimeSpan.FromSeconds(Math.Max(Math.Min(300,seconds),retryAfter?.TotalSeconds??0));
    }
    /// <summary>产生可持久化的等待状态；保留原快照和基线，不在此发送请求。</summary>
    public static LocalUpload WaitForTransient(LocalUpload item,Exception error,DateTimeOffset now)
    {
        var next=now+TransientDelay(item.TransientFailureCount,(error as SourceRequestRejectedException)?.RetryAfter);
        return item with{State="Waiting",TransientFailureCount=item.TransientFailureCount==int.MaxValue?int.MaxValue:item.TransientFailureCount+1,
            LastCheckedUtc=now,NextAttemptUtc=next,Error=error.Message+$" 下次重试 {next.ToLocalTime():HH:mm:ss}。"};
    }
    /// <summary>综合稳定期、旧记录检查间隔和新退避时间，返回本代次最早允许调度的时刻。</summary>
    public static DateTimeOffset AttemptUtc(LocalUpload item)
    {
        var checkedAt=item.State=="Waiting"&&item.NextAttemptUtc is {} next?next:
            item.LastCheckedUtc is {} last?last+TimeSpan.FromSeconds(item.State is "OutcomeUnknown" or "WaitingCapacity"?60:3):DateTimeOffset.MinValue;
        var stable=item.ChangedUtc+TimeSpan.FromSeconds(2);return checkedAt>stable?checkedAt:stable;
    }
    /// <summary>从调用方已筛选的候选中选择到期任务；目录及浅路径优先，无到期任务返回空。</summary>
    public static LocalUpload? NextUpload(IEnumerable<LocalUpload> items,DateTimeOffset now)=>items.Where(item=>AttemptUtc(item)<=now)
        .OrderBy(item=>item.SourcePath.Count(c=>c=='/')).ThenBy(item=>item.IsDirectory?0:1).ThenBy(item=>item.ChangedUtc).FirstOrDefault();
    /// <summary>计算下一任务或全量兜底扫描前的等待；唤醒和取消仍由调度器拥有。</summary>
    public static TimeSpan UploadWait(IEnumerable<LocalUpload> items,DateTimeOffset now,DateTimeOffset scanDue)
        =>TimeSpan.FromMilliseconds(Math.Max(1,(items.Select(AttemptUtc).Append(scanDue).Min()-now).TotalMilliseconds));
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

    /// <summary>本地删除不删除远端；只有精确同路径、已物化且唯一的旧映射可以给同名替换提供原始基线。</summary>
    /// <param name="entryId">已有任务绑定的索引身份优先；不能从任意新枚举或歧义历史中选择版本。</param>
    public static MountEntry? LocalReplacementBaseline(IEnumerable<MountEntry> entries,string path,string? entryId=null)
    {
        var candidates=entries.Where(x=>x.Deleted&&x.Materialized&&x.RemotePresence!=RemotePresence.Deleted&&
            x.LocalPath==path&&x.SourcePath==path).ToArray();
        if(entryId is not null)return candidates.FirstOrDefault(x=>x.Id==entryId);
        return candidates.Length==1?candidates[0]:null;
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
