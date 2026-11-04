using HybridMount.Models.Mounting;
namespace HybridMount.Common;

/// <summary>可选的只读完整用量统计；不改变普通文件源读取、路径映射或服务器配额接口。</summary>
public interface IRemoteUsageSource
{
    /// <summary>只返回完整统计；失败或取消必须抛出，不得将部分合计作为完整值。</summary>
    Task<RemoteUsage> GetRemoteUsageAsync(CancellationToken token=default);
}
