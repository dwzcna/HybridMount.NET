using HybridMount.Models.Mounting;
using System.Text.Json;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    IReadOnlySet<string> IMountRepository.CompleteRepeatedRemoteTasks(Guid mount,IReadOnlyList<RemotePendingChange> candidates,Func<string,string,bool> sameVersion) => CompleteRepeatedRemoteTasks(mount, candidates, sameVersion);
}
