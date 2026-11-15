using HybridMount.UI.Localization;
namespace HybridMount.UI;
/// <summary>跨协议共用操作术语，集中为后续资源化提供稳定入口；不推断业务严重程度。</summary>
public static class UITerms
{
    public static string CreateAndConnect=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CreateAndConnect", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public static string SaveConnectionAndReconnect=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SaveConnectionAndReconnect", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public static string TestConnection=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TestConnection", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public static string Close=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Close", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public static string NameRequired=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("MountNameRequired", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public static string LocalDirectoryRequired=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("LocalDirectoryRequired", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public static string ServerAddressRequired=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ServerAddressRequired", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
}
