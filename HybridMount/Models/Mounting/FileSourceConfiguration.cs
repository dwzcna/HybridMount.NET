namespace HybridMount.Models.Mounting;

/// <summary>0 保留旧配置的迁出语义；SMB/NFS 仅预留适配器标识。</summary>
public enum FileSourceKind { LegacyMock, WebDav, S3, Smb, Nfs }
/// <summary>可持久化的非秘密连接参数；凭据通过挂载 ID 独立保存。</summary>
public sealed record FileSourceConfiguration(FileSourceKind Kind, string Endpoint = "", string Bucket = "",
    string Prefix = "", string Region = "us-east-1", bool PathStyle = true, bool AllowHttp = false,
    bool UseDefaultCredentials = false);
/// <summary>仅在当前用户 DPAPI 密文或内存中存在；禁止写入日志和普通配置。</summary>
public sealed record SourceCredentials(string UserName = "", string Password = "", string AccessKey = "",
    string SecretKey = "", string SessionToken = "");
/// <summary>请求可能已在服务器完成，调用者必须核对结果，不可盲目重试。</summary>
public sealed class SourceOutcomeUnknownException(string operation, Exception inner)
    : IOException($"{operation}结果不确定，请刷新远端并核对后再操作。", inner);
