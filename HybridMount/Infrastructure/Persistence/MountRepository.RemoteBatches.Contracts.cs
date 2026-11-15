using HybridMount.Models.Mounting;
using System.Globalization;
using Microsoft.Data.Sqlite;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    void IMountRepository.FinishRemoteDetection(string id,string state,string? error) => FinishRemoteDetection(id, state, error);
    IReadOnlyList<MountOperationSnapshot> IMountRepository.RemoteBatches(Guid mount,bool activeTracesOnly) => RemoteBatches(mount, activeTracesOnly);
    MountOperationSnapshot? IMountRepository.FindRemoteBatch(string id) => FindRemoteBatch(id);
    IReadOnlyList<RemoteBatchMember> IMountRepository.RemoteBatchMembers(string id,int offset,int limit) => RemoteBatchMembers(id, offset, limit);
    IReadOnlyList<string> IMountRepository.ChangeRemoteBatch(string id,bool retry) => ChangeRemoteBatch(id, retry);
    RemotePendingChange IMountRepository.ClassifyRemotePending(RemotePendingChange proposed,bool download) => ClassifyRemotePending(proposed, download);
    IReadOnlySet<string> IMountRepository.BeginColdCreationBatch(IReadOnlyList<string> ids) => BeginColdCreationBatch(ids);
    IReadOnlyList<string> IMountRepository.BatchIdsForPending(IReadOnlyList<string> ids) => BatchIdsForPending(ids);
    void IMountRepository.RecoverRemoteDetections() => RecoverRemoteDetections();
}
