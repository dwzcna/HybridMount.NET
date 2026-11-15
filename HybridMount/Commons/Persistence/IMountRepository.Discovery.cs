using HybridMount.Models.Mounting;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal IReadOnlyList<MountEntry> EntriesBySources(Guid mount,IReadOnlyList<string> paths);
    internal MountEntry? EntryBySource(Guid mount,string path);
    internal MountEntry? EntryById(Guid mount, string? id) ;
    internal IReadOnlyList<MountEntry> EntriesInSourceSubtree(Guid mount,string path);
    internal IReadOnlyList<MountEntry> EntriesInLocalSubtree(Guid mount,string path);
    internal void CommitDiscoveryBatch(Guid mount,IReadOnlyList<MountEntry> entries,IReadOnlyList<RemotePendingChange> pending,IReadOnlyList<MountOperationSnapshot> operations);
    internal bool BindColdObservedVersion(Guid mount,MountEntry expected,string version,bool allowColdBinding);
    internal IReadOnlyList<RemotePendingChange> RemotePendingByIds(IReadOnlyList<string> ids);
    internal RemotePendingChange? FindRemotePending(string id);
    internal MountOperationSnapshot? FindOperation(string id);
    internal MountOperationSnapshot? FindRecoverableFileOperation(string id,Guid mount,MountEntry entry,string operation);
    internal bool RemoteDependenciesReady(RemotePendingChange item);
    internal void SaveEntries(IReadOnlyList<MountEntry> entries,Guid mount);
}
