using System.Security.Cryptography;
using System.Text;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly object _remoteJournalLock=new();
    private sealed class RemoteSyncRuntime(IFileSource source,CloudMountSession session,RemoteSyncEngine engine,CancellationToken parent) : IDisposable
    {
        public readonly IFileSource Source=source;
        public readonly CloudMountSession Session=session;
        public readonly RemoteSyncEngine Engine=engine;
        public readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(parent);
        public readonly SemaphoreSlim Serial=new(1,1);
        public readonly SemaphoreSlim ScanSerial=new(1,1);
        public readonly NetworkFailureLog FailureLog=new();
        public readonly StagingRetrySchedule StagingRetries=new();
        public readonly SemaphoreSlim Signal=new(0,1);
        public readonly object Sync=new();
        public CancellationTokenSource? Request;
        public string? CurrentId;
        public Task Work=Task.CompletedTask,QueueWork=Task.CompletedTask,Cancellation=Task.CompletedTask;
        public bool Stopping,LoopStarted,RescanSoon,QueueStarted;
        public long Discovered,Created,Published,LastPublicationProgress;
        public Task? Finish;
        public void RequestStop(){lock(Sync){if(Stopping)return;Stopping=true;Cancellation=Stop.CancelAsync();}}
        public void Wake(){lock(Sync){if(Stopping)return;if(Signal.CurrentCount==0)Signal.Release();}}
        public void Dispose(){Stop.Dispose();Serial.Dispose();ScanSerial.Dispose();Signal.Dispose();}
    }
    private void StartRemoteSync(Runtime root,bool startLoop=true)
    {
        lock(root.ConnectionSync)
        {
            if(root.RemoteSync is {} existing)
            {
                if(startLoop&&!existing.LoopStarted&&!existing.Stopping&&root.Definition.RemoteAutoDetectEnabled&&!IsStopping)
                {existing.LoopStarted=true;existing.Work=TasksQueue.RunObserverAsync(new MountingObserver("remote:"+root.Definition.Id,root.Definition.Id,cancel=>RemoteLoopAsync(root,existing,cancel)),existing.Stop.Token);}
                if(startLoop)StartRemoteEvents(root,existing);
                return;
            }
            if(root.Source is not IRemoteChangeSource changes||root.Session is null||root.Definition.Source?.Kind is not(FileSourceKind.WebDav or FileSourceKind.S3 or FileSourceKind.Synology)||root.RemovalRequested||IsStopping)return;
            var identity=root.Definition.Source.Endpoint;
            var scope=root.Source is IMountSourceRuntime scoped?scoped.RemoteScope(root.Definition.CredentialRevision):Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity+"\0"+root.Definition.CredentialRevision)));
            _repository.ActivateRemoteScope(root.Definition.Id,scope);
            var runtime=new RemoteSyncRuntime(root.Source,root.Session,new(root.Definition.Id,scope,root.Source,changes,_repository,root.ChangeSession),_stop.Token);root.RemoteSync=runtime;
            runtime.Engine.PublishDiscovered=(batch,cancel)=>PublishDiscoveryBatchAsync(root,runtime,batch,cancel);
            runtime.Engine.DiscoveryProgress=count=>{runtime.Discovered=count;PublishDiscoveryProgress(root,runtime);};
            changes.RestoreObservations(_repository.RemoteShadow(root.Definition.Id,scope));
            if(startLoop&&root.Definition.RemoteAutoDetectEnabled){runtime.LoopStarted=true;runtime.Work=TasksQueue.RunObserverAsync(new MountingObserver("remote:"+root.Definition.Id,root.Definition.Id,cancel=>RemoteLoopAsync(root,runtime,cancel)),runtime.Stop.Token);StartRemoteEvents(root,runtime);}
        }
    }
    private static void RequestRemoteStop(Runtime root){lock(root.ConnectionSync)root.RemoteSync?.RequestStop();}
    private static Task StopRemoteAsync(Runtime root)
    {
        lock(root.ConnectionSync)
        {
            var runtime=root.RemoteSync;if(runtime is null)return Task.CompletedTask;runtime.RequestStop();
            return runtime.Finish??=FinishRemoteAsync(root,runtime);
        }
    }
    private static async Task FinishRemoteAsync(Runtime root,RemoteSyncRuntime runtime)
    {
        await Task.WhenAll(runtime.Cancellation,runtime.Work,runtime.QueueWork).ConfigureAwait(false);
        await runtime.Serial.WaitAsync().ConfigureAwait(false);runtime.Serial.Release();
        await runtime.ScanSerial.WaitAsync().ConfigureAwait(false);runtime.ScanSerial.Release();
        lock(root.ConnectionSync)if(ReferenceEquals(root.RemoteSync,runtime))root.RemoteSync=null;
        runtime.Dispose();
    }
    private async Task RemoteLoopAsync(Runtime root,RemoteSyncRuntime runtime,CancellationToken observerToken=default)
    {
        using var observerStop=observerToken.UnsafeRegister(static state=>((RemoteSyncRuntime)state!).RequestStop(),runtime);
        var failures=0;var next=TimeSpan.FromSeconds(runtime.Source is IRemoteSnapshotSource||root.ChangeSession.Fallback is not null||runtime.Engine.Mode.StartsWith("完整对账")?root.Definition.RemoteFullSeconds:root.Definition.RemoteIncrementalSeconds);
        if(runtime.RescanSoon||_repository.RemotePending(root.Definition.Id,runtime.Engine.Scope).Any(x=>x.State is not("Completed" or "Canceled" or "Superseded")))next=TimeSpan.Zero;
        while(!runtime.Stop.IsCancellationRequested)
        {
            try
            {
                await runtime.Signal.WaitAsync(next,runtime.Stop.Token).ConfigureAwait(false);
                await DetectRemoteAsync(root,runtime,runtime.RescanSoon,runtime.Stop.Token).ConfigureAwait(false);runtime.RescanSoon=false;failures=0;
                if(runtime.FailureLog.Recovered(out var recovered))_logger.Queue("远端检测","Information",recovered);
                next=TimeSpan.FromSeconds(runtime.Source is IRemoteSnapshotSource||runtime.Engine.Mode.StartsWith("完整对账")?root.Definition.RemoteFullSeconds:root.Definition.RemoteIncrementalSeconds);
            }
            catch(OperationCanceledException)when(runtime.Stop.IsCancellationRequested){break;}
            catch(OperationCanceledException){next=TimeSpan.FromSeconds(root.Definition.RemoteFullSeconds);}
            catch(Exception error)
            {
                root.Detection=root.Detection with{Mode=runtime.Engine.Mode,Error=error is SourceRescanRequiredException?"远端持续变化，三次完整扫描未能稳定完成；5 秒后继续，现有内容保持保护。":error.Message};Notify();
                if(error is SourceRescanRequiredException){_logger.QueueException("远端检测","Warning","远端持续变化，完整扫描将重试",error);runtime.RescanSoon=true;next=TimeSpan.FromSeconds(5);continue;}
                if(error is RemoteChangeFailure{AuthenticationFailure:true} or SourceRequestRejectedException{HttpStatus:401 or 403} or SourceConnectionException{Diagnostic.Kind:ConnectionFailureKind.Authentication or ConnectionFailureKind.Permission}){_logger.Queue("远端检测","Warning","远端认证或权限失败，已暂停至重新连接。");break;}
                if(error is SourceConnectionException{Diagnostic.Kind:ConnectionFailureKind.Certificate})
                {root.Detection=root.Detection with{Error=error.Message+" 自动检测已暂停，请修正后断开并重新连接。"};Notify();_logger.QueueException("远端检测","Warning","证书失败，自动检测已暂停",error);break;}
                failures++;next=TimeSpan.FromMinutes(Math.Clamp(Math.Pow(2,Math.Min(failures-1,4))*(0.9+Random.Shared.NextDouble()*0.2),1,15));
                var retry=error is RemoteChangeFailure sync?sync.RetryAfter:(error as SourceRequestRejectedException)?.RetryAfter;
                if(retry is {} delay&&delay>next)next=delay;
                if(error is not SourceConnectionException connection)_logger.QueueException("远端检测","Warning","操作未完成",error);
                else if(runtime.FailureLog.ShouldWrite(connection.Diagnostic,out var suffix))_logger.QueueException("远端检测","Warning","检测未完成"+suffix,connection);
            }
        }
    }
    /// <summary>手动刷新请求完整基线，关闭自动检测仍可执行；全程属于连接停止及应用退出所有者。</summary>
    private async Task DetectRemoteNowAsync(Runtime root,bool full,CancellationToken token)
    {
        using var queueTask=TasksQueue.EnterTask(root.Definition.Id,TasksQueueService.CurrentTask?.Id??Guid.NewGuid().ToString("N"),MountingPriority.User,"refresh");
        StartRemoteSync(root);var runtime=root.RemoteSync??throw new IOException("远端尚未连接。");
        try{await DetectRemoteAsync(root,runtime,full,token).ConfigureAwait(false);}
        catch(SourceRescanRequiredException){runtime.RescanSoon=true;if(runtime.LoopStarted)runtime.Wake();throw;}
    }
    /// <summary>仅程序集内协议/CFAPI 联合验证使用，仍登记生命周期且走正式串行调度与保护。</summary>
    internal Task DetectRemoteForTestsAsync(Guid id,CancellationToken token=default)=>OwnAsync(cancel=>DetectRemoteNowAsync(Root(id),false,cancel),token);
    private bool RemoteSessionCurrent(Runtime root,RemoteSyncRuntime runtime)=>!IsStopping&&!root.RemovalRequested&&!root.Definition.RemovalPending&&
        ReferenceEquals(root.RemoteSync,runtime)&&ReferenceEquals(root.Session,runtime.Session)&&ReferenceEquals(root.Source,runtime.Source)&&_roots.ContainsKey(root.Definition.Id);
    private async Task DetectRemoteAsync(Runtime root,RemoteSyncRuntime runtime,bool full,CancellationToken token)
    {
        var parent=TasksQueueService.CurrentTask;
        using var queueTask=TasksQueue.EnterTask(root.Definition.Id,parent?.MountId==root.Definition.Id?parent.Id:Guid.NewGuid().ToString("N"),parent?.MountId==root.Definition.Id?parent.Priority:MountingPriority.Observation,"remoteScan");
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,runtime.Stop.Token);token=linked.Token;
        using var cancelOwner=TasksQueue.RegisterCancellation(TasksQueueService.CurrentTask!.Id,()=>{if(!linked.IsCancellationRequested)_=linked.CancelAsync();return true;});
        await runtime.ScanSerial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);
            try
            {
                runtime.Discovered=runtime.Created=runtime.Published=runtime.LastPublicationProgress=0;
                await runtime.Engine.ObserveEventHintsAsync(token).ConfigureAwait(false);
                try{await runtime.Engine.PollAsync(full,TimeSpan.FromSeconds(root.Definition.RemoteFullSeconds),token).ConfigureAwait(false);}
                finally
                {
                    // 格式错误被引擎转为全量后不会抛出；即使后续全量失败，也先记录一次挂载会话降级。
                    if(root.ChangeSession.Fallback is {} fallback)RecordStateDiagnostic("远端检测",root.Definition.Id,root.Definition.Id.ToString("N"),"RFC6578Fallback",true,fallback.OccurredUtc.UtcTicks.ToString(),
                        description:"RFC 6578 响应异常，本次挂载会话已改用完整对账；降级时间UTC="+fallback.OccurredUtc.ToString("O"));
                }

                var checkpoint=_repository.RemoteCollections(root.Definition.Id,runtime.Engine.Scope).FirstOrDefault(x=>x.Path=="");
                root.Detection=root.Detection with{Mode=runtime.Engine.Mode,LastSuccessUtc=runtime.Source is IRemoteSnapshotSource?checkpoint?.LastFullUtc:DateTimeOffset.UtcNow,Error=null,
                    PendingHints=_repository.PendingRemoteHints(root.Definition.Id,runtime.Engine.Scope).Count};Notify();
            }
            catch(Exception error){if(error is not OperationCanceledException)_logger.QueueException("远端检测","Warning","远端检测未完成",error,root.Definition.Id.ToString("N"));root.Detection=root.Detection with{Mode=runtime.Engine.Mode};Notify();throw;}
        }
        finally{runtime.ScanSerial.Release();}
        await runtime.Serial.WaitAsync(token).ConfigureAwait(false);
        try{await ApplyRemotePendingAsync(root,runtime,token).ConfigureAwait(false);}
        finally{runtime.Serial.Release();}
    }
    private IReadOnlyList<RemotePendingChange> RemotePendingFor(Runtime root)=>_repository.AllRemotePending(root.Definition.Id);
    private static MountOperationSnapshot RemoteTask(RemotePendingChange change,long bytes=0)=>new(change.Id,"remoteUpdate",change.State=="Pending"?"Queued":change.State,
        change.State=="Completed"?1:0,1,change.Error is null?[]:[new(change.Path,change.State,change.Error)],change.Error,change.MountId,change.Path,bytes,change.Entry?.Size??0,change.Entry?.IsDirectory,change.Path);
    private RemotePendingChange RequireRemotePending(string id)=>_repository.FindRemotePending(id)??throw new IOException("远端事件身份已丢失，保留当前阶段等待核对。");
    private RemotePendingChange SaveRemote(RemotePendingChange pending,bool retry=false,Exception? failure=null)
    {
        lock(_remoteJournalLock)
        {
            var current=_repository.FindRemotePending(pending.Id);
            if(!retry&&current?.State=="Canceled"&&pending.State is not("Canceled" or "Completed" or "Superseded"))return current;
            if(!StagingDiskSpace.IsWaiting(pending.State,pending.Error)&&_roots.TryGetValue(pending.MountId,out var root))root.RemoteSync?.StagingRetries.Reset(pending.Id);
            pending=_repository.SaveRemotePendingCurrent(pending,retry);RecordStateDiagnostic("远端更新",pending.MountId,pending.Id,pending.State,pending.Error is not null&&pending.State is not("Completed" or "Canceled" or "Superseded"),DiagnosticVersion(pending.Scope+pending.Error),failure);_repository.SaveOperation(RemoteTask(pending,pending.State=="Completed"?pending.Entry?.Size??0:0));Notify();return pending;
        }
    }
    private bool CancelRemoteChange(string id)
    {
        foreach(var root in _roots.Values)
        {
            var pending=RemotePendingFor(root).FirstOrDefault(x=>x.Id==id);if(pending is null)continue;if(HasConflictResolution(root.Definition.Id,pending.Path))return false;
            if(pending.State is "Completed" or "Superseded" or "Canceled" or "Committing" or "Hydrating")return false;
            SaveRemote(pending with{State="Canceled",Error="已取消此远端版本；显式重试或下一远端版本后恢复。"});
            if(root.RemoteSync is {} runtime)lock(runtime.Sync)if(runtime.CurrentId==id)runtime.Request?.Cancel();return true;
        }
        return false;
    }
    private bool RetryRemoteChange(string id)
    {
        foreach(var root in _roots.Values)
        {
            var pending=RemotePendingFor(root).FirstOrDefault(x=>x.Id==id);if(pending is null)continue;if(HasConflictResolution(root.Definition.Id,pending.Path))return false;
            if(pending.State is not("Conflict" or "Failed" or "Canceled" or "Waiting" or "Interrupted")&&!(pending.State is "Committing" or "Hydrating"&&pending.Error is not null))return false;
            var retryState=pending.State=="Hydrating"?"Hydrating":pending.State=="Committing"?"Prepared":"Pending";
            SaveRemote(pending with{State=retryState,Error=null},retry:true);
            // 仅显式重试唤起当前轮，不重置游标；异常仍由统一状态记录。
            if(root.RemoteSync is {} runtime)_ = OwnAsync(async cancel=>
            {try{await DetectRemoteAsync(root,runtime,false,cancel).ConfigureAwait(false);}catch(OperationCanceledException)when(IsStopping||runtime.Stop.IsCancellationRequested){}catch(Exception e){root.Detection=root.Detection with{Error=e.Message};Notify();}},_stop.Token);
            return true;
        }
        return false;
    }
    /// <summary>保存后再停止检测；设置切换不接管其他挂载，失败由 UI 重新读取旧值。</summary>
    public Task SetRemoteDetectionAsync(Guid id,bool enabled,int incrementalSeconds,int fullSeconds,CancellationToken token=default)
        =>OwnAsync(async cancel=>
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            if(incrementalSeconds is <10 or >86400||fullSeconds is <30 or >604800)throw new ArgumentException("增量间隔为 10–86400 秒，完整对账间隔为 30–604800 秒。");
            var root=Root(id);var reopened=false;await root.Gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if(root.Definition.Source?.Kind is not(FileSourceKind.WebDav or FileSourceKind.S3 or FileSourceKind.Synology)||root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("当前挂载不能更改远端检测设置。");
                reopened=enabled&&!root.Definition.RemoteAutoDetectEnabled;
                var definition=root.Definition with{RemoteAutoDetectEnabled=enabled,RemoteIncrementalSeconds=incrementalSeconds,RemoteFullSeconds=fullSeconds};_repository.SaveDefinition(definition);root.Definition=definition;
            }
            finally{root.Gate.Release();}
            RequestRemoteStop(root);await StopRemoteAsync(root).ConfigureAwait(false);
            if(reopened){root.ChangeSession=new();root.Detection=root.Detection with{Mode="尚未检测",Error=null};}
            StartRemoteSync(root);if(reopened)root.RemoteSync?.Wake();Notify();
        },token);
    private static void RejectRemoteLinks(string staging,string path)
    {
        for(var cursor=Path.GetFullPath(path);;cursor=Path.GetDirectoryName(cursor)!)
        {
            var attributes=LocalFileMetadata.TryRead(cursor)?.Attributes;
            if(attributes is {} flags&&(flags&FileAttributes.ReparsePoint)!=0)throw new SourceConflictException("远端暂存路径含外部链接，保留现场。");
            if(cursor.Equals(staging,StringComparison.OrdinalIgnoreCase))break;
            if(!MountPathGuard.IsUnder(staging,cursor))throw new SourceConflictException("远端暂存路径越过边界。");
        }
    }
    private void ValidateRemoteSnapshot(RemotePendingChange pending)
    {
        var staging=Path.GetFullPath(Path.Combine(_repository.DataDirectory,"RemoteStaging"));
        var expected=Path.Combine(staging,pending.MountId.ToString("N"),pending.Id+".snapshot");
        if(!Guid.TryParseExact(pending.Id,"N",out _)||pending.SnapshotPath is not {} path||!Path.IsPathFullyQualified(path)||!Path.GetFullPath(path).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new SourceConflictException("远端快照路径不属于此挂载和任务。");
        RejectRemoteLinks(staging,path);
    }
    private void DeleteRemoteSnapshot(string? path)
    {
        if(path is null)return;var staging=Path.GetFullPath(Path.Combine(_repository.DataDirectory,"RemoteStaging"));var full=Path.GetFullPath(path);
        if(!MountPathGuard.IsUnder(staging,full)||full==staging||!Path.GetExtension(full).Equals(".snapshot",StringComparison.OrdinalIgnoreCase))return;
        try{RejectRemoteLinks(staging,full);File.Delete(full);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
