using HybridMount.Models.Mounting;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    public IReadOnlyList<RemoteCollectionCheckpoint> RemoteCollections(Guid mount,string scope);
    public IReadOnlyList<RemoteShadowEntry> RemoteShadow(Guid mount,string scope);
    public IReadOnlyList<RemotePendingChange> RemotePending(Guid mount,string scope);
    public IReadOnlyList<RemotePendingChange> AllRemotePending(Guid mount);
    public void CommitRemoteBatch(Guid mount,string scope,RemoteCollectionCheckpoint checkpoint,IReadOnlyList<RemoteShadowEntry> changed,IReadOnlyList<string> deleted,IReadOnlySet<string>? silent=null,string? scan=null,string? hint=null);
    internal IReadOnlyList<RemotePendingChange> CommitDiscoveredBatch(Guid mount,string scope,RemoteCollectionCheckpoint checkpoint,IReadOnlyList<RemoteShadowEntry> changed,IReadOnlyList<string> deleted,IReadOnlySet<string>? silent=null,string? scan=null,string? hint=null,IReadOnlyDictionary<string,RemoteShadowEntry>? repeated=null,RemoteDetection? detection=null);
    public void SaveRemotePending(RemotePendingChange pending);
    internal RemotePendingChange SaveRemotePendingCurrent(RemotePendingChange pending,bool retry=false,Func<RemotePendingChange,MountOperationSnapshot>? snapshot=null,MountEntry? present=null);
    public void CommitRemotePlaceholder(RemotePendingChange pending,MountEntry? entry);
    public bool HasPendingRemote(Guid mount,string path);
    internal void ActivateRemoteScope(Guid mount,string scope);
}
