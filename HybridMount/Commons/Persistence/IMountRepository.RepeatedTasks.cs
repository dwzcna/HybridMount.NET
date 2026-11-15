using HybridMount.Models.Mounting;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal IReadOnlySet<string> CompleteRepeatedRemoteTasks(Guid mount,IReadOnlyList<RemotePendingChange> candidates,Func<string,string,bool> sameVersion);
}
