using HybridMount.Models.Protocols.S3;
using HybridMount.Commons.Protocols;
using HybridMount.Infrastructure.Protocols.S3;
using HybridMount.Models.Mounting;
using System.Text.Json;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    void IMountRepository.PersistSqsMessage(string queue,string message,IReadOnlyList<S3EventHint> hints)

 => PersistSqsMessage(queue, message, hints);

}
