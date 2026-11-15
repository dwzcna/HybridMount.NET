using HybridMount.Commons.Settings;
using HybridMount.Infrastructure.Windows;
namespace HybridMount.Infrastructure.Settings;

/// <summary>沿用当前模式原HKCU键名和DWORD编码，不迁移、不回写读取失败的值。</summary>
public sealed class RegistryApplicationPreferenceStore : IApplicationPreferenceStore
{
    public bool ReadCloseToTray() => WinRegisterService.ReadFlag("CloseToTray", true);
    public void WriteCloseToTray(bool value) => WinRegisterService.WriteFlag("CloseToTray", value);
    public bool ReadCompactLayout() => WinRegisterService.ReadFlag("CompactLayout", false);
    public void WriteCompactLayout(bool value) => WinRegisterService.WriteFlag("CompactLayout", value);
    public object? ReadCloudConcurrency() => WinRegisterService.ReadValue(ApplicationPaths.RegistryPath, "CloudOperationConcurrency");
    public void WriteCloudConcurrency(int value) => WinRegisterService.WriteDword("CloudOperationConcurrency", value);
}
