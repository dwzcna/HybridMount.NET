using System.Security.Cryptography;
using System.Text;
using HybridMount.Common;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly object _remoteJournalLock=new();
    private readonly SemaphoreSlim _remoteSlots=new(2,2);
    private sealed class RemoteSyncRuntime(IFileSource source,CloudMountSession session,RemoteSyncEngine engine,CancellationToken parent) : IDisposable
    {
        public readonly IFileSource Source=source;
        public readonly CloudMountSession Session=session;
        public readonly RemoteSyncEngine Engine=engine;
        public readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(parent);
        public readonly SemaphoreSlim Serial=new(1,1);
        public readonly SemaphoreSlim Signal=new(0,1);
        public readonly object Sync=new();
        public CancellationTokenSource? Request;
        public string? CurrentId;
        public Task Work=Task.CompletedTask,Cancellation=Task.CompletedTask;
        public bool Stopping,LoopStarted;
        public Task? Finish;
        public void RequestStop(){lock(Sync){if(Stopping)return;Stopping=true;Cancellation=Stop.CancelAsync();}}
        public void Wake(){lock(Sync){if(Stopping)return;if(Signal.CurrentCount==0)Signal.Release();}}
        public void Dispose(){Stop.Dispose();Serial.Dispose();Signal.Dispose();}
    }
    private void StartRemoteSync(Runtime root,bool startLoop=true)
    {
        lock(root.ConnectionSync)
        {
            if(root.RemoteSync is {} existing)
            {
                if(startLoop&&!existing.LoopStarted&&!existing.Stopping&&root.Definition.RemoteAutoDetectEnabled&&!IsStopping)
                {existing.LoopStarted=true;existing.Work=Task.Run(()=>RemoteLoopAsync(root,existing));}
                return;
            }
            if(root.Source is not IRemoteChangeSource changes||root.Session is null||root.Definition.Source?.Kind!=FileSourceKind.WebDav||root.RemovalRequested||IsStopping)return;
            var identity=root.Source is WebDavFileSource dav?dav.RemoteSyncIdentity:root.Definition.Source.Endpoint;
            var scope=root.Source is WebDavFileSource scoped?scoped.RemoteScope(root.Definition.CredentialRevision):Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity+"\0"+root.Definition.CredentialRevision)));
            var runtime=new RemoteSyncRuntime(root.Source,root.Session,new(root.Definition.Id,scope,root.Source,changes,_repository),_stop.Token);root.RemoteSync=runtime;
            changes.RestoreObservations(_repository.RemoteShadow(root.Definition.Id,scope));
            if(startLoop&&root.Definition.RemoteAutoDetectEnabled){runtime.LoopStarted=true;runtime.Work=Task.Run(()=>RemoteLoopAsync(root,runtime));}
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
        await Task.WhenAll(runtime.Cancellation,runtime.Work).ConfigureAwait(false);
        await runtime.Serial.WaitAsync().ConfigureAwait(false);runtime.Serial.Release();
        lock(root.ConnectionSync)if(ReferenceEquals(root.RemoteSync,runtime))root.RemoteSync=null;
        runtime.Dispose();
    }
    private async Task RemoteLoopAsync(Runtime root,RemoteSyncRuntime runtime)
    {
        var failures=0;var next=TimeSpan.FromSeconds(root.Definition.RemoteIncrementalSeconds);
        if(_repository.RemotePending(root.Definition.Id,runtime.Engine.Scope).Any(x=>x.State is not("Completed" or "Canceled" or "Superseded")))next=TimeSpan.Zero;
        while(!runtime.Stop.IsCancellationRequested)
        {
            try
            {
                await runtime.Signal.WaitAsync(next,runtime.Stop.Token).ConfigureAwait(false);
                await DetectRemoteAsync(root,runtime,false,runtime.Stop.Token).ConfigureAwait(false);failures=0;
                next=TimeSpan.FromSeconds(runtime.Engine.Mode.StartsWith("完整对账")?root.Definition.RemoteFullSeconds:root.Definition.RemoteIncrementalSeconds);
            }
            catch(OperationCanceledException)when(runtime.Stop.IsCancellationRequested){break;}
            catch(Exception error)
            {
                root.Detection=root.Detection with{Error=error.Message};Notify();
                if(error is RemoteSyncException{AuthenticationFailure:true} or SourceRequestRejectedException{HttpStatus:401 or 403}){_logger.Queue("远端检测","Warning","WebDAV 认证或权限失败，已暂停至重新连接。");break;}
                failures++;next=TimeSpan.FromMinutes(Math.Min(15,Math.Pow(2,Math.Min(failures-1,4))));
                var retry=error is RemoteSyncException sync?sync.RetryAfter:(error as SourceRequestRejectedException)?.RetryAfter;
                if(retry is {} delay&&delay>next)next=delay;
                _logger.Queue("远端检测","Warning",error.Message);
            }
        }
    }
    /// <summary>手动刷新请求完整基线，关闭自动检测仍可执行；全程属于连接停止及应用退出所有者。</summary>
    private Task DetectRemoteNowAsync(Runtime root,bool full,CancellationToken token)
    {
        StartRemoteSync(root);var runtime=root.RemoteSync??throw new IOException("WebDAV 尚未连接。");
        return DetectRemoteAsync(root,runtime,full,token);
    }
    /// <summary>仅程序集内协议/CFAPI 联合验证使用，仍登记生命周期且走正式串行调度与保护。</summary>
    internal Task DetectRemoteForTestsAsync(Guid id,CancellationToken token=default)=>OwnAsync(cancel=>DetectRemoteNowAsync(Root(id),false,cancel),token);
    private bool RemoteSessionCurrent(Runtime root,RemoteSyncRuntime runtime)=>!IsStopping&&!root.RemovalRequested&&!root.Definition.RemovalPending&&
        ReferenceEquals(root.RemoteSync,runtime)&&ReferenceEquals(root.Session,runtime.Session)&&ReferenceEquals(root.Source,runtime.Source)&&_roots.ContainsKey(root.Definition.Id);
    private async Task DetectRemoteAsync(Runtime root,RemoteSyncRuntime runtime,bool full,CancellationToken token)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,runtime.Stop.Token);token=linked.Token;
        await runtime.Serial.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);
            await _remoteSlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await runtime.Engine.PollAsync(full,TimeSpan.FromSeconds(root.Definition.RemoteFullSeconds),token).ConfigureAwait(false);
                await ApplyRemotePendingAsync(root,runtime,token).ConfigureAwait(false);
                root.Detection=new(runtime.Engine.Mode,DateTimeOffset.UtcNow);Notify();
            }
            finally{_remoteSlots.Release();}
        }
        finally{runtime.Serial.Release();}
    }
    private IReadOnlyList<RemotePendingChange> RemotePendingFor(Runtime root)=>_repository.AllRemotePending(root.Definition.Id);
    private static MountOperationSnapshot RemoteTask(RemotePendingChange change,long bytes=0)=>new(change.Id,"remoteUpdate",change.State=="Pending"?"Queued":change.State,
        change.State=="Completed"?1:0,1,change.Error is null?[]:[new(change.Path,change.State,change.Error)],change.Error,change.MountId,change.Path,bytes,change.Entry?.Size??0,change.Entry?.IsDirectory,change.Path);
    private RemotePendingChange SaveRemote(RemotePendingChange pending,bool retry=false)
    {
        lock(_remoteJournalLock)
        {
            var current=_repository.RemotePending(pending.MountId,pending.Scope).FirstOrDefault(x=>x.Id==pending.Id);
            if(!retry&&current?.State=="Canceled"&&pending.State is not("Canceled" or "Completed" or "Superseded"))return current;
            _repository.SaveRemotePending(pending);_repository.SaveOperation(RemoteTask(pending,pending.State=="Completed"?pending.Entry?.Size??0:0));Notify();return pending;
        }
    }
    private bool CancelRemoteChange(string id)
    {
        foreach(var root in _roots.Values)
        {
            var pending=RemotePendingFor(root).FirstOrDefault(x=>x.Id==id);if(pending is null)continue;
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
            var pending=RemotePendingFor(root).FirstOrDefault(x=>x.Id==id);if(pending is null)continue;
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
            if(incrementalSeconds is <10 or >86400||fullSeconds is <30 or >604800)throw new ArgumentException("增量间隔为 10–86400 秒，完整对账间隔为 30–604800 秒。");
            var root=Root(id);await root.Gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if(root.Definition.Source?.Kind!=FileSourceKind.WebDav||root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("当前挂载不能更改 WebDAV 检测设置。");
                var definition=root.Definition with{RemoteAutoDetectEnabled=enabled,RemoteIncrementalSeconds=incrementalSeconds,RemoteFullSeconds=fullSeconds};_repository.SaveDefinition(definition);root.Definition=definition;
            }
            finally{root.Gate.Release();}
            RequestRemoteStop(root);await StopRemoteAsync(root).ConfigureAwait(false);StartRemoteSync(root);Notify();
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
