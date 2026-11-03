namespace HybridMount.Models.Mounting;

/// <summary>一份完整校验的 sync-level 1 响应；只有资源级 404 进入 Deleted。</summary>
public sealed record RemoteChangeBatch(IReadOnlyList<SourceEntry> Entries,IReadOnlyList<string> Deleted,string NextToken,bool HasMore);
/// <summary>协议失败保留结构化分类；不含响应原文、认证信息或不透明同步令牌。</summary>
public sealed class RemoteSyncException(int status,bool invalidToken=false,TimeSpan? retryAfter=null,bool unsupportedReport=false)
    : IOException(invalidToken?"WebDAV 同步令牌已失效，需要重新建立完整基线。":$"WebDAV 远端检测失败（HTTP {status}）。")
{
    public int Status {get;}=status;
    public bool InvalidToken {get;}=invalidToken;
    public TimeSpan? RetryAfter {get;}=retryAfter;
    public bool AuthenticationFailure=>Status is 401 or 403;
    public bool Unsupported=>unsupportedReport||Status is 405 or 501;
}
/// <summary>每目录检查点；初始基线续批不改变已确认的 Shadow，直到完整成功。</summary>
public sealed record RemoteCollectionCheckpoint(string Path,string Mode="Unknown",string? Token=null,bool BuildingBaseline=false,
    IReadOnlyList<SourceEntry>? Baseline=null,DateTimeOffset? LastFullUtc=null);
/// <summary>经确认的远端状态；观测代次只用于缓存和内容基线隔离。</summary>
public sealed record RemoteShadowEntry(SourceEntry Entry,long Observation=0);
/// <summary>持久的本地待应用事件；游标可以前进，因为事件、阶段和快照均可重放。</summary>
public sealed record RemotePendingChange(string Id,Guid MountId,string Scope,string Path,SourceEntry? Entry,
    string State="Pending",string? Error=null,string? SnapshotPath=null,string? Sha256=null,string? OriginalStamp=null,
    string? EntryId=null,long UploadGeneration=0,uint PinState=0,string? OldVersion=null);
/// <summary>挂载的检测状态，不包含内部游标。</summary>
public sealed record RemoteDetectionStatus(string Mode="尚未检测",DateTimeOffset? LastSuccessUtc=null,string? Error=null);
