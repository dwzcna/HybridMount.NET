using HybridMount.Models.Mounting;
namespace HybridMount.Common;

/// <summary>可选的集合轮询能力；通知能力与增量轮询独立，不改变 IFileSource 读写契约。</summary>
public interface IRemoteChangeSource
{
    Task<bool> SupportsCollectionSyncAsync(string collection,CancellationToken token);
    Task<RemoteChangeBatch> GetCollectionChangesAsync(string collection,string? syncToken,CancellationToken token);
    /// <summary>为明确的弱验证器变化分配观测代次；不得提升其条件写入能力。</summary>
    SourceEntry WithObservation(SourceEntry entry,long observation);
    void RestoreObservations(IEnumerable<RemoteShadowEntry> entries);
    /// <summary>忽略观测代次比较服务元数据线索；只能筛选摘要核对，不能单独证明内容等价。</summary>
    bool IsSameVersionHint(string expected,string observed)=>expected==observed;
    bool HasReliableVersion(SourceEntry entry);
}
