namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>本程序远端临时项的意图日志；Scope 为服务器及账号的摘要，Hashes 仅允许本次已知内容，未确认不存在或删除前保留。</summary>
public sealed record RemoteTemporaryItem(Guid MountId,string Scope,string Path,string Purpose,string Stage,string[] Hashes,string? ETag=null,string? Error=null,long? Size=null,DateTimeOffset? ModifiedUtc=null,int HashAttempts=0,DateTimeOffset? NextHashAttemptUtc=null);
