using HybridMount.Models.Mounting;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal bool HasMaterializedEntries(Guid mount);
    internal IReadOnlyList<MountEntry> MaterializedFiles(Guid mount) ;
    internal MountEntry? EntryByExactSource(Guid mount, string path) ;
    internal MountEntry? EntryForUpload(Guid mount, string? preferredId, string source, string local) ;
    internal bool HasDeletedSourceAncestor(Guid mount, string path);
}
