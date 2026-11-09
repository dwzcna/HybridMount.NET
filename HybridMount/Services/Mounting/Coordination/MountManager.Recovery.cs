using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting.Persistence;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>只读合并可见的两模式定义用于排除受管根；不复制、保存或接管另一模式的配置。</summary>
    private IReadOnlyList<MountDefinition> RecoveryManagedDefinitions()=>_roots.Values.Select(x=>x.Definition).Concat(_repository.LoadDefinitions())
        .Concat(new RegistryMountDefinitionStore(ApplicationPaths.MountDefinitionsRegistryPathFor(true)).LoadDefinitions())
        .Concat(new RegistryMountDefinitionStore(ApplicationPaths.MountDefinitionsRegistryPathFor(false)).LoadDefinitions()).ToArray();
    private OrphanedMountRecovery Recovery()=>new(Path.Combine(_repository.DataDirectory,"Recovery"),RecoveryManagedDefinitions,_logger,RemovalShellForTests??_removalShell,ensureRunning:ThrowIfStopping);
    /// <summary>发现遗留根由应用生命周期持有；退出取消并等待，不在UI线程枚举或隐式删除。</summary>
    public Task<IReadOnlyList<OrphanedMountCandidate>> DiscoverOrphanedMountsAsync(CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try{return await Task.Run(()=>Recovery().Discover(cancel),cancel).ConfigureAwait(false);}finally{_configurationGate.Release();}
    },token);
    /// <summary>用户选目录只产生经验证的候选，不登记正式挂载、不启动连接。</summary>
    public Task<OrphanedMountCandidate> InspectOrphanedMountAsync(string path,CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try{return await Task.Run(()=>{cancel.ThrowIfCancellationRequested();return Recovery().Inspect(path);},cancel).ConfigureAwait(false);}finally{_configurationGate.Release();}
    },token);
    /// <summary>用户明确确认后离线清空注销；配置锁防止同目录被新建或导入，退出等待句柄与注销阶段收尾。</summary>
    public Task<OrphanedMountResult> ClearOrphanedMountAsync(OrphanedMountCandidate candidate,CancellationToken token=default,Action<string>? progress=null)=>OwnAsync(async cancel=>
    {
        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try{return await Task.Run(()=>Recovery().ClearAsync(candidate,cancel,progress),cancel).ConfigureAwait(false);}finally{_configurationGate.Release();}
    },token);
}
