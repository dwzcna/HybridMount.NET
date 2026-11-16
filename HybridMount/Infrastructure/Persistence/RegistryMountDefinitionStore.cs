using HybridMount.Commons.Settings;
using HybridMount.Infrastructure.Settings;
using HybridMount.Infrastructure.Windows;
using HybridMount.Models.Mounting;
using System.Text.Json;
using Microsoft.Win32;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>只负责 HKCU 定义与缓存偏好，保持原键名和值类型；所有失败直接报告，不用默认值回写。</summary>
internal sealed class RegistryMountDefinitionStore(string? registryPath) : IMountDefinitionStore
{
    private readonly string _registry=registryPath??ApplicationPaths.MountDefinitionsRegistryPathFor(WinRegisterService.IsPackaged());
    private readonly object _gate=new();
    private static readonly JsonSerializerOptions CurrentJson=new(){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
    /// <summary>仅读取精确本机定义版本；不能把残缺子键、未知字段或读取失败当空配置。</summary>
    internal MountIndexFormat InspectFormat()
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(_registry);if(key is null||key.SubKeyCount==0)return new("New");
            foreach(var name in key.GetSubKeyNames())
            {
                using var child=key.OpenSubKey(name);if(child is null)return new("ReadError");
                var marker=child.GetValue("MountSchemaVersion");
                if(marker is null)return new("Old");if(marker is not int version)return new("Corrupt");
                if(version<MountDefinitionRules.RegistrySchemaVersion)return new("Old",version);
                if(version>MountDefinitionRules.RegistrySchemaVersion)return new("Newer",version);
                if(child.GetValue("Definition") is not string json)return new("Corrupt",version);
                try{var definition=JsonSerializer.Deserialize<MountDefinition>(json,CurrentJson);if(definition is null)return new("Corrupt",version);MountDefinitionRules.Validate(definition,name);}
                catch(Exception error)when(error is JsonException or ArgumentException or IOException){return new("Corrupt",version,HResult:error.HResult);}
            }
            return new("Current",MountDefinitionRules.RegistrySchemaVersion);
        }
        catch(UnauthorizedAccessException error){return new("AccessDenied",HResult:error.HResult);}
        catch(System.Security.SecurityException error){return new("AccessDenied",HResult:error.HResult);}
        catch(IOException error){return new("ReadError",HResult:error.HResult);}
    }
    internal MountIndexFormat InspectApplicationData(string directory)
    {
        var configuration=InspectFormat();if(!configuration.CanStart)return configuration;
        var index=MountIndexFormat.Inspect(directory);
        return index.State=="New"&&configuration.State=="Current"?new("MissingIndex"):index;
    }
    public void EnsureCurrentFormat()=>InspectFormat().RequireCurrent();
    public IReadOnlyList<MountDefinition> LoadDefinitions()
    {
        lock (_gate)
        {
            EnsureCurrentFormat();
            using var key = Registry.CurrentUser.OpenSubKey(_registry); if (key is null) return [];
            var result = new List<MountDefinition>();
            foreach (var name in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(name);
                if(child?.GetValue("MountSchemaVersion") is not int marker||marker!=MountDefinitionRules.RegistrySchemaVersion)throw new IOException("挂载定义版本在读取期间发生变化。");
                if (child.GetValue("Definition") is not string json) throw new IOException("挂载定义在读取期间发生变化。");
                var definition = JsonSerializer.Deserialize<MountDefinition>(json,CurrentJson) ?? throw new IOException("挂载配置为空。");
                MountDefinitionRules.Validate(definition,name);
                result.Add(definition with{ExclusionRules=definition.ExclusionRules is null?null:Array.AsReadOnly(FileExclusionRules.Validate(definition.ExclusionRules))});
            }
            return result;
        }
    }
    public void SaveDefinition(MountDefinition definition)
    {
        lock (_gate) { EnsureCurrentFormat();MountDefinitionRules.Validate(definition,definition.Id.ToString("N")); using var key = Registry.CurrentUser.CreateSubKey(_registry + "\\" + definition.Id.ToString("N"), true); key.SetValue("Definition", JsonSerializer.Serialize(definition)); key.SetValue("MountSchemaVersion", MountDefinitionRules.RegistrySchemaVersion, RegistryValueKind.DWord); }
    }
    /// <summary>此仓库的全局缓存软上限；新配置默认 10 GiB，0 为关闭。</summary>
    public long CacheLimitBytes
    {
        get { lock(_gate) { using var key=Registry.CurrentUser.OpenSubKey(_registry);return key?.GetValue("CacheLimitBytes") is long value&&value>=0?value:10L*1024*1024*1024; } }
        set { lock(_gate) { using var key=Registry.CurrentUser.CreateSubKey(_registry,true);key.SetValue("CacheLimitBytes",value,RegistryValueKind.QWord); } }
    }
    public void DeleteDefinition(Guid id) { lock (_gate) Registry.CurrentUser.DeleteSubKeyTree(_registry + "\\" + id.ToString("N"), false); }
}
