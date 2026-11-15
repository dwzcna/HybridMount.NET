using HybridMount.Models.Mounting.Tasks;
using HybridMount.Infrastructure.Files;
using HybridMount.Models.Mounting;
using System.Diagnostics;
using System.Text.Json;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    (long Rows,double Milliseconds,int Queries) IMountRepository.LastTaskRead { get => LastTaskRead; }
    TaskStateSnapshot IMountRepository.ReadTaskState()

 => ReadTaskState();
    IReadOnlyList<RemotePendingChange> IMountRepository.RemoteAncestors(Guid mount,string path)

 => RemoteAncestors(mount, path);
    IReadOnlyList<LocalUpload> IMountRepository.UploadsRelated(Guid mount,string path,bool subtree)

 => UploadsRelated(mount, path, subtree);
    MountEntry? IMountRepository.DeletedSourceAncestor(Guid mount,string path)

 => DeletedSourceAncestor(mount, path);
}
