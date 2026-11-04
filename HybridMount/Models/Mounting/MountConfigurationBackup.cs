namespace HybridMount.Models.Mounting;

/// <summary>可迁移的挂载配置；只保存用户选项，不包含挂载 ID、同步根身份或运行索引。</summary>
/// <remarks>凭据仅供加密编解码及重新保存到 DPAPI；不得记录整个对象。</remarks>
public sealed record MountBackupItem(string Name, string LocalPath, FileSourceConfiguration Source,
    SourceCredentials Credentials, bool AutoConnect = true, bool ThumbnailsEnabled = false, bool RemoteAutoDetectEnabled = true, int RemoteIncrementalSeconds = 60, int RemoteFullSeconds = 300, SqsConfiguration? Sqs = null, SourceCredentials? SqsCredentials = null, bool CapacityAutoDetectEnabled = false, long? MaximumCapacityBytes = null)
{
    /// <summary>避免调试或日志展开记录中的凭据。</summary>
    public override string ToString() => "挂载配置备份条目";
}

/// <summary>加密载荷的版本化白名单；SchemaVersion 与容器版本分别验证。</summary>
public sealed record MountConfigurationBackup(int SchemaVersion, DateTimeOffset ExportedUtc, IReadOnlyList<MountBackupItem> Mounts);

/// <summary>导入时的本机目录和选择状态；原目录只作为提示，不能用备份中的运行身份注册。</summary>
public sealed record MountImportItem(MountBackupItem Configuration, string LocalPath, bool Selected = true);

/// <summary>不携带秘密的逐项校验或提交结果；Index 对应本次输入顺序。</summary>
public sealed record MountImportResult(int Index, string State, string Message, Guid? MountId = null);

/// <summary>已保存项目不会因取消回滚；Canceled 表示后续项目没有继续提交。</summary>
public sealed record MountImportBatchResult(IReadOnlyList<MountImportResult> Results, bool Canceled);
