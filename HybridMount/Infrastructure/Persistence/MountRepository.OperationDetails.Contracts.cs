using HybridMount.Models.Mounting;
using System.Globalization;
using Microsoft.Data.Sqlite;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    string IMountRepository.BeginOperationRun(string task,string generation,IReadOnlyList<string> steps,bool retry) => BeginOperationRun(task, generation, steps, retry);
    void IMountRepository.AdvanceOperation(string task,string name,string? evidence) => AdvanceOperation(task, name, evidence);
    void IMountRepository.UpdateOperationProgress(string task,string stage,long completed,long total) => UpdateOperationProgress(task, stage, completed, total);
    void IMountRepository.EndOperationRun(string task,string state,string? reason) => EndOperationRun(task, state, reason);
    void IMountRepository.RecoverOperationRuns() => RecoverOperationRuns();
    void IMountRepository.DiscardUnpublishedTrace(string task) => DiscardUnpublishedTrace(task);
    int IMountRepository.OperationRunCount(string task) => OperationRunCount(task);
    IReadOnlyList<OperationRunSnapshot> IMountRepository.OperationRuns(string task,int offset,int limit) => OperationRuns(task, offset, limit);
}
