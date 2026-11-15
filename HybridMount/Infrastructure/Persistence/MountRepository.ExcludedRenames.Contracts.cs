using HybridMount.Models.Mounting.Renames;
using HybridMount.Models.Mounting;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    MountEntry IMountRepository.RebindExcludedRename(ExcludedRenameIntent intent)

 => RebindExcludedRename(intent);
    void IMountRepository.RetireExcludedRename(ExcludedRenameIntent intent)

 => RetireExcludedRename(intent);
}
