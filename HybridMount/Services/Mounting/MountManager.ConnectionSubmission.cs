using HybridMount.Commons.Protocols;
using HybridMount.Infrastructure.Protocols.Synology;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly HashSet<string> _connectionSubmissions=new(StringComparer.OrdinalIgnoreCase);
    private static string SubmissionScope(ConnectionSubmissionDraft draft)=>draft.ExistingId?.ToString("N")??Path.GetFullPath(draft.Path);
    private void ReleaseSubmission(string scope){lock(_lifetime)_connectionSubmissions.Remove(scope);}
    /// <summary>只读本地校验及本次服务器认证；失败和取消不保存、不注册、不停止现有连接。结果由UI单次移交。</summary>
    internal Task<ConnectionSubmissionProof> AuthenticateSubmissionAsync(ConnectionSubmissionDraft input,string? otp,CancellationToken token)
    {
        var draft=input.Freeze();var scope=SubmissionScope(draft);
        lock(_lifetime){ThrowIfStopping();if(_connectionSubmissions.Count>=32||!_connectionSubmissions.Add(scope))throw new IOException(HybridMount.Models.Localization.ResourceMessage.Create("ConfigurationAlreadySubmitting").Render());}
        try{return OwnAsync(async cancel=>
        {
            SynologyProbeResult? synology=null;
            try
            {
                await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
                try{ValidateSubmission(draft);}finally{_configurationGate.Release();}
                var started=DateTimeOffset.UtcNow;var credentials=draft.Credentials;
                if(draft.Source.Kind==FileSourceKind.Synology)
                {
                    synology=await TestSynologyConnectionAsync(draft.Source,credentials,otp,cancel,previous:null).ConfigureAwait(false);
                    if(!synology.Success)throw new ConnectionSubmissionException(synology.Message,fingerprint:synology.CertificateSha256,authenticationCode:synology.AuthenticationCode);
                    credentials=synology.Credentials??credentials;
                }
                else
                {
                    var tested=await TestConnectionAsync(draft.Source,credentials,cancel).ConfigureAwait(false);
                    if(!tested.Success)throw new ConnectionSubmissionException(tested.Message,tested.Certificate);
                }
                cancel.ThrowIfCancellationRequested();var proof=new ConnectionSubmissionProof(this,draft,started,DateTimeOffset.UtcNow,credentials,synology?.Session,()=>ReleaseSubmission(scope));
                synology=null;return proof;
            }
            catch{if(synology is not null)await synology.DisposeAsync().ConfigureAwait(false);ReleaseSubmission(scope);throw;}
        },token);}
        catch{ReleaseSubmission(scope);throw;}
    }
    private void ValidateSubmission(ConnectionSubmissionDraft draft)
    {
        EnsureNoRemovalBatch();ValidateNewConnection(draft.Source,draft.Credentials);
        var options=draft.Options;CapacityRules.Validate(draft.Source.Kind,options.CapacityAutoDetectEnabled,options.MaximumCapacityBytes);
        if(options.RemoteIncrementalSeconds is <10 or >86400||options.RemoteFullSeconds is <30 or >604800)throw new ArgumentException("检测间隔超出允许范围。");
        ValidateSqsSettings(draft.Source,options.Sqs,options.SqsCredentials);FileExclusionRules.Validate(draft.Exclusions);
        if(draft.ExistingId is {} id)
        {
            var root=Root(id);if(root.RemovalRequested||root.Definition.RemovalPending)throw new IOException(HybridMount.Models.Localization.ResourceMessage.Create("ThisConfigurationIsBusyWithAnotherOperation").Render());
            var previous=root.Definition.Source??throw new IOException("旧版挂载不能更换协议。");
            if(previous.Kind!=draft.Source.Kind||(!CanEditRemoteRoot(id)&&(previous.Endpoint!=draft.Source.Endpoint||previous.Bucket!=draft.Source.Bucket||previous.Prefix!=draft.Source.Prefix||previous.AllowHttp!=draft.Source.AllowHttp)))throw new IOException("已有挂载不能更换远端根，请新建挂载。");
        }
        else _=ValidateNewMountRoot(draft.Name,draft.Path);
    }
    /// <summary>认证结果只能被原应用接纳一次。快照和阶段先持久化，后台使用应用令牌，关闭表单不取消它。</summary>
    internal Task<AcceptedApplicationTask> AcceptSubmissionAsync(ConnectionSubmissionProof proof,CancellationToken token)
        =>OwnAsync(cancel=>
        {
            lock(_lifetime)
            {
                ThrowIfStopping();cancel.ThrowIfCancellationRequested();EnsureNoRemovalBatch();
                if(!ReferenceEquals(proof.Owner,this)||proof.Transferred)throw new IOException(HybridMount.Models.Localization.ResourceMessage.Create("ConfigurationAuthenticationExpired").Render());
                var draft=proof.Draft;var mount=draft.ExistingId??Guid.NewGuid();var job=new Job(Guid.NewGuid().ToString("N"),mount,"connect",_stop.Token);
                job.Snapshot=job.Snapshot with{Total=1,ItemPath=draft.Path,MountName=draft.Name};
                var stages=new List<string>{"OperationStageAuthenticate"};if(draft.ExistingId is not null)stages.Add("OperationStageStopPreviousConnection");
                stages.AddRange(["OperationStageSaveConfiguration","OperationStageConnectSource","OperationStageInitialize","OperationStageReadChanges","OperationStageSummarize"]);
                try
                {
                    if(DateTimeOffset.UtcNow-proof.Finished>TimeSpan.FromMinutes(4))throw new IOException(HybridMount.Models.Localization.ResourceMessage.Create("ConfigurationAuthenticationExpired").Render());
                    _repository.AcceptApplicationTask(job.Snapshot,stages,proof.Started,proof.Finished);proof.Transfer();
                    _jobs[job.Snapshot.Id]=job;
                    job.Work=OwnAsync(appToken=>RunSubmissionAsync(job,proof,appToken),_stop.Token);Notify();
                    return Task.FromResult(new AcceptedApplicationTask(job.Snapshot.Id,mount));
                }
                catch{job.Cancel.Dispose();throw;}
            }
        },token);
#if HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>实际持久接纳后、开始保存前的隔离屏障；不进入生产。</summary>
    internal Func<string,CancellationToken,Task>? SubmissionAcceptedForTests {get;set;}
#endif
    private async Task RunSubmissionAsync(Job job,ConnectionSubmissionProof proof,CancellationToken appToken)
    {
        var scope=SubmissionScope(proof.Draft);
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(appToken,job.Cancel.Token);
        using var authenticated=AuthenticationSessionContext.Use(proof.Session);
        using var parent=TasksQueue.EnterTask(job.Root,job.Snapshot.Id,MountingPriority.User,"connect");
        try
        {
#if HYBRIDMOUNT_HEADLESS_TEST
            if(SubmissionAcceptedForTests is {} checkpoint)await checkpoint(job.Snapshot.Id,cancel.Token).ConfigureAwait(false);
#endif
            job.Snapshot=job.Snapshot with{State="Running"};Save(job);
            var draft=proof.Draft;
            if(draft.ExistingId is {} existing)await UpdateCredentialsCoreAsync(existing,proof.Credentials,cancel.Token,draft.Source,job.Snapshot.Id).ConfigureAwait(false);
            else
            {
                Stage(job.Snapshot.Id,"OperationStageSaveConfiguration");
                await _configurationGate.WaitAsync(cancel.Token).ConfigureAwait(false);
                try
                {
                    var options=draft.Options;
                    SaveNewMountCore(draft.Name,draft.Path,draft.AutoConnect,draft.Source,proof.Credentials,options.ThumbnailsEnabled,cancel.Token,
                        remoteAuto:options.RemoteAutoDetectEnabled,incremental:options.RemoteIncrementalSeconds,full:options.RemoteFullSeconds,sqs:options.Sqs,
                        sqsCredentials:options.SqsCredentials,capacityAuto:options.CapacityAutoDetectEnabled,maximumCapacity:options.MaximumCapacityBytes,exclusions:draft.Exclusions,id:job.Root);
                }
                finally{_configurationGate.Release();}
                Notify();await ConnectOwnedAsync(job.Root,cancel.Token,acceptedTask:job.Snapshot.Id).ConfigureAwait(false);
            }
            job.Snapshot=job.Snapshot with{State="Completed",Completed=1};EndTrace(job.Snapshot.Id,"Completed");Save(job);
        }
        catch(Exception error)
        {
            var stage=_repository.OperationRuns(job.Snapshot.Id).FirstOrDefault()?.Steps.FirstOrDefault(item=>item.State is "Running" or "Failed" or "Canceled")?.Name;
            var state=error is OperationCanceledException&&cancel.IsCancellationRequested?"Canceled":"Failed";
            job.Snapshot=job.Snapshot with{State=state,Error=error.Message,FailureStage=stage,FailureHResult=error.HResult};EndTrace(job.Snapshot.Id,state,error.Message);Save(job);
            ReportDiagnosticException("配置任务","配置接纳后的保存或连接未完成",error);
        }
        finally
        {
            try{if(proof.Session is {} session)await session.DisposeAsync().ConfigureAwait(false);}
            finally{ReleaseSubmission(scope);proof.ClearSecrets();job.Cancel.Dispose();}
        }
    }
}
