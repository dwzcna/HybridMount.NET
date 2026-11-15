using HybridMount.Models.Mounting;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    LocalUpload IMountRepository.CreateUploadCopy(string id,string destination,Action<LocalUpload> validate)

 => CreateUploadCopy(id, destination, validate);
    void IMountRepository.DeleteUnreferencedUploadSnapshot(string path,Action delete,bool forget)

 => DeleteUnreferencedUploadSnapshot(path, delete, forget);
}
