namespace HybridMount.Models.Mounting;

/// <summary>0 保留旧配置的迁出语义；SMB/NFS 仅预留适配器标识。</summary>
public enum FileSourceKind { LegacyMock, WebDav, S3, Smb, Nfs, Synology }
/// <summary>可持久化的非秘密连接参数；凭据通过挂载 ID 独立保存。</summary>
public sealed record FileSourceConfiguration(FileSourceKind Kind, string Endpoint = "", string Bucket = "",
    string Prefix = "", string Region = "us-east-1", bool PathStyle = true, bool AllowHttp = false,
    bool UseDefaultCredentials = false,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? PinnedCertificateSha256 = null);
/// <summary>仅在内存、当前用户 DPAPI 或用户选择的明文／加密备份中存在；禁止写入日志和普通配置。</summary>
public sealed record SourceCredentials(string UserName = "", string Password = "", string AccessKey = "",
    string SecretKey = "", string SessionToken = "");
/// <summary>请求可能已在服务器完成，调用者必须核对结果，不可盲目重试。</summary>
public sealed class SourceOutcomeUnknownException(string operation, Exception inner)
    : IOException($"{operation}结果不确定，请刷新远端并核对后再操作。", inner);

/// <summary>已响应写入的只读核对失败；仅保留有限阶段与类型／码，原始异常消息和响应正文不进入诊断。</summary>
internal sealed class SourceWriteVerificationException:IOException
{
    internal string Stage {get;}
    internal string FailureType {get;}
    internal SourceWriteVerificationException(string stage,Exception error):base($"写入核对阶段={stage}；类型={error.GetType().Name}；HRESULT=0x{error.HResult:X8}",error is SourceConnectionException?error:null)
    {Stage=stage;FailureType=error.GetType().Name;HResult=error.HResult;}
}
/// <summary>服务明确拒绝请求，区别于写入可能已提交但响应丢失；不触发盲目重试。</summary>
public sealed class SourceRequestRejectedException(int status,string message,TimeSpan? retryAfter=null) : IOException(message)
{
    public int HttpStatus { get; }=status;
    public TimeSpan? RetryAfter {get;}=retryAfter;
}
