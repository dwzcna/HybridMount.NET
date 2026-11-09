using HybridMount.Models.Mounting;
namespace HybridMount.Commons;

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

/// <summary>完整远端快照能力；所有页面及变化元数据核对完成后才返回，不把分页当增量游标。</summary>
public interface IRemoteSnapshotSource
{
    Task<IReadOnlyList<SourceEntry>> ReadSnapshotAsync(IReadOnlyList<RemoteShadowEntry> previous,CancellationToken token);
}

/// <summary>持久提示的最小只读视图，不暴露队列正文、回执或协议事件字段。</summary>
public interface IRemoteHintJournal
{
    IReadOnlyList<RemotePathHint> PendingRemoteHints(Guid mount,string scope);
}
/// <summary>可选事件提示核对；只返回重新查询后的当前变化，提交及确认仍由公共事务协调。</summary>
public interface IRemoteHintSource
{
    IAsyncEnumerable<RemoteHintChange> ReadHintChangesAsync(IRemoteHintJournal journal,Guid mount,string scope,CancellationToken token);
}
