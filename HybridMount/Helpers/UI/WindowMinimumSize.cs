using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace HybridMount.Helpers.UI;

/// <summary>从 XAML 根容器读取逻辑最小尺寸，在窗口线程落实到含边框的原生跟踪尺寸；关闭前幂等注销。</summary>
internal sealed class WindowMinimumSize : IDisposable
{
    private const nuint SubclassId = 0x484D0167;
    private readonly nint _handle;
    private readonly FrameworkElement _root;
    private readonly Subclass _callback;
    private bool _disposed;

    /// <summary>窗口创建后、首次显示前安装回调；失败向调用者报告原生错误。</summary>
    internal WindowMinimumSize(Window window, FrameworkElement root)
    {
        _root = root;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _callback = Message;
        if (!SetWindowSubclass(_handle, _callback, SubclassId, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint Message(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0024 && !_disposed)
        {
            var scale = GetDpiForWindow(window) / 96d;
            var rectangle = new Rect { Right = (int)Math.Ceiling(_root.MinWidth * scale), Bottom = (int)Math.Ceiling(_root.MinHeight * scale) };
            if (AdjustWindowRectExForDpi(ref rectangle, unchecked((uint)GetWindowLongPtr(window, -16).ToInt64()), false,
                unchecked((uint)GetWindowLongPtr(window, -20).ToInt64()), GetDpiForWindow(window)))
            {
                var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                limits.MinTrack.X = Math.Max(limits.MinTrack.X, rectangle.Right - rectangle.Left);
                limits.MinTrack.Y = Math.Max(limits.MinTrack.Y, rectangle.Bottom - rectangle.Top);
                Marshal.StructureToPtr(limits, lParam, false);
            }
        }
        if (message == 0x0082) Dispose();
        return result;
    }

    /// <summary>仅在创建窗口的 UI 线程释放；重复调用不再次注销。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveWindowSubclass(_handle, _callback, SubclassId);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint Subclass(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, Subclass callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, Subclass callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AdjustWindowRectExForDpi(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle, uint dpi);
}
