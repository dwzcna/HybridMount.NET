using HybridMount.UI.Localization;
using HybridMount.Models.Mounting;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class OrphanedMountCandidatePresentationView(OrphanedMountCandidate value)
{
    private readonly OrphanedMountCandidate _value=value;
    private Guid MountId => _value.MountId;
    private string LocalPath => _value.LocalPath;
    private string RootIdentity => _value.RootIdentity;
    private string SyncRootId => _value.SyncRootId;
    private bool Registered => _value.Registered;
    private string? Error => _value.Error;
    private bool ShellOnly => _value.ShellOnly;
    private bool OrphanedDirectory => _value.OrphanedDirectory;
    private string? NamespaceId => _value.NamespaceId;
    private string? ContentStamp => _value.ContentStamp;
    private bool CanRemove => _value.CanRemove;
public string ActionText=>OrphanedDirectory?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ClearOrphanedPlaceholderFolder", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:ShellOnly?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemoveLegacyEntry", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ClearAndUnregister", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
public string Display=>string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TwoLineDetails", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, LocalPath, (Error??(OrphanedDirectory?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("OrphanedPlaceholderFolderTheOriginalProviderCannotBeVerified", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:ShellOnly?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ShellEntryOnly", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:Registered?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("VerifiedLegacySyncRootOwnedByThisProduct", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("LocalContentClearedUnregistrationCleanupIsPending", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!)));
}
