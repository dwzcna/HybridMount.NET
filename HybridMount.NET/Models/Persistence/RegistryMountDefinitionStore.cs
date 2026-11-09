using System.Text.Json;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
using Microsoft.Win32;
namespace HybridMount.Models.Persistence;

/// <summary>只负责 HKCU 定义与缓存偏好，保持原键名和值类型；所有失败直接报告，不用默认值回写。</summary>
internal sealed class RegistryMountDefinitionStore(string? registryPath) : IMountDefinitionStore
{
    private readonly string _registry=registryPath??ApplicationPaths.MountDefinitionsRegistryPathFor(WinRegisterService.IsPackaged());
    private readonly object _gate=new();
    public IReadOnlyList<MountDefinition> LoadDefinitions()
    {
        lock (_gate)
        {
            using var key = Registry.CurrentUser.OpenSubKey(_registry); if (key is null) return [];
            var result = new List<MountDefinition>();
            foreach (var name in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(name);
                if (child?.GetValue("Definition") is not string json) continue;
                var definition = JsonSerializer.Deserialize<MountDefinition>(json) ?? throw new IOException("挂载配置为空。");
                MountDefinitionRules.Validate(definition,name);
                result.Add(definition with{ExclusionRules=definition.ExclusionRules is null?null:Array.AsReadOnly(FileExclusionRules.Validate(definition.ExclusionRules))});
            }
            return result;
        }
    }
    public void SaveDefinition(MountDefinition definition)
    {
        lock (_gate) { using var key = Registry.CurrentUser.CreateSubKey(_registry + "\\" + definition.Id.ToString("N"), true); key.SetValue("Definition", JsonSerializer.Serialize(definition)); key.SetValue("MountSchemaVersion", MountDefinitionRules.RegistrySchemaVersion, RegistryValueKind.DWord); }
    }
    /// <summary>此仓库的全局缓存软上限；旧配置默认 10 GiB，0 为关闭。</summary>
    public long CacheLimitBytes
    {
        get { lock(_gate) { using var key=Registry.CurrentUser.OpenSubKey(_registry);return key?.GetValue("CacheLimitBytes") is long value&&value>=0?value:10L*1024*1024*1024; } }
        set { lock(_gate) { using var key=Registry.CurrentUser.CreateSubKey(_registry,true);key.SetValue("CacheLimitBytes",value,RegistryValueKind.QWord); } }
    }
    public void DeleteDefinition(Guid id) { lock (_gate) Registry.CurrentUser.DeleteSubKeyTree(_registry + "\\" + id.ToString("N"), false); }
}
