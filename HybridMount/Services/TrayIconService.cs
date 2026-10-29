#if !HYBRIDMOUNT_HEADLESS_TEST
using global::System.ComponentModel;
using global::System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Services;

/// <summary>以原生通知区域图标管理窗口恢复和用户主动退出。</summary>
/// <remarks>使用独立的隐藏顶层窗口接收鼠标及 Explorer 重建消息，不依赖主窗口可见性。</remarks>
internal sealed class TrayIconService : IDisposable
{
    private const uint CallbackMessage = 0x8001, WmLeftDoubleClick = 0x0203, WmRightButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B, WmNull = 0, RestoreCommand = 1000, ExitCommand = 1001;
    private const uint NimAdd = 0, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4;
    private readonly string _className = $"HybridMount.NET.Tray.{Guid.NewGuid():N}";
    private readonly WindowProcedure _procedure;
    private readonly IntPtr _module;
    private readonly uint _taskbarCreated;
    private IntPtr _window;
    private IntPtr _icon;
    private bool _registered;
    private bool _disposed;

    /// <summary>当前 Explorer 是否已成功接收通知区域图标。</summary>
    public bool IsRegistered => _registered && !_disposed;

    /// <summary>用户双击托盘图标时请求显示主窗口。</summary>
    public event Action? RestoreRequested;
    /// <summary>用户选择“终止应用”时请求完成异步退出。</summary>
    public event Action? ExitRequested;

    /// <summary>创建隐藏消息窗口并向 Explorer 注册现有应用图标。</summary>
    public TrayIconService()
    {
        _procedure = WindowProc;
        _module = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = _module,
            ClassName = _className
        };
        try
        {
            if (RegisterClassEx(ref windowClass) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘消息窗口类");
            _window = CreateWindowEx(0, _className, "HybridMount.NET Tray", 0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero, _module, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘消息窗口");
            _icon = LoadAppIcon();
            if (_icon == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法载入托盘图标");
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法订阅资源管理器重建事件");
            AddIcon();
        }
        catch { Dispose(); throw; }
    }

    /// <summary>优先读取随应用发布的 ICO；若旧调试目录缺少文件，则读取 EXE 内嵌图标。</summary>
    /// <returns>由调用方负责销毁的原生图标句柄。</returns>
    private static IntPtr LoadAppIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "HybridMount.ico");
        if (File.Exists(iconPath))
        {
            var fromFile = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x10 | 0x40);
            if (fromFile != IntPtr.Zero) return fromFile;
        }
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return IntPtr.Zero;
        return ExtractIconEx(executable, 0, out var large, out var small, 1) > 0
            ? ChooseIcon(large, small) : IntPtr.Zero;
    }

    /// <summary>从提取出的两个尺寸选择托盘图标，并释放未使用的句柄。</summary>
    /// <param name="large">EXE 的大尺寸图标。</param>
    /// <param name="small">EXE 的小尺寸图标。</param>
    /// <returns>最终由托盘服务持有的图标句柄。</returns>
    private static IntPtr ChooseIcon(IntPtr large, IntPtr small)
    {
        if (small != IntPtr.Zero) { if (large != IntPtr.Zero) DestroyIcon(large); return small; }
        return large;
    }

    /// <summary>将通知图标注册到当前 Explorer 实例。</summary>
    private void AddIcon()
    {
        var data = IconData(NifMessage | NifIcon | NifTip);
        if (!ShellNotifyIcon(NimAdd, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法在 Windows 托盘显示图标");
        _registered = true;
        data.Version = 4;
        ShellNotifyIcon(NimSetVersion, ref data);
    }

    /// <summary>生成通知区域图标参数，始终使用固定 ID 与窗口句柄。</summary>
    /// <param name="flags">本次系统调用携带的字段。</param>
    /// <returns>符合 Shell_NotifyIconW 布局的数据。</returns>
    private NotificationIconData IconData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotificationIconData>(),
        Window = _window,
        Id = 1,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = "HybridMount.NET",
        Info = "",
        InfoTitle = ""
    };

    /// <summary>处理图标双击、右键菜单及 Explorer 重启后的重新注册。</summary>
    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_taskbarCreated != 0 && message == _taskbarCreated && !_disposed)
            {
                try { _registered = false; AddIcon(); }
                catch (Exception error)
                {
                    LifecycleTraceService.Record("重新注册托盘图标失败", error.ToString());
                    RestoreRequested?.Invoke();
                }
                return IntPtr.Zero;
            }
            if (message == CallbackMessage)
            {
                var mouseMessage = (uint)(lParam.ToInt64() & 0xffff);
                if (mouseMessage == WmLeftDoubleClick) RestoreRequested?.Invoke();
                else if (mouseMessage is WmRightButtonUp or WmContextMenu) ShowMenu();
                return IntPtr.Zero;
            }
        }
        catch (Exception error)
        {
            LifecycleTraceService.Record("托盘消息处理失败", error.ToString());
            return IntPtr.Zero;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    /// <summary>显示只有“终止应用”的原生右键菜单。</summary>
    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            if (!AppendMenu(menu, 0, RestoreCommand, "显示主窗口")) return;
            if (!AppendMenu(menu, 0, ExitCommand, "退出 HybridMount.NET")) return;
            if (!GetCursorPos(out var point)) return;
            SetForegroundWindow(_window);
            var selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _window, IntPtr.Zero);
            PostMessage(_window, WmNull, IntPtr.Zero, IntPtr.Zero);
            if (selected == RestoreCommand) RestoreRequested?.Invoke();
            else if (selected == ExitCommand) ExitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }

    /// <summary>按注册、图标和窗口的逆序释放资源；可重复调用。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered) { var data = IconData(0); ShellNotifyIcon(NimDelete, ref data); _registered = false; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (_window != IntPtr.Zero) { DestroyWindow(_window); _window = IntPtr.Zero; }
        UnregisterClass(_className, _module);
        GC.KeepAlive(_procedure);
    }

    /// <summary>隐藏消息窗口的 Win32 回调签名。</summary>
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public IntPtr MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true)] private static extern bool ShellNotifyIcon(uint message, ref NotificationIconData data);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
#endif
