using HybridMount.Models.Mounting;
using HybridMount.Models.Persistence;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private int _recoveryBatchRunning;
    private ExplorerEntryRecovery ExplorerRecovery(Action<string>? notify = null) => new(Path.Combine(_repository.DataDirectory, "Recovery", "ExplorerEntries"),
        () => RecoveryManagedDefinitions().Concat(ExplorerEntryRecovery.ReadPhysicalDefinitions()).ToArray(), new ExplorerEntryAccess(), new ExplorerEntryPathGuard(), notify ?? (_ => { }));

    /// <summary>应用持有导航检索生命周期；配置与系统注册检查均在后台完成，不连接挂载。</summary>
    internal Task<IReadOnlyList<ExplorerEntryCandidate>> DiscoverExplorerEntriesAsync(CancellationToken token) => OwnAsync<IReadOnlyList<ExplorerEntryCandidate>>(async cancel =>
    {
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try { return await Task.Run(() => ExplorerRecovery().Discover(cancel), cancel).ConfigureAwait(false); }
        finally { _configurationGate.Release(); }
    }, token);

    /// <summary>逐入口配置锁和持久备份；单项失败继续其他项，取消后保留未开始项目。</summary>
    internal Task<IReadOnlyList<ExplorerEntryResult>> RemoveExplorerEntriesAsync(IReadOnlyList<ExplorerEntryCandidate> selected,
        CancellationToken token, Action<int, int, string>? progress = null)
    {
        var captured = selected.DistinctBy(x => x.Key).ToArray();
        if (captured.Length == 0 || captured.Any(x => !x.CanRemove)) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError50"));
        return OwnAsync<IReadOnlyList<ExplorerEntryResult>>(async cancel =>
        {
            if (Interlocked.CompareExchange(ref _recoveryBatchRunning, 1, 0) != 0) throw new IOException(global::HybridMount.Services.Localization.UiText.Legacy("已有一批遗留挂载正在处理。"));
            var results = new List<ExplorerEntryResult>();
            var refreshNeeded = false;
            try
            {
                for (var index = 0; index < captured.Length; index++)
                {
                    var entry = captured[index];
                    if (cancel.IsCancellationRequested) { results.Add(new(entry, false, "NotStarted")); continue; }
                    try
                    {
                        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
                        try
                        {
                            progress?.Invoke(index + 1, captured.Length, entry.Name);
                            results.Add(await Task.Run(() => ExplorerRecovery(_ => refreshNeeded = true).Remove(entry, cancel), cancel).ConfigureAwait(false));
                        }
                        finally { _configurationGate.Release(); }
                    }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { results.Add(new(entry, false, "Canceled", global::HybridMount.Services.Localization.UiText.Legacy("已取消"))); }
                    catch (Exception error) { results.Add(new(entry, false, "Failed", error.Message, OrphanedMountBatch.Code(error))); }
                }
                return results;
            }
            finally
            {
                if (refreshNeeded) CloudFilesNative.NotifyExplorerNavigationChanged();
                Volatile.Write(ref _recoveryBatchRunning, 0);
            }
        }, token);
    }

    /// <summary>只读合并可见的两模式定义用于排除受管根；不复制、保存或接管另一模式的配置。</summary>
    private IReadOnlyList<MountDefinition> RecoveryManagedDefinitions()=>_roots.Values.Select(x=>x.Definition).Concat(_repository.LoadDefinitions())
        .Concat(new RegistryMountDefinitionStore(ApplicationPaths.MountDefinitionsRegistryPathFor(true)).LoadDefinitions())
        .Concat(new RegistryMountDefinitionStore(ApplicationPaths.MountDefinitionsRegistryPathFor(false)).LoadDefinitions()).ToArray();
    private OrphanedMountRecovery Recovery()=>new(Path.Combine(_repository.DataDirectory,"Recovery"),RecoveryManagedDefinitions,_logger,RemovalShellForTests??_removalShell,ensureRunning:ThrowIfStopping);
    private OrphanedDirectoryRecovery OrphanedDirectories()=>new(Path.Combine(_repository.DataDirectory,"Recovery","OrphanedDirectories"),RecoveryManagedDefinitions,ThrowIfStopping);
    /// <summary>发现遗留根由应用生命周期持有；退出取消并等待，不在UI线程枚举或隐式删除。</summary>
    public Task<IReadOnlyList<OrphanedMountCandidate>> DiscoverOrphanedMountsAsync(CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var known=await Task.Run(()=>Recovery().Discover(cancel),cancel).ConfigureAwait(false);
            var manual=await OrphanedDirectories().DiscoverAsync(cancel).ConfigureAwait(false);
            return (IReadOnlyList<OrphanedMountCandidate>)known.Concat(manual).ToArray();
        }finally{_configurationGate.Release();}
    },token);
    /// <summary>在配置锁内先识别两模式归属；预期拒绝不进入原生检查或孤立占位接管。</summary>
    public Task<OrphanedMountInspection> InspectOrphanedMountSelectionAsync(string path,CancellationToken token=default)=>OwnAsync<OrphanedMountInspection>(async cancel=>
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));cancel.ThrowIfCancellationRequested();
            var current=_roots.Values.Select(x=>x.Definition).Concat(_repository.LoadDefinitions());
            var owner=current.FirstOrDefault(x=>MountPathGuard.IsUnder(x.LocalPath,full)||MountPathGuard.IsUnder(full,x.LocalPath));
            if(owner is not null)return new(null,owner,ApplicationPaths.DeploymentScope);
            foreach(var packaged in new[]{true,false})
            {
                var definitions=new RegistryMountDefinitionStore(ApplicationPaths.MountDefinitionsRegistryPathFor(packaged)).LoadDefinitions();
                owner=definitions.FirstOrDefault(x=>MountPathGuard.IsUnder(x.LocalPath,full)||MountPathGuard.IsUnder(full,x.LocalPath));
                if(owner is not null)return new(null,owner,packaged?"Packaged":"Unpackaged");
            }
            var manual=OrphanedDirectories();
            if(manual.HasPending(full))return new(await manual.InspectAsync(full,cancel).ConfigureAwait(false));
            try{return new(await Task.Run(()=>{cancel.ThrowIfCancellationRequested();return Recovery().Inspect(full);},cancel).ConfigureAwait(false));}
            catch(MissingOrphanedMountEvidenceException){return new(await manual.InspectAsync(full,cancel).ConfigureAwait(false));}
        }
        finally{_configurationGate.Release();}
    },token);
    /// <summary>用户选目录只产生经验证的候选，不登记正式挂载、不启动连接。</summary>
    public Task<OrphanedMountCandidate> InspectOrphanedMountAsync(string path,CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var manual=OrphanedDirectories();if(manual.HasPending(path))return await manual.InspectAsync(path,cancel).ConfigureAwait(false);
            try{return await Task.Run(()=>{cancel.ThrowIfCancellationRequested();return Recovery().Inspect(path);},cancel).ConfigureAwait(false);}
            catch(MissingOrphanedMountEvidenceException){return await manual.InspectAsync(path,cancel).ConfigureAwait(false);}
        }finally{_configurationGate.Release();}
    },token);
    /// <summary>用户明确确认后离线清空注销；配置锁防止同目录被新建或导入，退出等待句柄与注销阶段收尾。</summary>
    public Task<OrphanedMountResult> ClearOrphanedMountAsync(OrphanedMountCandidate candidate,CancellationToken token=default,Action<string>? progress=null)=>OwnAsync(async cancel=>
    {
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try{return await Task.Run(()=>candidate.OrphanedDirectory?OrphanedDirectories().ClearAsync(candidate,cancel,progress):Recovery().ClearAsync(candidate,cancel,progress),cancel).ConfigureAwait(false);}finally{_configurationGate.Release();}
    },token);
    /// <summary>应用拥有整批生命周期；逐根加配置锁，取消后等待当前根收尾，重复批量请求拒绝。</summary>
    public Task<OrphanedMountBatchResult> ClearOrphanedMountsAsync(IReadOnlyList<OrphanedMountCandidate> candidates,CancellationToken token=default,Action<OrphanedMountBatchProgress>? progress=null)
    {
        var captured=OrphanedMountBatch.Capture(candidates);
        return OwnAsync(async cancel=>
        {
            if(Interlocked.CompareExchange(ref _recoveryBatchRunning,1,0)!=0)throw new InvalidOperationException("已有一批遗留挂载正在处理。");
            try
            {
                return await OrphanedMountBatch.RunAsync(captured,async(root,owner,report)=>
                {
                    await _configurationGate.WaitAsync(owner).ConfigureAwait(false);
                    try{return await Task.Run(()=>root.OrphanedDirectory?OrphanedDirectories().ClearAsync(root,owner,report):Recovery().ClearAsync(root,owner,report),owner).ConfigureAwait(false);}
                    finally{_configurationGate.Release();}
                },cancel,progress).ConfigureAwait(false);
            }
            finally{Volatile.Write(ref _recoveryBatchRunning,0);}
        },token);
    }

}
