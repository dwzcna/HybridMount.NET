namespace HybridMount.Helpers.UI;
/// <summary>跨协议共用操作术语，集中为后续资源化提供稳定入口；不推断业务严重程度。</summary>
public static class UITerms
{
    public static string CreateAndConnect=>global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("CreateAndConnect", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!;
    public static string SaveConnectionAndReconnect=>global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("SaveConnectionAndReconnect", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!;
    public static string TestConnection=>global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("TestConnection", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!;
    public static string Close=>global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("Close", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!;
    public static string NameRequired=>global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("MountNameRequired", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!;
    public static string LocalDirectoryRequired=>global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("LocalDirectoryRequired", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!;
    public static string ServerAddressRequired=>global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("ServerAddressRequired", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!;
}
