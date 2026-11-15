using HybridMount.Models.Mounting;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
namespace HybridMount.Models.Mounting.Renames;

/// <summary>改名退出上传的持久事务；文件身份与原远端版本独立于可清除的任务历史。</summary>
internal sealed record ExcludedRenameIntent(string Id,Guid MountId,string RootPath,string RootIdentity,string Owner,string FileId,
    string OldLocalPath,string LocalPath,string SourcePath,string? EntryId,string? ExpectedVersion,string? UploadId,long UploadGeneration,
    string Stage,DateTimeOffset CreatedUtc,long Revision=1,bool CancelRequested=false,bool RetryRequested=false,string? Error=null,
    DateTimeOffset? IssueUtc=null,string? LocalSha256=null,bool LocalDetached=false,bool MoveStarted=false,DateTimeOffset? CheckedUtc=null,
    string? OriginalVersion=null,bool WaitForUploadResult=false,string? InitialTarget=null,bool ReadOnlyRequested=false,string? Verification=null,string? SourceScope=null,
    string? DestinationPath=null,SourceEntry? MoveReceipt=null,bool RemoteMoved=false,bool MappingRebound=false)
{
    internal static string ScopeFor(MountDefinition definition)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{definition.Source,definition.CredentialRevision})));
    internal bool Finished=>Stage is "Completed" or "Canceled" or "Superseded";
    internal bool NeedsAttention=>!Finished&&IssueUtc is not null;
    internal bool Runnable=>!Finished&&(ReadOnlyRequested||CancelRequested&&(!MoveStarted||CheckedUtc is null)||Stage is "OutcomeUnknown" or "Copied"&&(CheckedUtc is null||RetryRequested)||Stage is not("Failed" or "Conflict" or "OutcomeUnknown" or "Copied")&&(CheckedUtc is null||Stage is not("Queued" or "WaitingUpload")||CheckedUtc.Value.AddSeconds(2)<=DateTimeOffset.UtcNow));
    /// <summary>仅复用展示快照，不将改名意图伪装为可发送的上传记录。</summary>
    internal MountOperationSnapshot Snapshot()
    {
        var state=Stage switch{"WaitingUpload"=>"Waiting","Localizing"=>"Committing","PreparingMove"=>"Queued","Moving"=>"Running","RemoteMoved"=>"Committing","Copied"=>"OutcomeUnknown",_=>Stage};
        var reason=$"本地：{OldLocalPath} → {LocalPath}\n远端：{SourcePath} → {DestinationPath??LocalPath}\n阶段：{StageText}"+(Error is null?"":"\n"+Error)+(Verification is null?"":"\n只读核对："+Verification);
        var attention=NeedsAttention?new UploadAttention(Error??"排除改名收尾尚未完成。",StageText,IssueUtc!.Value,1,state,ShellState:"应用内提示",RetryRequested:RetryRequested,FailureActive:!RetryRequested):null;
        return new(Id,"excludedRename",state,Stage=="Completed"?1:0,1,[new(LocalPath,state,reason)],Error,MountId,LocalPath,IsDirectory:false,RemotePath:DestinationPath??LocalPath,Attention:attention);
    }
    internal string StageText=>Stage switch{"Queued"=>"核对改名意图","WaitingUpload"=>"等待原上传结果","PreparingMove"=>"准备远端改名","Moving"=>"远端条件改名","Copied"=>"目标已复制，源移除待核对","RemoteMoved"=>"绑定新云路径","Downloading"=>"取回缺失内容并核验","Localizing"=>"保留为本地排除文件","OutcomeUnknown"=>"只读核对两端结果","Completed"=>"远端已改名，本地已排除","Superseded"=>"已由后续改名接管","Canceled"=>"已停止收尾，保留当前两端","Conflict"=>"冲突","Failed"=>"失败",_=>Stage};
}
