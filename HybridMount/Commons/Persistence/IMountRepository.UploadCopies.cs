using HybridMount.Models.Mounting;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal LocalUpload CreateUploadCopy(string id,string destination,Action<LocalUpload> validate);
    internal void DeleteUnreferencedUploadSnapshot(string path,Action delete,bool forget=false);
}
