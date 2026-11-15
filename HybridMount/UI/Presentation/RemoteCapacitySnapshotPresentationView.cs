using HybridMount.UI.Localization;
using HybridMount.UI.Presentation;
using System.Globalization;
using HybridMount.Models.Mounting;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class RemoteCapacitySnapshotPresentationView(RemoteCapacitySnapshot value)
{
    private readonly RemoteCapacitySnapshot _value=value;
    private RemoteCapacity Value => _value.Value;
    private DateTimeOffset? LastSuccessUtc => _value.LastSuccessUtc;
    private DateTimeOffset? LastAttemptUtc => _value.LastAttemptUtc;
    private string? Error => _value.Error;
    private bool IsRefreshing => _value.IsRefreshing;
public string Display=>string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemoteCapacityAvailableUsedTotal", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, Value.Scope, Bytes(Value.AvailableBytes), Bytes(Value.UsedBytes), Bytes(Value.TotalBytes), (LastSuccessUtc is {} time?string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("LastSuccess", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, time.ToLocalTime()):""), (IsRefreshing?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Querying", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:""), (Error is {} error?"\n"+error:""));
private static string Bytes(long? value)=>value is {} bytes?TaskByteFormat.Format(bytes):global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Unknown", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
}
