using HybridMount.Models.Mounting.Recovery;

namespace HybridMount.Services.Shell;

/// <summary>旧索引受限启动时的独立维护所有者；不构造 MountRepository 或任务服务，退出取消并排空。</summary>
internal sealed class ExplorerEntryMaintenance : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Task> _work = [];
    private bool _stopping;
    private Task? _dispose;
    internal Task<T> RunAsync<T>(Func<CancellationToken, T> action, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource cancel;
        lock (_sync)
        {
            if (_stopping) throw new OperationCanceledException();
            token.ThrowIfCancellationRequested();
            cancel = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            _work.Add(done.Task);
        }
        return ExecuteAsync();
        async Task<T> ExecuteAsync()
        {
            try
            {
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                await _gate.WaitAsync(cancel.Token).ConfigureAwait(false);
                try { cancel.Token.ThrowIfCancellationRequested(); return action(cancel.Token); }
                finally { _gate.Release(); }
            }
            finally { cancel.Dispose(); lock (_sync) { _work.Remove(done.Task); done.TrySetResult(); } }
        }
    }
    private static ExplorerEntryRecovery Recovery(Action<string> notify) => new(
        Path.Combine(ApplicationPaths.DataDirectory,"Mounting","Recovery","ExplorerEntries"),
        ExplorerEntryRecovery.ReadPhysicalDefinitions, new ExplorerEntryAccess(), new ExplorerEntryPathGuard(), notify);
    internal Task<IReadOnlyList<ExplorerEntryCandidate>> DiscoverAsync(CancellationToken token) =>
        RunAsync<IReadOnlyList<ExplorerEntryCandidate>>(cancel => Recovery(_ => { }).Discover(cancel), token);
    internal Task<IReadOnlyList<ExplorerEntryResult>> RemoveAsync(IReadOnlyList<ExplorerEntryCandidate> entries,
        CancellationToken token, Action<int,int,string> progress) => RunAsync<IReadOnlyList<ExplorerEntryResult>>(cancel =>
    {
        var captured=entries.DistinctBy(x=>x.Key).ToArray();
        if(captured.Length==0||captured.Any(x=>!x.CanRemove))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError50"));
        var results=new List<ExplorerEntryResult>();var refresh=false;
        try
        {
            for(var index=0;index<captured.Length;index++)
            {
                var entry=captured[index];
                if(cancel.IsCancellationRequested){results.Add(new(entry,false,"NotStarted"));continue;}
                try{progress(index+1,captured.Length,entry.Name);results.Add(Recovery(_=>refresh=true).Remove(entry,cancel));}
                catch(OperationCanceledException)when(cancel.IsCancellationRequested){results.Add(new(entry,false,"Canceled",global::HybridMount.Services.Localization.UiText.Legacy("已取消")));}
                catch(Exception error){results.Add(new(entry,false,"Failed",error.Message,OrphanedMountBatch.Code(error)));}
            }
            return results;
        }
        finally{if(refresh)CloudFilesNative.NotifyExplorerNavigationChanged();}
    },token);
    internal void RequestStop()
    {
        lock(_sync){if(_stopping)return;_stopping=true;}
        _stop.Cancel();
    }
    public ValueTask DisposeAsync()
    {
        lock(_sync)return new(_dispose??=DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        RequestStop();Task[] pending;lock(_sync)pending=_work.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);_gate.Dispose();_stop.Dispose();
    }
}
