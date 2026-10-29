using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HybridMount.Services;

/// <summary>从参考工程裁剪的当前用户设置存储；保留成对保存、失败恢复及包身份判断。</summary>
internal static class WinRegisterService
{
    private static readonly object Gate = new();
    /// <summary>读取原生值；缺值返回 null，拒绝把访问失败冒充默认设置。</summary>
    internal static object? ReadValue(string path, string name)
    {
        lock (Gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
    }
    /// <summary>保存一个布尔偏好，不影响同一根下其他值。</summary>
    internal static void WriteFlag(string name, bool value)
    {
        lock (Gate)
        {
            using var key = Registry.CurrentUser.CreateSubKey(ApplicationPaths.RegistryPath, writable: true)
                ?? throw new IOException("无法打开当前用户设置。");
            key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("SchemaVersion", 1, RegistryValueKind.DWord);
        }
    }
    /// <summary>读取布尔偏好；无效类型和值采用调用方指定默认。</summary>
    internal static bool ReadFlag(string name, bool defaultValue) =>
        ReadValue(ApplicationPaths.RegistryPath, name) is int value && value is 0 or 1 ? value == 1 : defaultValue;

    /// <summary>在进程锁内保存外观两项，失败尽力恢复原值、类型及旧格式。</summary>
    /// <remarks>注册表多值写入不保证崩溃原子性；调用方仅在完整成功后发布新快照。</remarks>
    internal static void WriteDwordPair(string path, string firstName, int first, string secondName, int second, string obsoleteName)
    {
        lock (Gate)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true)
                ?? throw new IOException("无法打开外观设置。");
            var names = new[] { firstName, secondName, obsoleteName, "SchemaVersion" };
            var existing = key.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var snapshots = names.ToDictionary(name => name, name => (
                Exists: existing.Contains(name), Value: key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames),
                Kind: existing.Contains(name) ? key.GetValueKind(name) : RegistryValueKind.Unknown));
            try
            {
                key.SetValue(firstName, first, RegistryValueKind.DWord);
                key.SetValue(secondName, second, RegistryValueKind.DWord);
                key.SetValue("SchemaVersion", 1, RegistryValueKind.DWord);
                key.DeleteValue(obsoleteName, false);
            }
            catch (Exception original)
            {
                var failures = new List<Exception> { original };
                foreach (var (name, snapshot) in snapshots)
                    try
                    {
                        if (snapshot.Exists) key.SetValue(name, snapshot.Value!, snapshot.Kind);
                        else key.DeleteValue(name, false);
                    }
                    catch (Exception cleanup) { failures.Add(cleanup); }
                if (failures.Count > 1) throw new IOException("设置保存和恢复失败。", new AggregateException(failures));
                throw;
            }
        }
    }
    /// <summary>区分已打包与开发进程；未知错误不当作无包身份。</summary>
    public static bool IsPackaged()
    {
        uint length = 0;
        var error = GetCurrentPackageFullName(ref length, IntPtr.Zero);
        return error switch
        {
            122 or 0 => true,
            15700 => false,
            _ => throw new Win32Exception(error, "无法读取应用包身份。")
        };
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr packageFullName);
}
