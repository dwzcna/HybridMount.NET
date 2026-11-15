using HybridMount.Commons.Presentation;
using HybridMount.Models.Protocols.S3;
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
public class SourceConflictException(string message) : IOException(message);
/// <summary>只读扫描的观测失效，可重新完整扫描；与真实版本写入冲突独立，不能用于重发写请求。</summary>
public sealed class SourceRescanRequiredException(string message) : SourceConflictException(message);
/// <summary>移除模式持久化后不可切换；默认值兼容旧版的保留文件流程。</summary>
public enum MountRemovalMode { PreserveFiles, ClearContents, PreserveCachedFiles }
/// <summary>Working 表示处理内容；Unregistering 表示内容已处理、正在解除注册。</summary>
public enum MountRemovalStage { Working, Unregistering }
/// <summary>旧配置默认未知，按实际注册及索引核对；注册中断不可冒充从未注册。</summary>
public enum MountRegistrationStage { Unknown, NotRegistered, Registering, Registered }
/// <summary>根位置不可原地修改；未完成的移除必须按已保存的模式恢复。</summary>
public sealed record MountDefinition(Guid Id, string Name, string LocalPath, bool AutoConnect = true, bool RemovalPending = false, string? RootIdentity = null, MountRemovalMode RemovalMode = MountRemovalMode.PreserveFiles, MountRemovalStage RemovalStage = MountRemovalStage.Working, FileSourceConfiguration? Source = null, MountRegistrationStage RegistrationStage = MountRegistrationStage.Unknown, bool AutoUploadEnabled = false, bool UploadInitialReviewCompleted = false, bool ContinuousUploadMigrated = false, bool ThumbnailsEnabled = false, bool RemoteAutoDetectEnabled = true, int RemoteIncrementalSeconds = 60, int RemoteFullSeconds = 300, long CredentialRevision = 0, SqsConfiguration? Sqs = null, bool CapacityAutoDetectEnabled = false, long? MaximumCapacityBytes = null, IReadOnlyList<FileExclusionRule>? ExclusionRules = null)
{
    private readonly FileExclusionRuleList? _exclusionRules=ExclusionRules is null?null:new(ExclusionRules);
    /// <summary>缺失为旧行为；设置时复制规则，列表值相同的配置仍按记录值相等。</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(FileExclusionRuleListConverter))]
    public IReadOnlyList<FileExclusionRule>? ExclusionRules{get=>_exclusionRules;init=>_exclusionRules=value is null?null:new(value);}
}

/// <summary>记录源路径与本地路径的映射；已删除或保护项不会被扫描复活。</summary>
public sealed record MountEntry(string Id, string SourcePath, string LocalPath, string Version, bool IsDirectory, bool Deleted = false, bool Protected = false, bool Materialized = false, RemotePresence RemotePresence = RemotePresence.Unknown);
/// <summary>新建表单一次提交的可选设置，连接前完整保存；不增加配置或备份格式。</summary>
public sealed record MountCreationOptions(bool ThumbnailsEnabled=false,bool RemoteAutoDetectEnabled=true,int RemoteIncrementalSeconds=60,int RemoteFullSeconds=300,bool CapacityAutoDetectEnabled=false,long? MaximumCapacityBytes=null,SqsConfiguration? Sqs=null,SourceCredentials? SqsCredentials=null);
public sealed record MountSnapshot(MountDefinition Definition, string Status, string Detail, RemoteDetectionStatus? Detection=null, bool IsConnected=false, bool IsConnecting=false, bool IsDisconnecting=false, string? ActiveOperation=null, RemoteCapacitySnapshot? Capacity=null,int UploadIssueCount=0,[property:System.Text.Json.Serialization.JsonIgnore] ProtocolCapabilitySnapshot? ProtocolCapabilities=null)
{
    /// <summary>连接状态为稳定代码；未知诊断保持原文，不从显示文字反向猜测资源。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayStatus => ModelPresentation.Render(this,nameof(DisplayStatus));
    public string CapacityDisplay => ModelPresentation.Render(this,nameof(CapacityDisplay));
    public string RemoteDetectionDisplay => ModelPresentation.Render(this,nameof(RemoteDetectionDisplay));
    public string SqsDisplay => ModelPresentation.Render(this,nameof(SqsDisplay));
    public string AutoUploadDisplay => ModelPresentation.Render(this,nameof(AutoUploadDisplay));
    public string ThumbnailDisplay => ModelPresentation.Render(this,nameof(ThumbnailDisplay));
    public string SourceDisplay => ModelPresentation.Render(this,nameof(SourceDisplay));
}
public sealed record MountItemResult(string Path, string Status, string Message)
{
    /// <summary>只在展示时翻译自有状态，持久值与原错误消息保持原文。</summary>
    public string DisplayStatus => ModelPresentation.Render(this,nameof(DisplayStatus));
}
public sealed record MountOperationSnapshot(string Id, string Operation, string State, int Completed, int Total,
    IReadOnlyList<MountItemResult> Results, string? Error, Guid? MountId = null, string? ItemPath = null, long BytesTransferred = 0, long TotalBytes = 0, bool? IsDirectory = null, string? RemotePath = null,UploadAttention? Attention=null,string? RecoveryPath=null)
{
    /// <summary>仅保存导出保护模式，重试仍需重新输入密码；旧记录缺省为空。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public bool? BackupProtected {get;init;}
    /// <summary>本地文件操作的可选证据；旧历史缺失时为null，不改变SQLite表结构或公共IPC。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? OperationEntryId {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? OperationVersion {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? OperationFileId {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? AttemptId {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? FailureStage {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public int? FailureHResult {get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? RetryAfterUtc {get;init;}
    /// <summary>公共IPC沿用旧快照形状；本地操作证据只写历史，不扩展公共v2响应字段。</summary>
    internal MountOperationSnapshot WithoutLocalEvidence()=>this with{OperationEntryId=null,OperationVersion=null,OperationFileId=null,AttemptId=null,FailureStage=null,FailureHResult=null,RetryAfterUtc=null};
    /// <summary>仅供界面使用的内存快照；不改变配置、IPC或任务历史格式。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string MountName {get=>_mountName??global::HybridMount.Models.Localization.ResourceMessage.Create(MountId is null?"Mount":"RemovedMount").Render();init=>_mountName=value;}
    private readonly string? _mountName;
    [System.Text.Json.Serialization.JsonIgnore] public string WorkflowDetails {get;init;}="";
    [System.Text.Json.Serialization.JsonIgnore] public string? RelativeItemPath {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public double BytesPerSecond {get;init;}
    /// <summary>本次处理的单调计时秒数；旧历史默认为0，附加JSON字段不改变表或IPC格式。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] public double DurationSeconds {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public bool HasAverageSpeed=>State=="Completed"&&IsDirectory is false&&Operation is "upload" or "download" or "keepOnDevice"&&double.IsFinite(DurationSeconds)&&DurationSeconds>0;
    [System.Text.Json.Serialization.JsonIgnore] public bool ConflictPending {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public MountingPriority? Priority {get;init;}
    /// <summary>冲突优先于其他错误；依据结构化事务状态，不解析中文提示。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public TaskIssueKind IssueKind=>ConflictPending||State is "Conflict" or "ResolutionPending"||Attention is {ResolvedUtc:null,Stage:"Conflict"}?TaskIssueKind.Conflict:
        Attention is {ResolvedUtc:null}||State is "Failed" or "PartialFailure" or "Interrupted" or "OutcomeUnknown"||Error is not null&&State is not("Completed" or "Canceled" or "Superseded" or "Deferred")?TaskIssueKind.Error:TaskIssueKind.Normal;
    [System.Text.Json.Serialization.JsonIgnore] public string CardOperation => ModelPresentation.Render(this,nameof(CardOperation));
    [System.Text.Json.Serialization.JsonIgnore] public string CardPath => ModelPresentation.Render(this,nameof(CardPath));
    /// <summary>目录与应用远端更改不展示文件字节；精确进度仍保留在详情中。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string CardSchedule => ModelPresentation.Render(this,nameof(CardSchedule));
    [System.Text.Json.Serialization.JsonIgnore] public string CardState => ModelPresentation.Render(this,nameof(CardState));
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset? SubmittedUtc {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset? NextRetryUtc {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public string? QueuePhase {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public string SchedulingText => ModelPresentation.Render(this,nameof(SchedulingText));
    /// <summary>SQLite 首次提交顺序，仅供展示排序，持久记录仍由原表行序决定。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long SubmissionOrder {get;init;}
    /// <summary>当前依赖只用于展示，不写入历史或公共IPC，不覆盖原始错误与冲突授权。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? WaitingReason {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public string? WaitingTaskId {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public string? InspectionMessage {get;init;}
    /// <summary>区分文件、目录和副本目标；旧历史缺少类型时显示条目，不猜测类型。</summary>
    public string DisplayTarget => ModelPresentation.Render(this,nameof(DisplayTarget));
    public double ProgressPercent=>Operation is "upload" or "remoteUpdate" or "download"&&IsDirectory is not true?(TotalBytes>0?Math.Clamp(100.0*BytesTransferred/TotalBytes,0,100):State=="Completed"?100:0):Total>0?100.0*Completed/Total:0;
    public string ProgressText => ModelPresentation.Render(this,nameof(ProgressText));
    /// <summary>悬停保留精确字节；目录任务按条目计数，不把目录显示为零字节文件。</summary>
    public string ProgressDetail => ModelPresentation.Render(this,nameof(ProgressDetail));
    public string DisplayOperation => ModelPresentation.Render(this,nameof(DisplayOperation));
    public string IssueDetails => ModelPresentation.Render(this,nameof(IssueDetails));
    public string DisplayIssueReason => ModelPresentation.Render(this,nameof(DisplayIssueReason));
    /// <summary>当前调度等待及只读核对反馈独立于原始失败原因；查询不清除持久证据。</summary>
    public string DisplayWaitDetails => ModelPresentation.Render(this,nameof(DisplayWaitDetails));
    public string IssueTimeText => ModelPresentation.Render(this,nameof(IssueTimeText));
    public string IssueStage => ModelPresentation.Render(this,nameof(IssueStage));
    public string DisplayState => ModelPresentation.Render(this,nameof(DisplayState));
}

/// <summary>批次占位创建的本地回执；失败项保留准备身份，已完成创建不回滚。</summary>
internal sealed record PlaceholderCreationResult(MountEntry Entry,SourceEntry Metadata,int HResult,long CreateUsn);
