using HybridMount.Infrastructure.Files;
using HybridMount.Models.Mounting;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    bool IMountRepository.HasMaterializedEntries(Guid mount) => HasMaterializedEntries(mount);
    IReadOnlyList<MountEntry> IMountRepository.MaterializedFiles(Guid mount) => MaterializedFiles(mount);
    MountEntry? IMountRepository.EntryByExactSource(Guid mount, string path) => EntryByExactSource(mount, path);
    MountEntry? IMountRepository.EntryForUpload(Guid mount, string? preferredId, string source, string local) => EntryForUpload(mount, preferredId, source, local);
    bool IMountRepository.HasDeletedSourceAncestor(Guid mount, string path) => HasDeletedSourceAncestor(mount, path);
}
