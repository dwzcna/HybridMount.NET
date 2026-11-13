using System.Text.Json;
namespace HybridMount.Models.Runtime;

/// <summary>原生监听意图；路径相对挂载根，代次及序号仅在本次认证连接有效，不携带正文。</summary>
internal sealed record NativeTaskIntent(string TaskId,Guid MountId,string SessionId,long Sequence,string EventKind,string Path,string? OldPath=null)
{
    /// <summary>仅本机索引保存的序号断档补扫标记；重发身份比较不包含此内部推导值。</summary>
    public bool RescanRequired {get;init;}
    /// <summary>只校验有界协议数据，不打开文件、根句柄或数据库。</summary>
    internal void Validate()
    {
        if(!Guid.TryParseExact(TaskId,"N",out _)||MountId==Guid.Empty||!Guid.TryParseExact(SessionId,"N",out _)||Sequence<=0||
            EventKind is not("created" or "changed" or "deleted" or "renamed" or "rescan"))throw new InvalidDataException("任务意图身份或类型无效。");
        ValidatePath(Path,EventKind=="rescan");
        if(EventKind=="renamed"){if(OldPath is null)throw new InvalidDataException("改名缺少原路径。");ValidatePath(OldPath,false);}
        else if(OldPath is not null)throw new InvalidDataException("非改名意图携带原路径。");
    }
    private static void ValidatePath(string value,bool root)
    {
        if(value is null||value.Length>4096||(!root&&value.Length==0)||value.Contains('\\')||value.Contains(':')||value.StartsWith('/')||
            value.Split('/').Any(part=>part is "." or ".."||part.Length==0&&value.Length!=0)||value.Any(c=>char.IsControl(c)))
            throw new InvalidDataException("任务路径不是有效的相对路径。");
    }
}

/// <summary>持久任务步骤和依赖；Verified是唯一能放行后续步骤的状态。</summary>
internal sealed record FileTaskStep(string Id,string TaskId,int Ordinal,string Name,string State,string? DependsOn=null,string? Error=null,int? HResult=null,string? AttemptId=null);
/// <summary>同一文件的展示任务与一次内容代次；失败、取消及未知结果不会被当成核验成功。</summary>
internal sealed record FileTaskRecord(string Id,Guid MountId,string Path,string Operation,long Generation,string State,string SessionId,DateTimeOffset UpdatedUtc,IReadOnlyList<string>? Dependencies=null,string? ParentId=null);
/// <summary>原生结果的持久待发送项；只有对端明确确认后才移除，超时不代表成功。</summary>
internal sealed record NativeTaskResult(string ResultId,string TaskId,Guid MountId,string SessionId,string Stage,string Outcome,bool Final,string? WorkflowId=null);
/// <summary>执行器核验后的业务结果；错误原因保存阶段和HRESULT，不复制秘密或网络正文。</summary>
internal sealed record TaskVerification(string State,string? Stage=null,int? HResult=null)
{
    internal bool Verified=>State=="Completed";
}
