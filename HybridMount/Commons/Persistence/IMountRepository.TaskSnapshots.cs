using HybridMount.Models.Mounting.Tasks;
using HybridMount.Models.Mounting;
using System.Diagnostics;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal (long Rows,double Milliseconds,int Queries) LastTaskRead {get;}
    internal TaskStateSnapshot ReadTaskState();
    internal IReadOnlyList<RemotePendingChange> RemoteAncestors(Guid mount,string path);
    internal IReadOnlyList<LocalUpload> UploadsRelated(Guid mount,string path,bool subtree=true);
    internal MountEntry? DeletedSourceAncestor(Guid mount,string path);
}
