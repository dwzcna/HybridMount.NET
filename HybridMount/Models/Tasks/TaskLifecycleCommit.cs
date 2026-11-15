using HybridMount.Models.Mounting;
namespace HybridMount.Models.Tasks;

/// <summary>一次已提交的业务展示变更；Revision仅在本进程递增，整批原子应用，不持久化或进入IPC。</summary>
public sealed record TaskLifecycleCommit(long Revision, IReadOnlyList<TaskLifecycleEntry> Entries, string? Error = null);

/// <summary>Attempt为单调尝试代次；Removed仅表示内部成员合并或归属变化，不代表取消业务。</summary>
public sealed record TaskLifecycleEntry(MountOperationSnapshot Task, long Attempt, bool IsTerminal, bool Removed = false);
