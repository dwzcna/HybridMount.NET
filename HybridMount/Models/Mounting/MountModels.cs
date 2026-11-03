namespace HybridMount.Models.Mounting;

/// <summary>只有经验证的能力才能声明支持。</summary>
public enum CapabilitySupport { Unknown, Supported, Unsupported }
public enum SourceCaseSensitivity { Unknown, Insensitive, Sensitive }
/// <summary>远端存在性与用户本地删除独立；只有完整刷新及明确确认才能进入 Deleted。</summary>
public enum RemotePresence { Unknown, Present, Deleted }
public sealed record FileSourceCapabilities(CapabilitySupport RangeRead, CapabilitySupport ConditionalWrite,
    CapabilitySupport AtomicReplace, CapabilitySupport ChangeNotify, CapabilitySupport StableIds, SourceCaseSensitivity CaseSensitivity);
/// <summary>StableId 与 Version 含义分离；路径使用斜杠相对路径。</summary>
public sealed record SourceEntry(string StableId, string Path, bool IsDirectory, long Size, DateTimeOffset ModifiedUtc, string Version);
public sealed class SourceConflictException(string message) : IOException(message);
/// <summary>移除模式持久化后不可切换；默认值兼容旧版的保留文件流程。</summary>
public enum MountRemovalMode { PreserveFiles, ClearContents }
/// <summary>Working 表示处理内容；Unregistering 表示内容已处理、正在解除注册。</summary>
public enum MountRemovalStage { Working, Unregistering }
/// <summary>旧配置默认未知，按实际注册及索引核对；注册中断不可冒充从未注册。</summary>
public enum MountRegistrationStage { Unknown, NotRegistered, Registering, Registered }
/// <summary>根位置不可原地修改；未完成的移除必须按已保存的模式恢复。</summary>
public sealed record MountDefinition(Guid Id, string Name, string LocalPath, bool AutoConnect = true, bool RemovalPending = false, string? RootIdentity = null, MountRemovalMode RemovalMode = MountRemovalMode.PreserveFiles, MountRemovalStage RemovalStage = MountRemovalStage.Working, FileSourceConfiguration? Source = null, MountRegistrationStage RegistrationStage = MountRegistrationStage.Unknown, bool AutoUploadEnabled = false, bool UploadInitialReviewCompleted = false, bool ContinuousUploadMigrated = false, bool ThumbnailsEnabled = false, bool RemoteAutoDetectEnabled = true, int RemoteIncrementalSeconds = 60, int RemoteFullSeconds = 300, long CredentialRevision = 0);
/// <summary>记录源路径与本地路径的映射；已删除或保护项不会被扫描复活。</summary>
public sealed record MountEntry(string Id, string SourcePath, string LocalPath, string Version, bool IsDirectory, bool Deleted = false, bool Protected = false, bool Materialized = false, RemotePresence RemotePresence = RemotePresence.Unknown);
public sealed record MountSnapshot(MountDefinition Definition, string Status, string Detail, RemoteDetectionStatus? Detection=null)
{
    public string RemoteDetectionDisplay=>Definition.Source?.Kind!=FileSourceKind.WebDav?"":!Definition.RemoteAutoDetectEnabled?"远端自动检测：关闭":
        $"远端自动检测：{Detection?.Mode??"尚未检测"}；增量 {Definition.RemoteIncrementalSeconds} 秒，完整 {Definition.RemoteFullSeconds} 秒；最近成功：{Detection?.LastSuccessUtc?.ToLocalTime().ToString("g")??"尚无"}"+(Detection?.Error is {} error?"；"+error:"");
    public string AutoUploadDisplay=>Definition.Source is null?"旧版源仅支持迁出":"本地新增和修改持续自动上传";
    public string ThumbnailDisplay=>Definition.ThumbnailsEnabled?"缩略图：开启（实验性）；依文件与服务读取能力生成，可能显示普通图标。":"缩略图：关闭";
    public string SourceDisplay => Definition.Source?.Kind switch { FileSourceKind.WebDav=>"WebDAV",FileSourceKind.S3=>"S3 对象存储",FileSourceKind.Smb=>"SMB（预留）",FileSourceKind.Nfs=>"NFS（预留）",_=>"旧版模拟源（仅迁出）" };
}
public sealed record MountItemResult(string Path, string Status, string Message);
public sealed record MountOperationSnapshot(string Id, string Operation, string State, int Completed, int Total,
    IReadOnlyList<MountItemResult> Results, string? Error, Guid? MountId = null, string? ItemPath = null, long BytesTransferred = 0, long TotalBytes = 0, bool? IsDirectory = null, string? RemotePath = null)
{
    /// <summary>区分文件、目录和副本目标；旧历史缺少类型时显示条目，不猜测类型。</summary>
    public string DisplayTarget=>Operation!="upload"?ItemPath??"":$"{(IsDirectory is true?"目录":IsDirectory is false?"文件":"条目")}：{ItemPath}"+
        (RemotePath is not null&&RemotePath!=ItemPath?$" → 远端：{RemotePath}":"");
    public double ProgressPercent=>Operation is "upload" or "remoteUpdate"?(TotalBytes>0?Math.Clamp(100.0*BytesTransferred/TotalBytes,0,100):State=="Completed"?100:0):Total>0?100.0*Completed/Total:0;
    public string ProgressText=>Operation is "upload" or "remoteUpdate"?$"{BytesTransferred:N0} / {TotalBytes:N0} 字节":$"{Completed} / {Total}";
    public string DisplayOperation=>Operation switch{"upload"=>"上传本地更改","remoteUpdate"=>"应用远端更改","keepOnDevice"=>"始终保留","freeSpace"=>"释放空间","refresh"=>"刷新","remove"=>"下载并移除","clearAndRemove"=>"清空并移除",_=>Operation};
    public string DisplayState=>State switch{"AwaitingConfirmation"=>"等待首次确认","Ignored"=>"未纳入上传","Waiting"=>"等待文件可用","Downloading"=>"下载远端快照","Committing"=>"切换占位版本","Hydrating"=>"从快照水合","Pending"=>"待应用","Superseded"=>"已被新版本替代","Prepared"=>"快照已就绪","Sending"=>"上传中","RemoteCommitted"=>"提交本地状态","Conflict"=>"冲突","OutcomeUnknown"=>"结果待核对","Queued"=>"排队中","Running"=>"执行中","Completed"=>"已完成","PartialFailure"=>"部分失败","Failed"=>"失败","Canceled"=>"已取消","Interrupted"=>"已中断",_=>State};
}
