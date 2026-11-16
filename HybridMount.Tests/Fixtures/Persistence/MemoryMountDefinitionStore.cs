using HybridMount.Commons.Settings;
using HybridMount.Models.Mounting;
namespace HybridMount.Tests.Fixtures.Persistence;

/// <summary>纯索引测试显式注入独立定义，不读取产品注册表；每用例自行持有或重用该实例。</summary>
internal sealed class MemoryMountDefinitionStore:IMountDefinitionStore
{
    private readonly Dictionary<Guid,MountDefinition> _definitions=[];
    public IReadOnlyList<MountDefinition> LoadDefinitions()=>_definitions.Values.ToArray();
    public void SaveDefinition(MountDefinition definition)=>_definitions[definition.Id]=definition;
    public void DeleteDefinition(Guid id)=>_definitions.Remove(id);
    public long CacheLimitBytes{get;set;}=10L*1024*1024*1024;
}
