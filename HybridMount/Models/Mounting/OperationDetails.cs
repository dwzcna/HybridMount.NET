using HybridMount.Services.Localization;
namespace HybridMount.Models.Mounting;

/// <summary>只读展示阶段；Name为语义资源键，Reason保留业务原文。不得用于授予写入或放行依赖。</summary>
public sealed record OperationStepSnapshot(int Ordinal,string Name,string State,DateTimeOffset? StartedUtc,DateTimeOffset? FinishedUtc,string? Reason,long Completed=0,long Total=0,string? Evidence=null)
{
    public string DisplayName=>UILanguageService.Messages.GetString(Name,UILanguageService.ResourceCulture)??Name;
    public string Glyph=>State switch{"Completed"=>"\uE930","Running"=>"\uE72A","Pending"=>"\uE712","Canceled"=>"\uE711","Conflict"=>"\uE7BA","Skipped" or "Superseded"=>"\uE893","Waiting" or "Deferred" or "Queued"=>"\uE823","Interrupted"=>"\uE769","OutcomeUnknown"=>"\uE9CE",_=>"\uEA39"};
    public string DisplayState=>UILanguageService.Messages.GetString(State switch{"Completed"=>"Completed","Running"=>"Running","Pending"=>"OperationStepPending","Canceled"=>"Canceled","Conflict"=>"Conflict","Skipped" or "Superseded"=>"TaskResultSkipped","Waiting" or "Deferred" or "Queued"=>"TaskDeferred","Interrupted"=>"Interrupted","OutcomeUnknown"=>"ResultNeedsChecking",_=>"Failed"},UILanguageService.ResourceCulture)!;
}
/// <summary>同一任务的一次展示尝试，重试保留此前所有真实阶段。</summary>
public sealed record OperationRunSnapshot(string Id,string TaskId,int Attempt,string Generation,string State,DateTimeOffset StartedUtc,DateTimeOffset? FinishedUtc,string? Reason,IReadOnlyList<OperationStepSnapshot> Steps);
/// <summary>按固定任务ID获取的详情快照；成员有界分页，关闭详情不改变后台所有权。</summary>
public sealed record OperationDetailsSnapshot(MountOperationSnapshot Task,IReadOnlyList<OperationRunSnapshot> Runs,IReadOnlyList<MountItemResult> Members,int MemberCount,int Offset,int RunCount=0,int RunOffset=0);
/// <summary>一次逻辑检测，分页、子目录和256项发布批次共用身份，连接身份不复用。</summary>
internal sealed record RemoteDetection(string Id,Guid MountId,string Scope,string Connection);
internal sealed record RemoteBatchMember(string PendingId,string BatchId,int Ordinal,string Path,string Category,string State,string? Reason,string Action);
