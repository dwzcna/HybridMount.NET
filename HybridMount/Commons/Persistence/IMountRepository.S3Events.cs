using HybridMount.Models.Protocols.S3;
using HybridMount.Commons.Protocols;
using HybridMount.Models.Mounting;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    public IReadOnlyList<S3EventHint> S3EventHints(Guid mount,string scope);
    internal void PersistSqsMessage(string queue,string message,IReadOnlyList<S3EventHint> hints);
}
