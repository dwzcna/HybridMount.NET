namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private bool _removalBatchActive;
    internal bool RemovalBatchActive {get{lock(_lifetime)return _removalBatchActive;}}
    internal MountRemovalBatchResult? LastRemovalBatch {get;private set;}
    private static bool IsRemovalOperation(string operation)=>operation is "remove" or "removeCached" or "clearAndRemove";
    private static MountRemovalMode RemovalMode(string operation)=>operation switch
    {"clearAndRemove"=>MountRemovalMode.ClearContents,"removeCached"=>MountRemovalMode.PreserveCachedFiles,_=>MountRemovalMode.PreserveFiles};
    private void EnsureNoRemovalBatch()
    {lock(_lifetime)if(_removalBatchActive)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("RemoveAllBusy"));}
    /// <summary>后台只读捕获当前模式全部配置，离线及连接失败的根仍列入确认。</summary>
    internal Task<MountRemovalBatchPlan> InspectAllMountRemovalsAsync(CancellationToken token=default)=>OwnAsync(async cancel=>
    {
        EnsureNoRemovalBatch();await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
        try{return new MountRemovalBatchPlan(Array.AsReadOnly(_roots.Values.Select(root=>root.Definition).OrderBy(item=>item.LocalPath,StringComparer.OrdinalIgnoreCase).ToArray()));}
        finally{_configurationGate.Release();}
    },token);
    /// <summary>确认接纳后由应用拥有，页面离开不取消。配置门禁与快照重验先于逐根队列；各根失败后继续。</summary>
    internal Task<MountRemovalBatchResult> RemoveAllMountsAsync(MountRemovalBatchPlan plan,MountRemovalMode mode)
    {
        if(mode is not(MountRemovalMode.ClearContents or MountRemovalMode.PreserveCachedFiles))throw new ArgumentOutOfRangeException(nameof(mode));
        lock(_lifetime){ThrowIfStopping();EnsureNoRemovalBatch();_removalBatchActive=true;}
        try{return OwnAsync(async token=>
        {
            var held=false;
            try
            {
                await _configurationGate.WaitAsync(token).ConfigureAwait(false);held=true;
                var approved=plan.Mounts.ToDictionary(item=>item.Id);
                if(approved.Count!=_roots.Count||_roots.Values.Any(root=>!approved.TryGetValue(root.Definition.Id,out var expected)||!SameRemovalTarget(expected,root.Definition)))
                    throw new IOException(global::HybridMount.Services.Localization.UiText.Text("RemoveAllSnapshotChanged"));
                var results=new List<MountRemovalBatchItem>();
                foreach(var expected in plan.Mounts)
                {
                    var id=expected.Id;string? task=null;
                    try
                    {
                        if(token.IsCancellationRequested){results.Add(new(id,expected.Name,expected.LocalPath,"Canceled",null));continue;}
                        lock(_lifetime)
                        {
                            if(!_roots.TryGetValue(id,out var root))
                            {results.Add(new(id,expected.Name,expected.LocalPath,"Skipped",null));continue;}
                            if(!SameRemovalTarget(expected,root.Definition)||root.Definition.RemovalPending&&root.Definition.RemovalMode!=mode)
                            {results.Add(new(id,expected.Name,expected.LocalPath,"Skipped",null,global::HybridMount.Services.Localization.UiText.Text("RemoveAllModeChanged")));continue;}
                            task=QueueCore(id,mode==MountRemovalMode.ClearContents?"clearAndRemove":"removeCached",null);
                        }
                        await _jobs[task].Work.ConfigureAwait(false);
                        var outcome=GetOperation(task)??throw new IOException("移除任务记录丢失。");
                        results.Add(new(id,expected.Name,expected.LocalPath,outcome.State,task,outcome.Error??outcome.Results.LastOrDefault(item=>item.Status is "Failed" or "Waiting")?.Message));
                    }
                    catch(Exception error){results.Add(new(id,expected.Name,expected.LocalPath,"Failed",task,error.Message));}
                }
                return LastRemovalBatch=new(Array.AsReadOnly(results.ToArray()));
            }
            finally
            {
                if(held)_configurationGate.Release();
                lock(_lifetime)_removalBatchActive=false;Notify();
            }
        },_stop.Token);}
        catch{lock(_lifetime)_removalBatchActive=false;throw;}
    }
    /// <summary>只接受确认时相同的配置和根身份；正在执行的同模式移除允许推进其阶段。</summary>
    private static bool SameRemovalTarget(MountDefinition expected,MountDefinition current)=>
        expected.Id==current.Id&&expected.LocalPath==current.LocalPath&&expected.RootIdentity==current.RootIdentity&&
        expected.Source==current.Source&&expected.CredentialRevision==current.CredentialRevision;
    /// <summary>失败或取消后显式继续原有移除语义，旧任务历史保留；完成的根不能再次创建任务。</summary>
    private bool RetryRemovalOperation(string id)
    {
        lock(_lifetime)
        {
            if(IsStopping||_removalBatchActive)return false;
            var item=_jobs.GetValueOrDefault(id)?.Snapshot??_repository.FindOperation(id);
            if(item is null||!IsRemovalOperation(item.Operation)||item.State is not("Failed" or "PartialFailure" or "Canceled")||
                item.MountId is not {} mount||!_roots.ContainsKey(mount))return false;
            QueueCore(mount,item.Operation,null);return true;
        }
    }
}
