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
public enum MountRemovalMode { PreserveFiles, ClearContents }
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
public sealed record MountSnapshot(MountDefinition Definition, string Status, string Detail, RemoteDetectionStatus? Detection=null, bool IsConnected=false, bool IsConnecting=false, bool IsDisconnecting=false, string? ActiveOperation=null, RemoteCapacitySnapshot? Capacity=null,int UploadIssueCount=0)
{
    public string CapacityDisplay=>Capacity?.Display??(!CapacityRules.Enabled(Definition)?global::HybridMount.Services.Localization.UiText.Text("Text_6E2647317D86"):Definition.CapacityAutoDetectEnabled?global::HybridMount.Services.Localization.UiText.Text("Text_DBBD93A69189"):global::HybridMount.Services.Localization.UiText.Text("Text_22C72115CBD6",TaskByteFormat.Format(Definition.MaximumCapacityBytes!.Value)));
    public string RemoteDetectionDisplay=>Definition.Source?.Kind is not(FileSourceKind.WebDav or FileSourceKind.S3 or FileSourceKind.Synology)?"":!Definition.RemoteAutoDetectEnabled?global::HybridMount.Services.Localization.UiText.Text("Text_80A50A7D7092"):
         global::HybridMount.Services.Localization.UiText.Text("Text_29B576B21307",Detection?.Mode??global::HybridMount.Services.Localization.UiText.Text("Text_4D2966C43800"),(Definition.Source?.Kind is FileSourceKind.S3 or FileSourceKind.Synology?"":global::HybridMount.Services.Localization.UiText.Text("Text_7B12C2DAAD9B",Definition.RemoteIncrementalSeconds)),Definition.RemoteFullSeconds,Detection?.LastSuccessUtc?.ToLocalTime().ToString("g")??global::HybridMount.Services.Localization.UiText.Text("Text_0BA9585C89B8"),(Detection?.Error is {} error?"；"+error:""));
    public string SqsDisplay=>Definition.Source?.Kind!=FileSourceKind.S3?"":Definition.Sqs?.Enabled!=true?global::HybridMount.Services.Localization.UiText.Text("Text_12166E73CDEA"):!Definition.RemoteAutoDetectEnabled?global::HybridMount.Services.Localization.UiText.Text("Text_814FA76101AE"):
        global::HybridMount.Services.Localization.UiText.Text("Text_F57E977ECDE7",Detection?.QueueMode??global::HybridMount.Services.Localization.UiText.Text("Text_66009C547BED"),Detection?.LastEventUtc?.ToLocalTime().ToString("g")??global::HybridMount.Services.Localization.UiText.Text("Text_0BA9585C89B8"),Detection?.PendingHints??0,(Detection?.QueueError is {} error?"；"+error:""));
    public string AutoUploadDisplay=>Definition.Source is null?global::HybridMount.Services.Localization.UiText.Text("Text_0F63185F79E7"):global::HybridMount.Services.Localization.UiText.Text("Text_73A0558AC622");
    public string ThumbnailDisplay=>Definition.ThumbnailsEnabled?global::HybridMount.Services.Localization.UiText.Text("Text_C6A6D7108D1C"):global::HybridMount.Services.Localization.UiText.Text("Text_4C533F41EF3C");
    public string SourceDisplay => Definition.Source?.Kind switch { FileSourceKind.WebDav=>"WebDAV",FileSourceKind.S3=>global::HybridMount.Services.Localization.UiText.Text("Text_F62DFC6C48E8"),FileSourceKind.Synology=>global::HybridMount.Services.Localization.UiText.Text("Text_A0E1ADC4B7E5"),FileSourceKind.Smb=>global::HybridMount.Services.Localization.UiText.Text("Text_3BD1FA6176CA"),FileSourceKind.Nfs=>global::HybridMount.Services.Localization.UiText.Text("Text_A7E6A95D7992"),_=>global::HybridMount.Services.Localization.UiText.Text("Text_3AEE8AA1D3BA") };
}
public sealed record MountItemResult(string Path, string Status, string Message)
{
    /// <summary>只在展示时翻译自有状态，持久值与原错误消息保持原文。</summary>
    public string DisplayStatus=>global::HybridMount.Services.Localization.UiText.Legacy(Status);
}
public sealed record MountOperationSnapshot(string Id, string Operation, string State, int Completed, int Total,
    IReadOnlyList<MountItemResult> Results, string? Error, Guid? MountId = null, string? ItemPath = null, long BytesTransferred = 0, long TotalBytes = 0, bool? IsDirectory = null, string? RemotePath = null,UploadAttention? Attention=null,string? RecoveryPath=null)
{
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
    [System.Text.Json.Serialization.JsonIgnore] public string MountName {get=>_mountName??global::HybridMount.Services.Localization.UiText.Text(MountId is null?"Text_6FC9C71CB14F":"Text_C2942442A231");init=>_mountName=value;}
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
    [System.Text.Json.Serialization.JsonIgnore] public string CardOperation=>Operation switch{"upload"=>global::HybridMount.Services.Localization.UiText.Text("Text_9E07E3C0532D"),"download" or "keepOnDevice"=>global::HybridMount.Services.Localization.UiText.Text("Text_4673A2306165"),_=>DisplayOperation};
    [System.Text.Json.Serialization.JsonIgnore] public string CardPath
    {
        get{var path=(RelativeItemPath??ItemPath??Results.LastOrDefault()?.Path??"\\").Replace('/','\\');return IsDirectory is true&&path.Length>0?path.TrimEnd('\\')+"\\":path;}
    }
    /// <summary>目录与应用远端更改不展示文件字节；精确进度仍保留在详情中。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string CardSchedule=>(Priority is {} value?$"P{(600-(int)value)/100}":"")+
        (IsDirectory is false&&Operation is "upload" or "download" or "keepOnDevice"?" · "+TaskByteFormat.Format(BytesTransferred)+" / "+TaskByteFormat.Format(TotalBytes):"");
    [System.Text.Json.Serialization.JsonIgnore] public string CardState=>global::HybridMount.Services.Localization.UiText.Text("Text_8391DA809DD9",(HasAverageSpeed?global::HybridMount.Services.Localization.UiText.Text("Text_F66E4F942756",TaskByteFormat.Speed(Math.Max(0,TotalBytes)/DurationSeconds)):BytesPerSecond>0&&State is "Sending" or "Downloading" or "Running"&&Operation is "upload" or "download" or "keepOnDevice"?TaskByteFormat.Speed(BytesPerSecond)+" · ":""),DisplayState);
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset? SubmittedUtc {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset? NextRetryUtc {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public string? QueuePhase {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public string SchedulingText=>Priority is {} priority?global::HybridMount.Services.Localization.UiText.Text("Text_4DC4E0597723",(600-(int)priority)/100,QueuePhase??global::HybridMount.Services.Localization.UiText.Text("Text_473A0089F3A0"),(NextRetryUtc is {} retry?global::HybridMount.Services.Localization.UiText.Text("Text_564385D36F6C",retry.ToLocalTime().ToString("G")):"")):"";
    /// <summary>SQLite 首次提交顺序，仅供展示排序，持久记录仍由原表行序决定。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long SubmissionOrder {get;init;}
    /// <summary>当前依赖只用于展示，不写入历史或公共IPC，不覆盖原始错误与冲突授权。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string? WaitingReason {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public string? WaitingTaskId {get;init;}
    [System.Text.Json.Serialization.JsonIgnore] public string? InspectionMessage {get;init;}
    /// <summary>区分文件、目录和副本目标；旧历史缺少类型时显示条目，不猜测类型。</summary>
    public string DisplayTarget=>Operation=="excludedRename"?global::HybridMount.Services.Localization.UiText.Text("Text_FB1B4EE3098C",ItemPath,RemotePath):Operation!="upload"?ItemPath??"":global::HybridMount.Services.Localization.UiText.Text("Text_B589C733AEEF",(IsDirectory is true?global::HybridMount.Services.Localization.UiText.Text("Text_52DAA71EBC31"):IsDirectory is false?global::HybridMount.Services.Localization.UiText.Text("Text_39932F24FE11"):global::HybridMount.Services.Localization.UiText.Text("Text_FEE60AE594C1")),ItemPath,(RemotePath is not null&&RemotePath!=ItemPath?global::HybridMount.Services.Localization.UiText.Text("Text_ADAB5F3BF6C5",RemotePath):""));
    public double ProgressPercent=>Operation is "upload" or "remoteUpdate"&&IsDirectory is not true?(TotalBytes>0?Math.Clamp(100.0*BytesTransferred/TotalBytes,0,100):State=="Completed"?100:0):Total>0?100.0*Completed/Total:0;
    public string ProgressText=>Operation is "upload" or "remoteUpdate"&&IsDirectory is not true
        ?$"{TaskByteFormat.Format(BytesTransferred)} / {TaskByteFormat.Format(TotalBytes)}":global::HybridMount.Services.Localization.UiText.Text("Text_77CE7125B59F",Completed,Total);
    /// <summary>悬停保留精确字节；目录任务按条目计数，不把目录显示为零字节文件。</summary>
    public string ProgressDetail=>Operation is "upload" or "remoteUpdate"&&IsDirectory is not true
        ?global::HybridMount.Services.Localization.UiText.Text("Text_97DBD0360022",BytesTransferred,TotalBytes):global::HybridMount.Services.Localization.UiText.Text("Text_77CE7125B59F",Completed,Total);
    public string DisplayOperation=>Operation switch{"upload"=>global::HybridMount.Services.Localization.UiText.Text("Text_23FE6EDCB6CD"),"remoteUpdate"=>global::HybridMount.Services.Localization.UiText.Text("Text_E76A52C1441D"),"excludedRename"=>global::HybridMount.Services.Localization.UiText.Text("Text_DC8CC2915933"),"download"=>global::HybridMount.Services.Localization.UiText.Text("Text_4673A2306165"),"keepOnDevice"=>global::HybridMount.Services.Localization.UiText.Text("Text_792BAAFC9179"),"freeSpace"=>global::HybridMount.Services.Localization.UiText.Text("Text_3D1D2F450C37"),"refresh"=>global::HybridMount.Services.Localization.UiText.Text("Text_AEE887434131"),"remove"=>global::HybridMount.Services.Localization.UiText.Text("Text_D2E33E1E22E6"),"clearAndRemove"=>global::HybridMount.Services.Localization.UiText.Text("Text_4CA35F63DD99"),_=>Operation};
    public string IssueDetails=>global::HybridMount.Services.Localization.UiText.Text("Text_8391DA809DD9",global::HybridMount.Services.Localization.UiText.Text("Text_F4DAD8B3E4CF",Id,MountName,SchedulingText,DisplayTarget,DisplayState,IssueStage,IssueTimeText,DisplayWaitDetails,DisplayIssueReason)+(Attention is {} a?global::HybridMount.Services.Localization.UiText.Text("Text_613A439ECFB6",a.ShellState,a.ShellError):Error),(RecoveryPath is {Length:>0}?global::HybridMount.Services.Localization.UiText.Text("Text_8C874AC291D6",RecoveryPath):""));
    private string OwnCommitReason(string? reason)=>State=="RemoteCommitted"&&reason==UploadRules.LocalCommitPendingMessage?global::HybridMount.Services.Localization.UiText.Text("Text_07E49220E577"):reason??"";
    public string DisplayIssueReason=>FailureHResult is {} code?global::HybridMount.Services.Localization.UiText.Text("FileTaskFailure",FailureStage??"",$"0x{code:X8}",global::HybridMount.Services.Localization.UiText.Legacy(Error)):global::HybridMount.Services.Localization.UiText.Legacy(OwnCommitReason(Attention is {ResolvedUtc:null} issue?issue.Reason:Error));
    /// <summary>当前调度等待及只读核对反馈独立于原始失败原因；查询不清除持久证据。</summary>
    public string DisplayWaitDetails=>string.Join("\n",new[]{WaitingReason,InspectionMessage}.Where(x=>!string.IsNullOrEmpty(x)));
    public string IssueTimeText=>Attention is {ResolvedUtc:null} issue?issue.OccurredUtc.ToLocalTime().ToString("g"):"";
    public string IssueStage=>Attention is {ResolvedUtc:null} issue?global::HybridMount.Services.Localization.UiText.Text("Text_84F8745990CF",(issue.Stage switch{"Failed"=>global::HybridMount.Services.Localization.UiText.Text("Text_219481A6DDE7"),"Conflict"=>global::HybridMount.Services.Localization.UiText.Text("Text_E8213DCA8072"),"OutcomeUnknown"=>global::HybridMount.Services.Localization.UiText.Text("Text_1BD3AAB06B8B"),"WaitingCapacity"=>global::HybridMount.Services.Localization.UiText.Text("Text_7E04EC712169"),"RemoteCommitted"=>global::HybridMount.Services.Localization.UiText.Text("Text_6B9CA6BA6D6B"),_=>issue.Stage})):"";
    public string DisplayState=>Operation=="excludedRename"?(State switch{"Waiting"=>global::HybridMount.Services.Localization.UiText.Text("Text_E68F1C0ED374"),"Queued"=>global::HybridMount.Services.Localization.UiText.Text("Text_E798BD8AAE7D"),"Running"=>global::HybridMount.Services.Localization.UiText.Text("Text_28E0433F19BC"),"Downloading"=>global::HybridMount.Services.Localization.UiText.Text("Text_97AEFDCEF758"),"Committing"=>global::HybridMount.Services.Localization.UiText.Text("Text_383D9FB36307"),"OutcomeUnknown"=>global::HybridMount.Services.Localization.UiText.Text("Text_F3D810080ECB"),"Completed"=>global::HybridMount.Services.Localization.UiText.Text("Text_4BD0E1A6ED5A"),"Superseded"=>global::HybridMount.Services.Localization.UiText.Text("Text_C3BAF245356F"),"Failed"=>global::HybridMount.Services.Localization.UiText.Text("Text_111C061D14C1"),"Conflict"=>global::HybridMount.Services.Localization.UiText.Text("Text_1875132781C1"),"Canceled"=>global::HybridMount.Services.Localization.UiText.Text("Text_91793038397C"),_=>State}):global::HybridMount.Services.Localization.UiText.Text("Text_8391DA809DD9",(UploadAttentionRules.IsRetryInProgress(State,Attention)?global::HybridMount.Services.Localization.UiText.Text("Text_8BFF5A44439A"):""),(State switch{"AwaitingConfirmation"=>global::HybridMount.Services.Localization.UiText.Text("Text_2FC79E442B86"),"Ignored"=>global::HybridMount.Services.Localization.UiText.Text("Text_8F40E6007E17"),"Excluded"=>global::HybridMount.Services.Localization.UiText.Text("Text_C6F3C8B82C24"),"Waiting"=>global::HybridMount.Services.Localization.UiText.Text("Text_D719D6371AD6"),"WaitingCapacity"=>global::HybridMount.Services.Localization.UiText.Text("Text_09D8C0615F3E"),"Downloading"=>global::HybridMount.Services.Localization.UiText.Text("Text_09B02099ADDE"),"Committing"=>global::HybridMount.Services.Localization.UiText.Text("Text_FB644ADE7E17"),"Hydrating"=>global::HybridMount.Services.Localization.UiText.Text("Text_E2039AFABE4C"),"Pending"=>global::HybridMount.Services.Localization.UiText.Text("Text_D433988AA349"),"Superseded"=>global::HybridMount.Services.Localization.UiText.Text("Text_243C77994AC6"),"Prepared"=>global::HybridMount.Services.Localization.UiText.Text("Text_66DD1880BBA8"),"Sending"=>global::HybridMount.Services.Localization.UiText.Text("Text_403B055E56F5"),"RemoteCommitted"=>global::HybridMount.Services.Localization.UiText.Text("Text_AD4671D5C48F"),"Conflict"=>global::HybridMount.Services.Localization.UiText.Text("Text_45FB95610363"),"ResolutionPending"=>global::HybridMount.Services.Localization.UiText.Text("Text_44CA7FFC290A"),"OutcomeUnknown"=>global::HybridMount.Services.Localization.UiText.Text("Text_79989E841C63"),"Canceling"=>global::HybridMount.Services.Localization.UiText.Text("TaskCanceling"),"Deferred"=>global::HybridMount.Services.Localization.UiText.Text("TaskDeferred"),"Queued"=>global::HybridMount.Services.Localization.UiText.Text("Text_D6F766F2ADF5"),"Running"=>global::HybridMount.Services.Localization.UiText.Text("Text_5026A63B58CD"),"Completed"=>global::HybridMount.Services.Localization.UiText.Text("Text_F28461BB49C8"),"PartialFailure"=>global::HybridMount.Services.Localization.UiText.Text("Text_7E214ED64AE3"),"Failed"=>global::HybridMount.Services.Localization.UiText.Text("Text_28384D7AFD2E"),"Canceled"=>global::HybridMount.Services.Localization.UiText.Text("Text_A37778F17C5F"),"Interrupted"=>global::HybridMount.Services.Localization.UiText.Text("Text_88CF2DC00C34"),_=>State}));
}

/// <summary>批次占位创建的本地回执；失败项保留准备身份，已完成创建不回滚。</summary>
internal sealed record PlaceholderCreationResult(MountEntry Entry,SourceEntry Metadata,int HResult,long CreateUsn);
