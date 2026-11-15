using HybridMount.UI.Localization;
using HybridMount.Models.Mounting;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class OrphanedMountInspectionPresentationView(OrphanedMountInspection value)
{
    private readonly OrphanedMountInspection _value=value;
    private OrphanedMountCandidate? Candidate => _value.Candidate;
    private MountDefinition? Managed => _value.Managed;
    private string? OwnerMode => _value.OwnerMode;
public string Message=>Managed is null?"":string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RecoveryManagedDirectory", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, Managed.Name, Managed.LocalPath,         global::HybridMount.UI.Localization.UILanguageService.Messages.GetString(OwnerMode=="Packaged"?"RecoveryModePackaged":"RecoveryModeUnpackaged", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!);
}
