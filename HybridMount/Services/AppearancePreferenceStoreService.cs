using HybridMount.Models;

namespace HybridMount.Services;

/// <summary>复用现有应用HKCU子键，以两个 DWORD 保存背景和主题；不影响启动／托盘偏好。</summary>
public sealed class AppearancePreferenceStoreService : IAppearancePreferenceStore
{
    internal const string ValueName = "AppearanceSettings";
    internal const string BackdropValueName = "AppearanceBackdrop";
    internal const string ThemeValueName = "AppearanceTheme";
    private readonly string _path;
    public AppearancePreferenceStoreService() : this(@"Software\HybridMount.NET") { }
    /// <summary>隔离测试必须指定独立HKCU路径，不读取或删除产品设置。</summary>
    internal AppearancePreferenceStoreService(string path) => _path = path;
    /// <summary>从既有HKCU路径读取，不迁移、不回写默认。</summary>
    /// <returns>独立校验两个字段后的快照。</returns>
    public AppearancePreferences Read()
    {
        var backdrop = WinRegisterService.ReadValue(_path, BackdropValueName);
        var theme = WinRegisterService.ReadValue(_path, ThemeValueName);
        return new(
            backdrop is int backdropCode && backdropCode is >= 0 and <= 4
                ? (AppearanceBackdrop)backdropCode : AppearanceBackdrop.None,
            theme is int themeCode && themeCode is >= 0 and <= 2
                ? (AppearanceTheme)themeCode : AppearanceTheme.System);
    }
    /// <summary>写前校验后保存两个 DWORD 并删除旧 JSON；失败尽力恢复原值和类型。</summary>
    /// <remarks>注册表多值写入不是真正跨值原子事务，异常交调用方处理，读取不会迁移或回写。</remarks>
    /// <param name="preferences">完整且合法的候选偏好。</param>
    public void Write(AppearancePreferences preferences)
    {
        _ = preferences.ToJson();
        WinRegisterService.WriteDwordPair(_path, BackdropValueName, (int)preferences.Backdrop,
            ThemeValueName, (int)preferences.Theme, ValueName);
    }
}
