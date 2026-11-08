using HybridMount.Common;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>一次只读比较的授权范围；保留当时会话、任务、元数据和版本，不能转用于其他文件或较新版本。</summary>
public sealed class ConflictComparison
{
    internal MountDefinition Definition{get;init;}=null!;
    internal CloudMountSession Session{get;init;}=null!;
    internal IFileSource Source{get;init;}=null!;
    internal LocalUpload? Upload{get;init;}
    internal MountEntry? Entry{get;init;}
    internal LocalFileStamp? Stamp{get;init;}
    internal SourceEntry? Remote{get;init;}
    internal string PendingJson{get;init;}="";
    public required string TaskId{get;init;}
    public required string Path{get;init;}
    public required string LocalText{get;init;}
    public required string RemoteText{get;init;}
    public required string BaselineText{get;init;}
    public required string LocalRestriction{get;init;}
    public required string RemoteRestriction{get;init;}
    public bool CanUseLocal=>LocalRestriction.Length==0;
    public bool CanUseRemote=>RemoteRestriction.Length==0;
    public string RecoveryPath=>Upload?.Resolution?.PreviousRecoveryPath??Upload?.Resolution?.BackupPath??Upload?.ConflictRecoveryPath??"";
    public DateTimeOffset ObservedUtc{get;init;}=DateTimeOffset.UtcNow;
    /// <summary>仅在已证明为本程序元数据变化后更新执行戳，保持原远端版本与用户比较时刻。</summary>
    internal ConflictComparison Rebased(LocalUpload? upload,LocalFileStamp? stamp)=>new()
    {Definition=Definition,Session=Session,Source=Source,Upload=upload,Entry=Entry,Stamp=stamp,Remote=Remote,PendingJson=PendingJson,
        TaskId=TaskId,Path=Path,LocalText=LocalText,RemoteText=RemoteText,BaselineText=BaselineText,LocalRestriction=LocalRestriction,RemoteRestriction=RemoteRestriction,ObservedUtc=ObservedUtc};
}
