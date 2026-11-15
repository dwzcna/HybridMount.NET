using HybridMount.Models.Mounting;
using System.Globalization;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal string BeginOperationRun(string task,string generation,IReadOnlyList<string> steps,bool retry=false);
    internal void AdvanceOperation(string task,string name,string? evidence=null);
    internal void UpdateOperationProgress(string task,string stage,long completed,long total);
    internal void EndOperationRun(string task,string state,string? reason=null);
    internal void RecoverOperationRuns();
    internal void DiscardUnpublishedTrace(string task);
    internal int OperationRunCount(string task);
    internal IReadOnlyList<OperationRunSnapshot> OperationRuns(string task,int offset=0,int limit=64);
}
