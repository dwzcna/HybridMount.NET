using HybridMount.Services.Localization;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private static string[] OperationStages(string operation)=>operation switch
    {
        "connect"=>["OperationStageAuthenticate","OperationStageInitialize","OperationStageReadChanges","OperationStageSummarize"],
        "upload"=>["OperationStagePrepareSnapshot","OperationStageTransfer","OperationStageVerifyContent","OperationStageCommitLocal","OperationStageCommitIndex"],
        "remoteBatch"=>["OperationStageReadChanges","OperationStageVerifyLocal","OperationStageApplyPlaceholder","OperationStageCommitIndex","OperationStageSummarize"],
        "remoteUpdate"=>["OperationStageVerifyLocal","OperationStageApplyPlaceholder","OperationStageCommitIndex"],
        "download"=>["OperationStagePrepareSnapshot","OperationStageTransfer","OperationStageVerifyContent","OperationStageApplyVersion","OperationStageCommitIndex"],
        "nativeRead"=>["OperationStageTransfer","OperationStageVerifyResult"],
        "freeSpace"=>["OperationStageVerifyLocal","OperationStageReleaseContent","OperationStageVerifyResult"],
        "keepOnDevice"=>["OperationStageVerifyLocal","OperationStageTransfer","OperationStageVerifyResult"],
        "refresh"=>["OperationStageReadChanges","OperationStageVerifyLocal","OperationStageSummarize"],
        "remove" or "removeCached" or "clearAndRemove"=>["OperationStageVerifyLocal","OperationStageProcessFiles","OperationStageUnregister","OperationStageVerifyResult"],
        "capabilityProbe"=>["OperationStageDetectCapabilities","OperationStageCleanup","OperationStageVerifyResult"],
        _=>["OperationStageExecute","OperationStageVerifyResult"]
    };
    private void BeginTrace(string id,string operation,string generation,bool retry=false)=>_repository.BeginOperationRun(id,generation,OperationStages(operation),retry);
    private void Stage(string id,string name,string? evidence=null)=>_repository.AdvanceOperation(id,name,evidence);
    private void EndTrace(string id,string state,string? reason=null)=>_repository.EndOperationRun(id,state,reason);
    /// <summary>读取详情只访问当前任务、展示尝试和一页成员；不会检测远端或重排任务。</summary>
    internal OperationDetailsSnapshot? ReadOperationDetails(string id,int offset=0,int runOffset=0)
    {
        var task=GetOperation(id);if(task is null)return null;
        var members=task.Operation=="remoteBatch"?_repository.RemoteBatchMembers(id,offset).Select(item=>new MountItemResult(item.Path,item.State is "Superseded"?"Skipped":item.State,(item.Action switch{"Created"=>ResourceMessage.Create("OperationMemberCreated").Render(),"Updated"=>ResourceMessage.Create("OperationMemberUpdated").Render(),"Deleted"=>ResourceMessage.Create("OperationMemberDeleted").Render(),_=>""})+(item.Reason is null?"":" · "+item.Reason))).ToArray():task.Results.Skip(offset).Take(256).ToArray();
        return new(task,_repository.OperationRuns(id,runOffset),members,task.Operation=="remoteBatch"?task.Total:task.Results.Count,offset,_repository.OperationRunCount(id),runOffset);
    }
    /// <summary>沿真实pending身份取消/重试，不把独立下载当作批次子取消目标。</summary>
    private bool ChangeDisplayedRemoteBatch(string id,bool retry)
    {
        var batch=_repository.FindRemoteBatch(id);if(batch?.MountId is not {} mount||!_roots.TryGetValue(mount,out var root)||IsStopping)return false;
        if(!retry&&batch.State is "Completed" or "Canceled")return false;
        var ids=_repository.ChangeRemoteBatch(id,retry);
        if(retry&&ids.Count==0)return false;
        if(root.RemoteSync is {} runtime)
        {
            if(!retry)lock(runtime.Sync)if(runtime.CurrentId is {} current&&ids.Contains(current))runtime.Request?.Cancel();
            if(retry)
            {
                BeginTrace(id,"remoteBatch",runtime.ConnectionIdentity,retry:true);Stage(id,"OperationStageVerifyLocal");
                _=OwnAsync(async token=>
                {
                    using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,runtime.Stop.Token);
                    await runtime.Serial.WaitAsync(linked.Token).ConfigureAwait(false);
                    try{await ApplyRemotePendingAsync(root,runtime,linked.Token).ConfigureAwait(false);}
                    finally{runtime.Serial.Release();FinishBatchTraces(root);}
                },_stop.Token);
            }
        }
        Notify();return true;
    }
    private void FinishBatchTraces(Runtime root)
    {
        foreach(var batch in _repository.RemoteBatches(root.Definition.Id,activeTracesOnly:true).Where(item=>item.State!="Running"))
        {Stage(batch.Id,"OperationStageSummarize");EndTrace(batch.Id,batch.State,batch.Error);}
    }
}
