using HybridMount.UI.Localization;
using HybridMount.Models.Mounting;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class OperationStepSnapshotPresentationView(OperationStepSnapshot value)
{
    private readonly OperationStepSnapshot _value=value;
    private int Ordinal => _value.Ordinal;
    private string Name => _value.Name;
    private string State => _value.State;
    private DateTimeOffset? StartedUtc => _value.StartedUtc;
    private DateTimeOffset? FinishedUtc => _value.FinishedUtc;
    private string? Reason => _value.Reason;
    private long Completed => _value.Completed;
    private long Total => _value.Total;
    private string? Evidence => _value.Evidence;
    private string Glyph => _value.Glyph;
public string DisplayName=>UILanguageService.Messages.GetString(Name,UILanguageService.ResourceCulture)??Name;
public string DisplayState=>UILanguageService.Messages.GetString(State switch{"Completed"=>"Completed","Running"=>"Running","Pending"=>"OperationStepPending","Canceled"=>"Canceled","Conflict"=>"Conflict","Skipped" or "Superseded"=>"TaskResultSkipped","Waiting" or "Deferred" or "Queued"=>"TaskDeferred","Interrupted"=>"Interrupted","OutcomeUnknown"=>"ResultNeedsChecking",_=>"Failed"},UILanguageService.ResourceCulture)!;
}
