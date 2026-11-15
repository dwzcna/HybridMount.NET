using HybridMount.Infrastructure.Recovery;
using HybridMount.UI.Localization;
using System.Security.Cryptography;
using System.Text;
using HybridMount.Models.Recovery;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class ExplorerEntryCandidatePresentationView(ExplorerEntryCandidate value)
{
    private readonly ExplorerEntryCandidate _value=value;
    private ExplorerEntryKey Key => _value.Key;
    private string Name => _value.Name;
    private string? LocalPath => _value.LocalPath;
    private ExplorerEntryProof? Local => _value.Local;
    private ExplorerEntryProof? External => _value.External;
    private ExplorerPathEvidence? Evidence => _value.Evidence;
    private string? Error => _value.Error;
    private int? HResult => _value.HResult;
    private bool CanRemove => _value.CanRemove;
public string Display => Name + "\n" + (LocalPath ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ExplorerEntryNoPath", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!) + "\n" +
        global::HybridMount.UI.Localization.UILanguageService.Messages.GetString(Key.Mode == "Packaged" ? "RecoveryModePackaged" : "RecoveryModeUnpackaged", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! + " · " +
        (Error ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ExplorerEntryReady", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!) + "\n" + Key.SyncRootId + "\n" + Key.NamespaceId;
}
