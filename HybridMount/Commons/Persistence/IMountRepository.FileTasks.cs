using HybridMount.Models.Runtime;
using System.Text.Json;
namespace HybridMount.Commons.Persistence;

/// <summary>仓库原有操作边界；具体SQLite连接、事务、锁与失败恢复由实现拥有。</summary>
public partial interface IMountRepository
{
    internal event Action<Guid>? TaskJournalChanged;
    internal (bool Created,bool Gap) AcceptTaskIntent(NativeTaskIntent intent);
    internal long AcceptedTaskSequence(Guid mount,string session);
    internal IReadOnlyList<NativeTaskIntent> UnroutedTaskIntents(Guid mount);
    internal void BindTaskIntent(NativeTaskIntent intent,IReadOnlyCollection<string> tasks);
    internal void DeferTaskIntent(NativeTaskIntent intent,string stage);
    internal void FinishInterruptedTaskIntent(NativeTaskIntent intent);
    internal void RecoverFileTaskAttempts();
    internal FileTaskRecord? ReadFileTask(string id);
    internal bool PrepareFileTask(FileTaskRecord task,IReadOnlyList<string> steps);
    internal IReadOnlyList<FileTaskStep> TaskSteps(string id);
    internal string BeginTaskStep(FileTaskRecord task,int ordinal);
    internal void EndTaskStep(FileTaskRecord task,int ordinal,string attempt,TaskVerification result);
    internal void SetFileTaskState(FileTaskRecord task,string state,string stage);
    internal IReadOnlyList<NativeTaskResult> PendingTaskResults(Guid mount);
    internal void AcknowledgeTaskResult(Guid mount,string resultId);
}
