namespace HybridMount.Models.Mounting;

/// <summary>空版本只允许创建；摘要必须来自原始远端快照，不能取自修改后的本地文件。</summary>
public sealed record UploadPrecondition(string? ExpectedVersion, string? BaselineSha256 = null);
/// <summary>已从不可变上传快照读取的字节数；总长度未知时为负数。</summary>
public sealed record UploadProgress(long Bytes, long Total);
/// <summary>与远端路径和原始版本绑定的内容基线，允许缓存回收后继续安全比较。</summary>
public sealed record SourceContentBaseline(string Path, string Version, string Sha256);
/// <summary>写能力分项记录；不从协议名称或其他操作的结果推断支持。</summary>
public sealed record SourceWriteCapabilities(CapabilitySupport CreateOnly = CapabilitySupport.Unknown,
    CapabilitySupport ConditionalUpdate = CapabilitySupport.Unknown, CapabilitySupport ConditionalDelete = CapabilitySupport.Unknown,
    CapabilitySupport ConditionalMove = CapabilitySupport.Unknown, CapabilitySupport ExclusiveLock = CapabilitySupport.Unknown,
    CapabilitySupport LockCreate = CapabilitySupport.Unknown, CapabilitySupport MultipartComplete = CapabilitySupport.Unknown, CapabilitySupport MoveCreate = CapabilitySupport.Unknown);
/// <summary>协议会话的上传辅助通知；处理器不得抛出异常或阻塞网络读取。</summary>
public interface IUploadDiagnosticsSource
{
    event Action<SourceContentBaseline>? ContentBaseline;
    event Action<string>? WriteWarning;
}

/// <summary>仅包含本地元数据的变化代次，不读取文件正文；USN 不可用时为零。</summary>
public sealed record LocalFileStamp(string Identity,long Usn,long Length,long LastWriteTime,long ChangeTime,bool IsDirectory,uint Reasons=0)
{
    public string Key=>$"{Identity}:{Usn}:{Length}:{LastWriteTime}:{ChangeTime}:{IsDirectory}";
}
/// <summary>首次启用时供用户选择的现有改动，路径相对挂载根。</summary>
public sealed record UploadCandidate(string Path,bool IsDirectory,string Reason);
/// <summary>上传意图、不可变快照和提交阶段的持久记录；Sending 重启后必须先核对远端。</summary>
public sealed record LocalUpload(string Id,Guid MountId,string LocalPath,string SourcePath,bool IsDirectory,
    string ObservedStamp,long Generation,string State,DateTimeOffset ChangedUtc,string? EntryId=null,
    string? ExpectedVersion=null,string? BaselineSha256=null,string? SnapshotPath=null,string? SnapshotSha256=null,
    string? SnapshotStamp=null,string? SnapshotIdentity=null,long SnapshotGeneration=0,long Bytes=0,long Total=0,
    SourceEntry? Receipt=null,string? Error=null,bool ExplicitRequest=false,bool CancelRequested=false,bool IsCopy=false,
    DateTimeOffset? LastCheckedUtc=null)
{
    /// <summary>同一上传的界面和持久历史使用相同描述；副本明确显示远端新路径。</summary>
    public MountOperationSnapshot ToSnapshot()=>new(Id,"upload",State,State=="Completed"?1:0,1,
        [new(LocalPath,State,Error??(IsCopy?$"另存到 {SourcePath}":IsDirectory?"本地目录上传":"本地文件上传"))],Error,MountId,LocalPath,Bytes,Total,IsDirectory,SourcePath);
}
