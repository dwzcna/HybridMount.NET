using HybridMount.Models.Mounting;
namespace HybridMount.Commons;

/// <summary>定义持久化边界；不探测协议、注册根或启动调度，失败由调用方处理。</summary>
public interface IMountDefinitionStore
{
    /// <summary>读取全部定义；访问或解析失败必须抛出，不能返回伪空集。</summary>
    IReadOnlyList<MountDefinition> LoadDefinitions();
    /// <summary>保存一个完整定义，失败保留此前有效配置。</summary>
    void SaveDefinition(MountDefinition definition);
    /// <summary>只删除指定定义，不访问远端或处理本地文件。</summary>
    void DeleteDefinition(Guid id);
    /// <summary>读取／保存缓存软上限，保持既有值类型及零关闭语义。</summary>
    long CacheLimitBytes {get;set;}
}
