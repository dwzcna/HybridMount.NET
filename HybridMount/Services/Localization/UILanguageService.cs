using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HybridMount.Services.Localization;

/// <summary>当前运行模式的语言偏好；写入失败不得发布新语言。</summary>
public interface IUILanguageStore { string? Read(); void Write(string choice); }

/// <summary>不可变的界面语言快照；后台工作可在接纳时保存此快照，不改变区域格式。</summary>
public sealed record UILanguageSelection(string Choice, string Effective)
{
    public bool IsSystem => Choice == "system";
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Effective);
}

/// <summary>管理语言偏好、标准 C# 资源及弱展示订阅。通知交回 UI Dispatcher，日期和数字保持系统区域。</summary>
public sealed class UILanguageService : IDisposable
{
    public static readonly IReadOnlyList<string> Choices = Array.AsReadOnly(new[] { "system", "zh-CN", "en-US", "zh-HK" });
    /// <summary>C# 唯一资源源；调用者直接使用标准 GetString，未提供强类型或 Text 包装。</summary>
    public static ResourceManager Messages { get; } = new("HybridMount.Resources.Messages", typeof(UILanguageService).Assembly);
    private static UILanguageService _active = new(new MemoryUILanguageStore());
    private static readonly object ObserverGate = new();
    private static readonly List<IObserver> Observers = [];
    static UILanguageService() => _active.Changed += ActiveChanged;
    public static UILanguageService Active => Volatile.Read(ref _active);
    /// <summary>显式取得当前已发布的 UI 语言快照；不会使用后台线程继承的过期区域。</summary>
    public static CultureInfo ResourceCulture => Active.Current.Culture;

    private readonly object _gate = new();
    private readonly IUILanguageStore _store;
    private readonly IXamlPropertyProvider? _properties;
    private readonly Func<IReadOnlyList<string>> _windows;
    private readonly Action<Action> _dispatch;
    private readonly Action<UILanguageSelection>? _publish;
    private UILanguageSelection _current = new("system", "zh-CN");
    private bool _loaded, _disposed;
    public UILanguageSelection Current => Volatile.Read(ref _current);
    public bool IsLoaded { get { lock (_gate) return _loaded && !_disposed; } }
    public ResourceMessage? Error { get; private set; }
    public event EventHandler? Changed;

    /// <summary>只读取一次偏好；XAML 提供者只处理 PRI 属性，不能代替 C# 标准资源。</summary>
    public UILanguageService(IUILanguageStore store, IXamlPropertyProvider? properties = null,
        Func<IReadOnlyList<string>>? windows = null, Action<Action>? dispatch = null,
        Action<UILanguageSelection>? publish = null)
    {
        _store = store; _properties = properties; _windows = windows ?? WindowsPreferredLanguages;
        _dispatch = dispatch ?? (action => action()); _publish = publish;
        try
        {
            var choice = Normalize(store.Read());
            _current = new(choice, choice == "system" ? ResolveWindows() : choice); _loaded = true;
        }
        catch (Exception error) when (StorageError(error)) { Error = ResourceMessage.Create("CouldNotReadTheInterfaceLanguageTheCurrentLanguageWasKept"); }
        PublishNative();
    }

    public static string Normalize(string? value) => Choices.FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) ?? "system";
    public static string Resolve(IEnumerable<string> languages)
    {
        foreach (var original in languages)
        {
            var value = original.Replace('_', '-').ToLowerInvariant();
            if (value == "en" || value.StartsWith("en-", StringComparison.Ordinal)) return "en-US";
            if (value == "zh" || value.StartsWith("zh-", StringComparison.Ordinal))
                return value.Split('-').Any(x => x is "hant" or "tw" or "hk" or "mo" or "cht") ? "zh-HK" : "zh-CN";
        }
        return "zh-CN";
    }
    private string ResolveWindows()
    {
        try { return Resolve(_windows()); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return "zh-CN"; }
    }
    public bool TrySave(string choice, out ResourceMessage? error)
    {
        lock (_gate)
        {
            if (_disposed || !_loaded) { error = Error ?? ResourceMessage.Create("TheInterfaceLanguageSettingHasNotBeenLoadedYet"); return false; }
            if (!Choices.Contains(choice)) { error = ResourceMessage.Create("SelectASupportedInterfaceLanguage"); return false; }
            try { _store.Write(choice); }
            catch (Exception failure) when (StorageError(failure)) { error = Error = ResourceMessage.Create("CouldNotSaveTheInterfaceLanguageThePreviousLanguageWasKept"); return false; }
            Volatile.Write(ref _current, new(choice, choice == "system" ? ResolveWindows() : choice)); Error = null;
        }
        error = null; PublishNative(); Notify(); return true;
    }
    public void ReevaluateSystemLanguage()
    {
        lock (_gate)
        {
            if (_disposed || !_current.IsSystem) return;
            var language = ResolveWindows(); if (language == _current.Effective) return;
            Volatile.Write(ref _current, new("system", language));
        }
        PublishNative(); Notify();
    }
    private void PublishNative()
    {
        try { _publish?.Invoke(Current); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("UI language bridge: 0x{0:X8}", error.HResult); }
    }
    private void Notify() => _dispatch(() =>
    {
        if (_disposed) return;
        if (ReferenceEquals(Active, this)) ApplyCulture(Current.Culture);
        if (Changed is { } handlers) foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("UI language observer: 0x{0:X8}", error.HResult); }
    });
    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
    private static bool StorageError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    internal IReadOnlyDictionary<string, string> PropertyResources(string id) => _properties?.PropertyResources(id) ?? new Dictionary<string, string>();
    internal string PropertyText(string key, CultureInfo culture) => _properties?.Read(culture.Name, key) ?? throw new MissingManifestResourceException("XAML resource provider unavailable: " + key);

    /// <summary>应用创建 XAML 前安装；隔离测试作用域结束时还原原 UI 语言，不修改区域文化。</summary>
    public static IDisposable Use(UILanguageService service)
    {
        var previous = Active; var culture = CultureInfo.CurrentUICulture; var defaultCulture = CultureInfo.DefaultThreadCurrentUICulture;
        previous.Changed -= ActiveChanged; Volatile.Write(ref _active, service); service.Changed += ActiveChanged;
        ApplyCulture(service.Current.Culture); NotifyObservers();
        return new Restore(previous, service, culture, defaultCulture);
    }
    private sealed class Restore(UILanguageService previous, UILanguageService installed, CultureInfo culture, CultureInfo? defaultCulture) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            installed.Changed -= ActiveChanged; Volatile.Write(ref _active, previous); previous.Changed += ActiveChanged;
            CultureInfo.CurrentUICulture = culture; CultureInfo.DefaultThreadCurrentUICulture = defaultCulture; NotifyObservers();
        }
    }
    private interface IObserver { bool Invoke(); }
    private sealed class Observer<T>(T target, Action<T> update) : IObserver, IDisposable where T : class
    {
        private readonly WeakReference<T> _target = new(target); private bool _disposed;
        public bool Invoke() { if (_disposed || !_target.TryGetTarget(out var target)) return false; update(target); return true; }
        public void Dispose() { lock (ObserverGate) { _disposed = true; Observers.Remove(this); } }
    }
    public static IDisposable Observe<T>(T target, Action<T> update) where T : class
    { var observer = new Observer<T>(target, update); lock (ObserverGate) Observers.Add(observer); return observer; }
    private static void ActiveChanged(object? sender, EventArgs args) => NotifyObservers();
    private static void NotifyObservers()
    {
        IObserver[] snapshot; lock (ObserverGate) snapshot = Observers.ToArray();
        foreach (var observer in snapshot)
            try { if (!observer.Invoke()) lock (ObserverGate) Observers.Remove(observer); }
            catch (Exception error) { System.Diagnostics.Trace.TraceWarning("UI localized display: 0x{0:X8}", error.HResult); }
    }
    public static IReadOnlyList<string> WindowsPreferredLanguages()
    {
        uint count = 0, size = 0;
        if (!GetUserPreferredUILanguages(8, ref count, null, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (size is 0 or > 32768) return [];
        var buffer = new char[size];
        if (!GetUserPreferredUILanguages(8, ref count, buffer, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; } Changed = null; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserPreferredUILanguages(uint flags, ref uint count, [Out] char[]? buffer, ref uint size);
}

/// <summary>只发布当前包及模式的 UI 语言，两个渠道分别失败不互相阻断。</summary>
internal static class UILanguagePublication
{
    internal static void Publish(UILanguageSelection selection, Func<bool> isPackaged, Action<string> applyOverride,
        Action<UILanguageSelection> publishNative, Action<string, Exception>? reportFailure = null)
    {
        Exception? applicationFailure = null, nativeFailure = null; var stage = "UI language package identity";
        try { if (isPackaged()) { stage = "UI language override"; applyOverride(selection.Effective); } } catch (Exception error) { applicationFailure = error; }
        try { publishNative(selection); } catch (Exception error) { nativeFailure = error; }
        if (applicationFailure is not null) Report(stage, applicationFailure, reportFailure);
        if (nativeFailure is not null) Report("Native UI language summary", nativeFailure, reportFailure);
    }
    private static void Report(string stage, Exception error, Action<string, Exception>? reportFailure)
    { if (reportFailure is not null) reportFailure(stage, error); else System.Diagnostics.Trace.TraceWarning("{0}: 0x{1:X8}", stage, error.HResult); }
}
internal sealed class RegistryUILanguageStore(string path) : IUILanguageStore
{
    public string? Read() => WinRegisterService.ReadValue(path, "UILanguage") as string;
    public void Write(string choice)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, true) ?? throw new IOException("UILanguage store unavailable");
        key.SetValue("UILanguage", choice, RegistryValueKind.String);
    }
}
internal sealed class MemoryUILanguageStore(string? choice = "zh-CN") : IUILanguageStore
{
    private string? _choice = choice; public string? Read() => _choice; public void Write(string value) => _choice = value;
}
