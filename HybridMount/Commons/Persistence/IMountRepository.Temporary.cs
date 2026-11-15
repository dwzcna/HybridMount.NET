using HybridMount.Models.Protocols.WebDAV;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    public void SaveTemporary(RemoteTemporaryItem item);
    public IReadOnlyList<RemoteTemporaryItem> TemporaryItems(Guid mount,string scope);
    public void CompleteTemporary(Guid mount,string scope,string path);
}
