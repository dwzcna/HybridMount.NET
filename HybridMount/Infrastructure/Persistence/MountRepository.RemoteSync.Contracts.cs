using HybridMount.Models.Mounting;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    IReadOnlyList<RemotePendingChange> IMountRepository.CommitDiscoveredBatch(Guid mount,string scope,RemoteCollectionCheckpoint checkpoint,IReadOnlyList<RemoteShadowEntry> changed,IReadOnlyList<string> deleted,IReadOnlySet<string>? silent,string? scan,string? hint,IReadOnlyDictionary<string,RemoteShadowEntry>? repeated,RemoteDetection? detection) => CommitDiscoveredBatch(mount, scope, checkpoint, changed, deleted, silent, scan, hint, repeated, detection);
    RemotePendingChange IMountRepository.SaveRemotePendingCurrent(RemotePendingChange pending,bool retry,Func<RemotePendingChange,MountOperationSnapshot>? snapshot,MountEntry? present) => SaveRemotePendingCurrent(pending, retry, snapshot, present);
    void IMountRepository.ActivateRemoteScope(Guid mount,string scope) => ActivateRemoteScope(mount, scope);
}
