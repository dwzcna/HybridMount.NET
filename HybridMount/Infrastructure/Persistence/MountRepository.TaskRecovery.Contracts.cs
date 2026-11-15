using HybridMount.Models.Mounting;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    void IMountRepository.RecoverRemovedRemoteOperations(IReadOnlySet<Guid> activeMounts) => RecoverRemovedRemoteOperations(activeMounts);
}
