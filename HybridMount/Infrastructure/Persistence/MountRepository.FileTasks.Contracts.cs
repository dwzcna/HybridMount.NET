using HybridMount.Models.Runtime;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using HybridMount.Commons.Persistence;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>内部门面只转发已有仓库入口，保持锁、事务及异常行为。</summary>
public sealed partial class MountRepository
{
    event Action<Guid>? IMountRepository.TaskJournalChanged { add => TaskJournalChanged += value; remove => TaskJournalChanged -= value; }
    (bool Created,bool Gap) IMountRepository.AcceptTaskIntent(NativeTaskIntent intent)

 => AcceptTaskIntent(intent);
    long IMountRepository.AcceptedTaskSequence(Guid mount,string session)

 => AcceptedTaskSequence(mount, session);
    IReadOnlyList<NativeTaskIntent> IMountRepository.UnroutedTaskIntents(Guid mount)

 => UnroutedTaskIntents(mount);
    void IMountRepository.BindTaskIntent(NativeTaskIntent intent,IReadOnlyCollection<string> tasks)

 => BindTaskIntent(intent, tasks);
    void IMountRepository.DeferTaskIntent(NativeTaskIntent intent,string stage)

 => DeferTaskIntent(intent, stage);
    void IMountRepository.FinishInterruptedTaskIntent(NativeTaskIntent intent)

 => FinishInterruptedTaskIntent(intent);
    void IMountRepository.RecoverFileTaskAttempts()

 => RecoverFileTaskAttempts();
    FileTaskRecord? IMountRepository.ReadFileTask(string id)

 => ReadFileTask(id);
    bool IMountRepository.PrepareFileTask(FileTaskRecord task,IReadOnlyList<string> steps)

 => PrepareFileTask(task, steps);
    IReadOnlyList<FileTaskStep> IMountRepository.TaskSteps(string id)

 => TaskSteps(id);
    string IMountRepository.BeginTaskStep(FileTaskRecord task,int ordinal)

 => BeginTaskStep(task, ordinal);
    void IMountRepository.EndTaskStep(FileTaskRecord task,int ordinal,string attempt,TaskVerification result)

 => EndTaskStep(task, ordinal, attempt, result);
    void IMountRepository.SetFileTaskState(FileTaskRecord task,string state,string stage)

 => SetFileTaskState(task, state, stage);
    IReadOnlyList<NativeTaskResult> IMountRepository.PendingTaskResults(Guid mount)

 => PendingTaskResults(mount);
    void IMountRepository.AcknowledgeTaskResult(Guid mount,string resultId)

 => AcknowledgeTaskResult(mount, resultId);
}
