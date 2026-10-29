namespace HybridMount.Models.Mounting;

/// <summary>只有经验证的能力才能声明支持。</summary>
public enum CapabilitySupport { Unknown, Supported, Unsupported }
public enum SourceCaseSensitivity { Unknown, Insensitive, Sensitive }
public sealed record FileSourceCapabilities(CapabilitySupport RangeRead, CapabilitySupport ConditionalWrite,
    CapabilitySupport AtomicReplace, CapabilitySupport ChangeNotify, CapabilitySupport StableIds, SourceCaseSensitivity CaseSensitivity);
/// <summary>StableId 与 Version 含义分离；路径使用斜杠相对路径。</summary>
public sealed record SourceEntry(string StableId, string Path, bool IsDirectory, long Size, DateTimeOffset ModifiedUtc, string Version);
public sealed class SourceConflictException(string message) : IOException(message);
/// <summary>移除模式持久化后不可切换；默认值兼容旧版的保留文件流程。</summary>
public enum MountRemovalMode { PreserveFiles, ClearContents }
/// <summary>Working 表示处理内容；Unregistering 表示内容已处理、正在解除注册。</summary>
public enum MountRemovalStage { Working, Unregistering }
/// <summary>根位置不可原地修改；未完成的移除必须按已保存的模式恢复。</summary>
public sealed record MountDefinition(Guid Id, string Name, string LocalPath, bool AutoConnect = true, bool RemovalPending = false, string? RootIdentity = null, MountRemovalMode RemovalMode = MountRemovalMode.PreserveFiles, MountRemovalStage RemovalStage = MountRemovalStage.Working, FileSourceConfiguration? Source = null);
/// <summary>记录源路径与本地路径的映射；已删除或保护项不会被扫描复活。</summary>
public sealed record MountEntry(string Id, string SourcePath, string LocalPath, string Version, bool IsDirectory, bool Deleted = false, bool Protected = false, bool Materialized = false);
public sealed record MountSnapshot(MountDefinition Definition, string Status, string Detail)
{
    public string SourceDisplay => Definition.Source?.Kind.ToString() ?? "旧版模拟源（仅迁出）";
}
public sealed record MountItemResult(string Path, string Status, string Message);
public sealed record MountOperationSnapshot(string Id, string Operation, string State, int Completed, int Total,
    IReadOnlyList<MountItemResult> Results, string? Error)
{
    public string ProgressText=>$"{Completed} / {Total}";
    public string DisplayOperation=>Operation switch{"keepOnDevice"=>"始终保留","freeSpace"=>"释放空间","refresh"=>"刷新","remove"=>"下载并移除","clearAndRemove"=>"清空并移除",_=>Operation};
    public string DisplayState=>State switch{"Queued"=>"排队中","Running"=>"执行中","Completed"=>"已完成","PartialFailure"=>"部分失败","Failed"=>"失败","Canceled"=>"已取消","Interrupted"=>"已中断",_=>State};
}
