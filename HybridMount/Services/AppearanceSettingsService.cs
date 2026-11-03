using HybridMount.Models;

namespace HybridMount.Services;

/// <summary>仅持有纯设置和订阅委托，不保存Window、XamlRoot或材质控制器。</summary>
/// <remarks>锁序只有设置gate→同步存储；通知在锁外。窗口在关闭/托盘回收时注销其订阅。</remarks>
public sealed class AppearanceSettingsService
{
    private readonly object _gate = new();
    private readonly IAppearancePreferenceStore _store;
    private AppearancePreferences _current = AppearancePreferences.Default;
    private bool _loaded;
    private string? _error;
    /// <summary>生产统一入口，读取原 HKCU／键名；读取失败不会写入默认值。</summary>
    public AppearanceSettingsService():this(new RegistryAppearancePreferenceStore()) { }
    /// <summary>隔离注册表回归入口，与生产使用相同存储实现，不接触正式设置。</summary>
    internal AppearanceSettingsService(string registryPath):this(new RegistryAppearancePreferenceStore(registryPath)) { }
    /// <summary>初始化并立即读取持久偏好；不订阅或保存UI对象。</summary>
    /// <param name="store">同步存储接口，测试可替换隔离存储。</param>
    public AppearanceSettingsService(IAppearancePreferenceStore store)
    { _store = store; Reload(); }
    /// <summary>最后一次成功读取或持久保存的有效快照。</summary>
    /// <value>不可变偏好；存储失败不推进。</value>
    public AppearancePreferences Current { get { lock (_gate) return _current; } }
    /// <summary>访问失败不冒充缺值；未成功读取前禁止覆盖持久偏好。</summary>
    /// <value>是否至少成功读取一次设置。</value>
    public bool IsLoaded { get { lock (_gate) return _loaded; } }
    /// <summary>最后一次存储失败的固定用户提示，不包含底层敏感数据。</summary>
    /// <value>无错误时为null。</value>
    public string? ErrorStatus { get { lock (_gate) return _error; } }
    /// <summary>有效快照成功发布后在锁外通知；订阅方负责UI调度与释放。</summary>
    public event EventHandler? Changed;
    /// <summary>重新读取正式偏好；访问异常保留当前有效快照，不写默认值。</summary>
    /// <returns>本次读取是否成功。</returns>
    public bool Reload()
    {
        lock (_gate)
        {
            try { _current = _store.Read(); _loaded = true; _error = null; }
            catch (Exception error) when (IsStorageFailure(error))
            { _error = "外观设置读取失败，已保留当前有效选择。"; return false; }
        }
        Notify(); return true;
    }
    /// <summary>两项保存及旧值清理成功后才发布设置；失败保留旧正式值供双向VM回滚。</summary>
    /// <param name="candidate">两项完整候选。</param><param name="error">用户可见固定诊断，不拼接底层数据。</param>
    /// <returns>是否已持久成功并发布。</returns>
    public bool TrySave(AppearancePreferences candidate, out string? error)
    {
        lock (_gate)
        {
            if (!_loaded) { error = _error ?? "外观设置尚未读取。"; return false; }
            try
            {
                _ = candidate.ToJson();
                _store.Write(candidate); _current = candidate; _error = null;
            }
            catch (Exception failure) when (IsStorageFailure(failure) || failure is ArgumentOutOfRangeException)
            { error = _error = "外观设置保存失败，已保留原背景和主题。"; return false; }
        }
        error = null; Notify(); return true;
    }
    private static bool IsStorageFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or global::System.Security.SecurityException;
    /// <summary>资源策略回退或单个显示订阅失败不撤销已经成功保存的有效偏好。</summary>
    private void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); }
            catch (Exception error) { global::System.Diagnostics.Trace.TraceWarning($"外观显示订阅失败：{error.GetType().Name}"); }
    }
}

/// <summary>复用现有应用HKCU子键，以两个 DWORD 保存背景和主题；不影响启动／托盘偏好。</summary>
internal sealed class RegistryAppearancePreferenceStore : IAppearancePreferenceStore
{
    internal const string ValueName = "AppearanceSettings";
    internal const string BackdropValueName = "AppearanceBackdrop";
    internal const string ThemeValueName = "AppearanceTheme";
    private readonly string _path;
    internal RegistryAppearancePreferenceStore() : this(ApplicationPaths.RegistryPath) { }
    /// <summary>隔离测试必须指定独立HKCU路径，不读取或删除产品设置。</summary>
    internal RegistryAppearancePreferenceStore(string path) => _path = path;
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
