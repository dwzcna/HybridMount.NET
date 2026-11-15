using HybridMount.Infrastructure.Recovery;
using HybridMount.UI.Localization;
using System.Security.Cryptography;
using System.Text;
using HybridMount.Models.Recovery;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class ExplorerEntryResultPresentationView(ExplorerEntryResult value)
{
    private readonly ExplorerEntryResult _value=value;
    private ExplorerEntryCandidate Entry => _value.Entry;
    private bool Completed => _value.Completed;
    private string Stage => _value.Stage;
    private string? Error => _value.Error;
    private int? HResult => _value.HResult;
public string Display => Entry.Name + "\n" + Entry.LocalPath + "\n" +
        (Completed ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ExplorerEntryRemoved", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : Error ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ExplorerEntryNotStarted", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!) +
        (HResult is {} code ? $" · 0x{code:X8}" : "");
}
