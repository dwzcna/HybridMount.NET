
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    public void BeginRemoteScan(Guid mount,string scope,string id);
    public void FailRemoteScan(Guid mount,string scope,string id);
}
