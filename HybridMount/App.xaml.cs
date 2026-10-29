using HybridMount.ViewModels;
using HybridMount.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;

namespace HybridMount;

/// <summary>协调单实例、窗口、通知区域、IPC 与日志；资源在 UI 线程按顺序退出。</summary>
public partial class App : Application
{
    private SingleInstanceService? _instance;
    private TrayIconService? _tray;
    private IpcServerService? _ipc;
    private DispatcherQueue? _dispatcher;
    private bool _exiting;
    private bool _allowClose;
    /// <summary>当前应用的类型化入口。</summary>
    public new static App Current => (App)Application.Current;
    /// <summary>展示与程序集相同的三段产品版本。</summary>
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.1";
    /// <summary>主窗口创建后一直保留到真正退出。</summary>
    public MainWindow? MainWindow { get; private set; }
    /// <summary>应用范围的 SQLite 日志队列。</summary>
    public LoggerService Logger { get; private set; } = null!;
    /// <summary>应用范围的正式外观快照。</summary>
    public AppearanceSettingsService Appearance { get; private set; } = null!;
    /// <summary>设置页共用的外观与生命周期偏好。</summary>
    public SettingsViewModel Settings { get; private set; } = null!;
    /// <summary>加载迁移后的公共 WinUI 资源。</summary>
    public App() => InitializeComponent();

    /// <summary>仅主实例创建托盘与管道；静默启动不激活主窗口。</summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            var silent = Environment.GetCommandLineArgs().Any(value => value.Equals("--tray", StringComparison.OrdinalIgnoreCase))
                || AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
            _instance = new SingleInstanceService();
            if (!_instance.IsPrimary)
            {
                try { await IpcClient.SendAsync(new(IpcProtocol.Version, Guid.NewGuid().ToString(), silent ? "ping" : "showWindow", [])); }
                catch (Exception error) { System.Diagnostics.Trace.TraceWarning($"已有实例激活失败：{error.GetType().Name}"); }
                _instance.Dispose();
                _instance = null;
                Exit();
                return;
            }
            Logger = new LoggerService();
            Appearance = new AppearanceSettingsService(new AppearancePreferenceStoreService());
            Settings = new SettingsViewModel(new AppearanceSettingsViewModel(Appearance), new StartupService(Logger), Logger);
            MainWindow = new MainWindow();
            MainWindow.AppWindow.Closing += OnWindowClosing;
            Appearance.Changed += OnAppearanceChanged;
            Logger.NotificationRequested += OnNotification;
            try
            {
                _tray = new TrayIconService();
                _tray.RestoreRequested += RestoreWindow;
                _tray.ExitRequested += RequestExit;
            }
            catch (Exception error) { Logger.Queue("托盘", "Error", $"托盘不可用，将显示主窗口：{error.Message}"); }
            _ipc = new IpcServerService(Logger);
            _ipc.RequestReceived += OnIpcRequest;
            _ipc.Start();
            Logger.Queue("应用", "Information", $"HybridMount.NET {Version} 已启动；当前版本仅提供占位菜单。");
            if (!silent || _tray?.IsRegistered != true) RestoreWindow();
        }
        catch (Exception error)
        {
            MessageBox(IntPtr.Zero, $"HybridMount.NET 启动失败：\n{error.Message}", "HybridMount.NET", 0x10);
            await ExitAsync();
        }
    }
    private void OnAppearanceChanged(object? sender, EventArgs args) => MainWindow?.ApplyAppearance();
    private void OnNotification(object? sender, LogNotificationEventArgs args) => MainWindow?.ShowNotification(args);
    private void OnIpcRequest(IpcRequest request)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            if (_exiting || request.Operation == "ping") return;
            RestoreWindow();
            if (request.Operation is "freeSpace" or "keepOnDevice")
                MainWindow?.ShowNotification(new("资源管理器", "0.0.1 为占位菜单，请求已记录，未修改文件。", InfoBarSeverity.Informational));
        });
    }
    /// <summary>托盘及手动重复启动恢复现有窗口，不创建第二个窗口。</summary>
    public void RestoreWindow()
    {
        if (_exiting || MainWindow is null) return;
        MainWindow.AppWindow.Show();
        ShowWindow(MainWindow.Handle, 9);
        MainWindow.Activate();
        SetForegroundWindow(MainWindow.Handle);
    }
    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        if (!_exiting && Settings.CloseToTray && _tray?.IsRegistered == true) sender.Hide();
        else RequestExit();
    }
    private async void RequestExit() => await ExitAsync();
    /// <summary>终止连接后排空日志，最后移除托盘、窗口和单实例租约；重复调用无副作用。</summary>
    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        try
        {
            if (_ipc is not null) { _ipc.RequestReceived -= OnIpcRequest; await _ipc.DisposeAsync(); }
        }
        catch (Exception error) { Logger?.Queue("退出", "Error", error.Message); }
        finally
        {
            if (Appearance is not null) Appearance.Changed -= OnAppearanceChanged;
            if (Settings is not null) Settings.Appearance.Dispose();
            MainWindow?.ReleaseResources();
            _tray?.Dispose();
            if (Logger is not null)
            {
                Logger.NotificationRequested -= OnNotification;
                Logger.Queue("应用", "Information", "HybridMount.NET 正在退出。");
                await Logger.DisposeAsync();
            }
            _instance?.Dispose();
            _instance = null;
            _allowClose = true;
            MainWindow?.Close();
            MainWindow = null;
            Exit();
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
