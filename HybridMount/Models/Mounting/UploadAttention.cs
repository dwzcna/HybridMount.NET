namespace HybridMount.Models.Mounting;

/// <summary>独立于历史和索引的上传问题；展示发布阶段也随原上传JSON持久化，旧记录默认无此字段。</summary>
public sealed record UploadAttention(string Reason,string Stage,DateTimeOffset OccurredUtc,long Generation,
    string LastDisplayState,DateTimeOffset? ResolvedUtc=null,string ShellState="Pending",string? ShellError=null,string? PublishedIdentity=null,bool RetryRequested=false,string? ShellOriginalStamp=null,bool FailureActive=true)
{
    public string NotificationKey=>$"{Generation}:{OccurredUtc.UtcTicks}";
}
/// <summary>上传问题的纯状态规则；重试不抹掉原因，成功或明确取消才解除；普通等待不产生新问题。</summary>
public static class UploadAttentionRules
{
    public static bool NeedsAttention(LocalUpload item)=>item.Attention is {ResolvedUtc:null};
    public static bool IsFailureState(LocalUpload item)=>item.State is "Failed" or "Conflict" or "ResolutionPending" or "OutcomeUnknown" or "WaitingCapacity"||item.State=="RemoteCommitted"&&!string.IsNullOrEmpty(item.Error);
    public static bool IsRetrying(string state)=>state is "Queued" or "Waiting" or "Prepared" or "Sending" or "RemoteCommitted";
    public static bool IsRetryInProgress(string state,UploadAttention? attention)=>attention is {ResolvedUtc:null,FailureActive:false}&&(IsRetrying(state)||state=="OutcomeUnknown");
    /// <summary>同一路径可有原上传和副本问题；单项完成不能清除其余问题，选择最近原因并显示总数。</summary>
    public static string ShellText(IEnumerable<LocalUpload> uploads,string path,string identity)
    {
        var issues=uploads.Where(x=>x.LocalPath==path&&x.ObservedStamp.Split(':')[0]==identity&&NeedsAttention(x)).OrderByDescending(x=>x.Attention!.OccurredUtc).ToArray();
        if(issues.Length==0)return "";var first=issues[0];
        return (issues.Length>1?$"{issues.Length} 个同步问题；":"")+(IsRetryInProgress(first.State,first.Attention)?"正在重试；上次原因：":"")+first.Attention!.Reason;
    }
    /// <summary>持久写入前统一归一化；恢复旧记录沿用原检查／变化时间，不能把启动误报为新失败。</summary>
    public static LocalUpload Normalize(LocalUpload item,bool recovering=false)
    {
        var a=item.Attention;
        if(item.State=="Excluded")return item; // 排除是暂停，保留原问题及发生时间，不当作成功或再次重试。
        if(item.CancelRequested||item.State is "Completed" or "Canceled" or "Ignored")
            return a is {ResolvedUtc:null}?item with{Attention=a with{ResolvedUtc=DateTimeOffset.UtcNow,LastDisplayState=item.State,ShellState="ClearPending",ShellError=null}}:item;
        var failure=IsFailureState(item)&&!(a?.RetryRequested==true&&string.IsNullOrEmpty(item.Error));
        if(failure)
        {
            var reason=string.IsNullOrWhiteSpace(item.Error)?a?.Reason??"上传未完成，请查看详情并重试。":item.Error;
            var newEpisode=a is null||a.ResolvedUtc is not null||a.Generation!=item.Generation||a.Stage!=item.State||!a.FailureActive;
            if(newEpisode)a=new(reason,item.State,recovering?item.LastCheckedUtc??item.ChangedUtc:DateTimeOffset.UtcNow,item.Generation,item.State,PublishedIdentity:a?.PublishedIdentity,ShellOriginalStamp:a?.ShellOriginalStamp);
            else if(a!.Reason!=reason)a=a with{Reason=reason,ShellState="Pending",ShellError=null};
        }
        if(!failure&&a is {ResolvedUtc:null,FailureActive:true})a=a with{FailureActive=false,ShellState="Pending",ShellError=null};
        if(a is {ResolvedUtc:null}&&a.LastDisplayState!=item.State)a=a with{LastDisplayState=item.State,ShellState="Pending",ShellError=null};
        return item with{Attention=a};
    }
}
