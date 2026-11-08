namespace HybridMount.Models.Mounting;

/// <summary>显式冲突取舍的持久阶段；备份位于根外，未完成阶段始终屏障自动上传，旧JSON缺失时保持旧行为。</summary>
public sealed record ConflictResolution(string Id,string Direction,string Phase,SourceEntry Remote,LocalFileStamp Original,
    MountEntry Entry,string PendingJson,string BackupPath,string? BackupSha256=null,string? RemotePath=null,string? RemoteSha256=null,
    string? PreviousRecoveryPath=null);
