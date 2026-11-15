using HybridMount.Commons.Protocols;
using HybridMount.Commons.Settings;
using HybridMount.Models.Mounting;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository : IRemoteHintJournal, HybridMount.Commons.Tasks.ITaskLifecycleSource
{
    public string DataDirectory{get;}
    public IReadOnlyList<MountDefinition> LoadDefinitions();
    public void SaveDefinition(MountDefinition definition);
    public long CacheLimitBytes {get;set;}
    public void DeleteDefinition(Guid id);
    public void DeleteEntries(Guid mount);
    public IReadOnlyList<MountEntry> Entries(Guid mount);
    internal IReadOnlyList<MountEntry> EntriesAtPaths(Guid mount,IReadOnlyCollection<string> paths);
    public void SaveEntry(Guid mount,MountEntry entry);
    public void SaveOperation(MountOperationSnapshot snapshot);
    public int ClearCompletedOperations();
    public int ClearOperations(TaskHistoryClearMode mode,IReadOnlyCollection<string>? interruptedIds=null);
    internal IReadOnlyList<MountOperationSnapshot> LoadFileOperations(Guid mount);
    public IReadOnlyList<MountOperationSnapshot> LoadOperations();
}
