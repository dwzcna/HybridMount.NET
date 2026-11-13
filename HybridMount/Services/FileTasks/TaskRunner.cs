namespace HybridMount.Services.FileTasks;

/// <summary>单个已接纳步骤的执行适配器；网络额度和路径屏障沿用TasksQueueService，不另建固定系统线程。</summary>
internal sealed class TaskRunner
{
    internal async Task<T> RunAsync<T>(Func<CancellationToken,Task<T>> execute,CancellationToken token)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        token.ThrowIfCancellationRequested();return await execute(token).ConfigureAwait(false);
    }
}
