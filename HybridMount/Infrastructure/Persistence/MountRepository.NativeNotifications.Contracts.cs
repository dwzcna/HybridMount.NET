using HybridMount.Models.Mounting.Renames;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    LocalRenameNotice? IMountRepository.ApplyNativeRename(Guid mount,string id,string oldPath,string target)

 => ApplyNativeRename(mount, id, oldPath, target);
    void IMountRepository.ApplyNativeDeletion(Guid mount,string id)

 => ApplyNativeDeletion(mount, id);
}
