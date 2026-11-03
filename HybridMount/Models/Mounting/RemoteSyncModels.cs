namespace HybridMount.Models.Mounting;

/// <summary>一份完整校验的 sync-level 1 响应；只有资源级 404 进入 Deleted。</summary>
public sealed record RemoteChangeBatch(IReadOnlyList<SourceEntry> Entries,IReadOnlyList<string> Deleted,string NextToken,bool HasMore);
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
public sealed record RemoteDetectionStatus(string Mode="尚未检测",DateTimeOffset? LastSuccessUtc=null,string? Error=null,DateTimeOffset? LastEventUtc=null,int PendingHints=0,string QueueMode="尚未启动",string? QueueError=null);

/// <summary>协议无关的持久提示身份；不包含事件报文或消息确认令牌。</summary>
public sealed record RemotePathHint(string Id,string Path);
/// <summary>协议重新核实后的提示结果；通用同步器持久化后才推进提示状态。</summary>
public sealed record RemoteHintChange(string Id,IReadOnlyList<SourceEntry> Entries,IReadOnlyList<string> Deleted);
/// <summary>远端变化能力的通用失败；检查点失效、不支持、权限及退避由模块从协议结果映射。</summary>
public class RemoteChangeFailure(string message,bool invalidCheckpoint,bool unsupported,bool authenticationFailure,TimeSpan? retryAfter):IOException(message)
{
    public bool InvalidCheckpoint {get;}=invalidCheckpoint;
    public bool Unsupported {get;}=unsupported;
    public bool AuthenticationFailure {get;}=authenticationFailure;
    public TimeSpan? RetryAfter {get;}=retryAfter;
}
