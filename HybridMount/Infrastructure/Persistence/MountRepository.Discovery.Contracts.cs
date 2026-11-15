using HybridMount.Infrastructure.Files;
using HybridMount.Models.Mounting;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    IReadOnlyList<MountEntry> IMountRepository.EntriesBySources(Guid mount,IReadOnlyList<string> paths) => EntriesBySources(mount, paths);
    MountEntry? IMountRepository.EntryBySource(Guid mount,string path) => EntryBySource(mount, path);
    MountEntry? IMountRepository.EntryById(Guid mount, string? id) => EntryById(mount, id);
    IReadOnlyList<MountEntry> IMountRepository.EntriesInSourceSubtree(Guid mount,string path) => EntriesInSourceSubtree(mount, path);
    IReadOnlyList<MountEntry> IMountRepository.EntriesInLocalSubtree(Guid mount,string path) => EntriesInLocalSubtree(mount, path);
    void IMountRepository.CommitDiscoveryBatch(Guid mount,IReadOnlyList<MountEntry> entries,IReadOnlyList<RemotePendingChange> pending,IReadOnlyList<MountOperationSnapshot> operations) => CommitDiscoveryBatch(mount, entries, pending, operations);
    bool IMountRepository.BindColdObservedVersion(Guid mount,MountEntry expected,string version,bool allowColdBinding) => BindColdObservedVersion(mount, expected, version, allowColdBinding);
    IReadOnlyList<RemotePendingChange> IMountRepository.RemotePendingByIds(IReadOnlyList<string> ids) => RemotePendingByIds(ids);
    RemotePendingChange? IMountRepository.FindRemotePending(string id) => FindRemotePending(id);
    MountOperationSnapshot? IMountRepository.FindOperation(string id) => FindOperation(id);
    MountOperationSnapshot? IMountRepository.FindRecoverableFileOperation(string id,Guid mount,MountEntry entry,string operation) => FindRecoverableFileOperation(id, mount, entry, operation);
    bool IMountRepository.RemoteDependenciesReady(RemotePendingChange item) => RemoteDependenciesReady(item);
    void IMountRepository.SaveEntries(IReadOnlyList<MountEntry> entries,Guid mount) => SaveEntries(entries, mount);
}
