using HybridMount.Models.Mounting;
using System.Globalization;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal void FinishRemoteDetection(string id,string state,string? error=null);
    internal IReadOnlyList<MountOperationSnapshot> RemoteBatches(Guid mount,bool activeTracesOnly=false);
    internal MountOperationSnapshot? FindRemoteBatch(string id);
    internal IReadOnlyList<RemoteBatchMember> RemoteBatchMembers(string id,int offset=0,int limit=256);
    internal IReadOnlyList<string> ChangeRemoteBatch(string id,bool retry);
    internal RemotePendingChange ClassifyRemotePending(RemotePendingChange proposed,bool download);
    internal IReadOnlySet<string> BeginColdCreationBatch(IReadOnlyList<string> ids);
    internal IReadOnlyList<string> BatchIdsForPending(IReadOnlyList<string> ids);
    internal void RecoverRemoteDetections();
}
