namespace HybridMount.Models.Mounting.S3;

/// <summary>队列凭据来源独立声明，不假设 S3 权限包含队列消费权限。</summary>
public enum SqsCredentialMode { ReuseS3, DefaultCredentials, SeparateKeys }
/// <summary>可备份的非秘密选项；标准队列默认关闭，事件只加速定期完整对账。</summary>
public sealed record SqsConfiguration(bool Enabled=false,string QueueUrl="",string Region="",SqsCredentialMode CredentialMode=SqsCredentialMode.ReuseS3);
/// <summary>已校验的通知线索，不以事件版本替代 S3 当前元数据；sequencer 只在同一 Key 比较。</summary>
internal sealed record S3EventRecord(string Bucket,string Key,string Sequence,string EventName,string Region,string? VersionId=null);
internal sealed record S3Notification(bool Test,IReadOnlyList<S3EventRecord> Records);
/// <summary>接收回执只在内存存在，不得把 Receipt 写入数据库、备份或日志。</summary>
internal sealed record SqsMessage(string Id,string Body,string Receipt);
/// <summary>持久提示先于 DeleteMessage；本地占用和冲突由独立 remote_pending 重放。</summary>
public sealed record S3EventHint(string Id,Guid MountId,string Scope,string Path,string Bucket,string Sequence,string EventName,
    string State="Pending",string? Error=null,DateTimeOffset? ReceivedUtc=null);
internal sealed class SqsOperationException(int status,string code) : IOException($"SQS 请求失败（HTTP {status}，服务码 {code}）。")
{public int Status {get;}=status;}
