using HybridMount.Models.Mounting.Renames;
using HybridMount.Models.Mounting;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal LocalRenameNotice? ApplyNativeRename(Guid mount,string id,string oldPath,string target);
    internal void ApplyNativeDeletion(Guid mount,string id);
}
