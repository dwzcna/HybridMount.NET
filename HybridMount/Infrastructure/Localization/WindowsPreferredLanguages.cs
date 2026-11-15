using System.Runtime.InteropServices;
namespace HybridMount.Infrastructure.Localization;

/// <summary>只读系统用户的首选UI语言；失败保留系统错误，不写应用偏好。</summary>
public static class WindowsPreferredLanguages
{
    public static IReadOnlyList<string> Read()
    {
        uint count = 0, size = 0;
        if (!GetUserPreferredUILanguages(8, ref count, null, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (size is 0 or > 32768) return [];
        var buffer = new char[size];
        if (!GetUserPreferredUILanguages(8, ref count, buffer, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserPreferredUILanguages(uint flags, ref uint count, [Out] char[]? buffer, ref uint size);
}
