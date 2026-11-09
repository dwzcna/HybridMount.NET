using System.Security.Cryptography;
using HybridMount.Commons;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>每根串行上传、全局两路；网络传输期间不持有根锁，窗口和读取不被队列阻塞。</summary>
    private async Task UploadLoopAsync(Runtime root,UploadRuntime runtime)
    {
        var token=runtime.Stop.Token;
        try
        {
            // 旧版本可能遗留同路径的排队记录与已发送记录；先由已发送记录接管，防止恢复时旧队列绕过结果核对。
            foreach(var group in _repository.Uploads(root.Definition.Id).Where(item=>!item.IsCopy).GroupBy(item=>item.SourcePath,StringComparer.Ordinal))
            {
                var sent=group.Where(item=>item.State is "Sending" or "RemoteCommitted" or "OutcomeUnknown").ToArray();
                if(sent.Length==1)foreach(var duplicate in group.Where(item=>item.Id!=sent[0].Id&&item.State is "Queued" or "Waiting" or "Prepared" or "WaitingCapacity" or "AwaitingConfirmation" or "Ignored"))
                    ChangeUpload(duplicate.Id,item=>item with{State="Canceled",CancelRequested=true,Error="同路径已有发送记录接管，本任务未重复发送。",LastCheckedUtc=null});
            }
            foreach(var item in _repository.Uploads(root.Definition.Id).Where(x=>x.State=="Sending"))ChangeUpload(item.Id,x=>x with{State="OutcomeUnknown",Error="上次请求已发出，恢复后先核对远端。",LastCheckedUtc=null});
            while(!token.IsCancellationRequested)
            {
                Task? idle;lock(runtime.Sync)idle=runtime.PropertyPath is not null||runtime.ConflictWaiters>0?runtime.IdleChanged.Task:null;
                if(idle is not null){await idle.WaitAsync(token).ConfigureAwait(false);continue;}
                var scan=runtime.Scans.Take(DateTimeOffset.UtcNow,runtime.TakeScanRequest(),path=>LocalWriteStampCache.Read(RelativePaths.ToLocal(root.Definition.LocalPath,path)));
                if(scan is not null)await ScanLocalUploadsAsync(root,token,batch:scan).ConfigureAwait(false);
                if(await TryRunExcludedRenameAsync(root,runtime,token).ConfigureAwait(false))continue;
                var candidates=UploadCandidates(root);
                var now=DateTimeOffset.UtcNow;var item=UploadRules.NextUpload(candidates,now);
                if(item is null)
                {
                    var wait=UploadRules.UploadWait(candidates,now,runtime.Scans.NextDueUtc(now));if(RenameIntents(root.Definition.Id).Any(x=>!x.Finished&&x.Stage is not("Failed" or "Conflict" or "OutcomeUnknown"))&&wait>TimeSpan.FromSeconds(1))wait=TimeSpan.FromSeconds(1);
                    await runtime.Signal.WaitAsync(wait,token).ConfigureAwait(false);continue;
                }
                await RunUploadAsync(root,runtime,item,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception error){_logger.QueueException("自动上传","Error","操作未完成",error);Status(root,root.Status,"自动上传监控已停止，请重新连接。" );}
    }
    /// <summary>与改名收尾共享同一可运行候选集，冲突和退避不会挡住其他文件。</summary>
    private LocalUpload[] UploadCandidates(Runtime root)=>_repository.Uploads(root.Definition.Id).Where(x=>
                    x.State=="OutcomeUnknown"||x.State=="RemoteCommitted"&&string.IsNullOrEmpty(x.Error)||(x.State is "Queued" or "Waiting" or "Prepared"&&!FileExclusionRules.Matches(root.Definition,x)&&!RenameBlocksSource(root.Definition.Id,x.SourcePath)&&(root.SchedulingEnabled||x.ExplicitRequest)&&!x.CancelRequested&&!_repository.HasPendingRemote(root.Definition.Id,x.SourcePath))).ToArray();
    private sealed class UploadReporter(Action<UploadProgress> action) : IProgress<UploadProgress>
    {public void Report(UploadProgress value)=>action(value);}
    /// <summary>发送前持久化快照及阶段；返回成功后先保存回执，再修改本地占位和索引。</summary>
    private async Task RunUploadAsync(Runtime root,UploadRuntime runtime,LocalUpload selected,CancellationToken stopping)
    {
        using var queueTask=TasksQueue.EnterTask(root.Definition.Id,selected.Id,selected.ExplicitRequest?MountingPriority.User:MountingPriority.Transaction,"upload",selected.SourcePath,selected.EntryId,selected.ExpectedVersion,selected.ChangedUtc);
        using var request=CancellationTokenSource.CreateLinkedTokenSource(stopping);
        lock(runtime.Sync){if(runtime.PropertyPath is not null||runtime.ConflictWaiters>0)return;runtime.CurrentId=selected.Id;runtime.Request=request;runtime.RequestCancellation=Task.CompletedTask;}
        Exception? failure=null;var token=request.Token;LocalUpload? prepared=selected;var preparing=true;UploadCapacityPermit? capacity=null;var receiptSaved=false;var sendingStarted=false;
        LocalUpload? Fail(Func<LocalUpload,LocalUpload> update)=>ChangeUpload(selected.Id,current=>current.State=="Excluded"?current:(preparing||prepared?.State=="RemoteCommitted")&&current!=prepared?current:update(current),failure);
        try
        {
            prepared=_repository.Upload(selected.Id);if(prepared is null)return;
            if(prepared.State=="OutcomeUnknown"){await ReconcileUploadAsync(root,prepared,token).ConfigureAwait(false);return;}
            if(prepared.State=="RemoteCommitted"){await CommitUploadAsync(root,prepared,token).ConfigureAwait(false);return;}
            if(prepared.CancelRequested||(!root.SchedulingEnabled&&!prepared.ExplicitRequest))return;
            await EnsureUploadIncludedAsync(root,prepared,token).ConfigureAwait(false);
            await UploadCheckpointAsync("BeforePrepare",prepared).ConfigureAwait(false);
            prepared=await PrepareUploadAsync(root,prepared,token).ConfigureAwait(false);
            if(prepared is null)return;preparing=false;
            await UploadCheckpointAsync("Prepared",prepared).ConfigureAwait(false);
            capacity=await EnterUploadCapacityAsync(root,prepared,token).ConfigureAwait(false);
            await PrepareUploadDestinationAsync(root,prepared,token,capacity.Definition).ConfigureAwait(false);
            await EnsureUploadIncludedAsync(root,prepared,token).ConfigureAwait(false);
            if(!prepared.IsDirectory)await root.Source!.PrepareUploadAsync(prepared.SourcePath,new(prepared.ExpectedVersion,prepared.BaselineSha256),prepared.Total,token).ConfigureAwait(false);
            SourceEntry receipt;
            if(prepared.IsDirectory)
            {
                var sending=await BeginUploadSendAsync(root.Definition.Id,prepared,token,capacity.Definition).ConfigureAwait(false);if(sending is null)return;prepared=sending;sendingStarted=true;
                receipt=await root.Source!.TryGetMetadataAsync(prepared.SourcePath,token).ConfigureAwait(false)??await root.Source.CreateDirectoryAsync(prepared.SourcePath,token).ConfigureAwait(false);
                if(!receipt.IsDirectory)throw new SourceConflictException("远端已有同名文件，目录不能覆盖它。");
            }
            else
            {
                var last=Environment.TickCount64;
                var progress=new UploadReporter(value=>{var now=Environment.TickCount64;if(now-last<200&&value.Bytes!=value.Total)return;last=now;
                    ChangeUpload(prepared.Id,x=>x with{Bytes=value.Bytes,Total=value.Total});});
                ValidateUploadSnapshot(prepared);
                await using var content=new FileStream(prepared.SnapshotPath!,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.Asynchronous|FileOptions.SequentialScan);
                var digest=Convert.ToHexString(await SHA256.HashDataAsync(content,token).ConfigureAwait(false));
                if(content.Length!=prepared.Total||digest!=prepared.SnapshotSha256)
                {ChangeUpload(prepared.Id,x=>x with{SnapshotStamp=null});throw new SourceConflictException("持久上传快照已变化，未发送内容；请重试以重新生成快照。");}
                content.Position=0;
                var sending=await BeginUploadSendAsync(root.Definition.Id,prepared,token,capacity.Definition).ConfigureAwait(false);if(sending is null)return;prepared=sending;sendingStarted=true;
                receipt=await root.Source!.UploadWithPreconditionAsync(prepared.SourcePath,content,new(prepared.ExpectedVersion,prepared.BaselineSha256),progress,token).ConfigureAwait(false);
            }
            prepared=ChangeUpload(prepared.Id,x=>x with{State="RemoteCommitted",Receipt=receipt,Error=x.Error==UploadRules.TypeConflictMessage?x.Error:null,Bytes=prepared.Total})!;
            capacity.Committed(receipt);receiptSaved=true;PublishCapacity(root,capacity.Definition,capacity.Snapshot);
            await UploadCheckpointAsync("RemoteCommitted",prepared).ConfigureAwait(false);
            await CommitUploadAsync(root,prepared,stopping).ConfigureAwait(false);
        }
        catch(UploadPreparationChangedException){}
        catch(UploadExcludedException){}
        catch(CapacitySettingsChangedException){ChangeUpload(selected.Id,x=>x.State=="Prepared"?x with{LastCheckedUtc=null}:x);}
        catch(CapacityWaitingException error){ChangeUpload(selected.Id,x=>x.CancelRequested?x with{State="Canceled"}:x.State=="Prepared"?x with{State="WaitingCapacity",Error=error.Message,LastCheckedUtc=DateTimeOffset.UtcNow}:x,error);}
        catch(StagingSpaceException error)when(preparing){failure=error;Fail(x=>StagingDiskSpace.Wait(x,error,DateTimeOffset.UtcNow));}
        catch(SourceConflictException error){failure=error;Fail(x=>x with{State="Conflict",Error=error.Message,LastCheckedUtc=null});}
        catch(NotSupportedException error){failure=error;Fail(x=>x with{State="Conflict",Error=error.Message+" 可另存为新文件。",LastCheckedUtc=null});}
        catch(Exception error){failure=error;HandleUploadFailure(selected.Id,error,preparing,stopping,Fail);}
        finally
        {
            if(capacity is not null)
            {if(!receiptSaved&&sendingStarted)capacity.Invalidate();capacity.Dispose();}
            Task canceled;lock(runtime.Sync){runtime.Request=null;runtime.CurrentId=null;canceled=runtime.RequestCancellation;}
            await canceled.ConfigureAwait(false);runtime.PulseIdle();
        }
    }
    /// <summary>上传任务所有者统一落库失败阶段；退出取消与已发送的未知结果优先，真实瞬时拒绝按持久退避调度。</summary>
    private void HandleUploadFailure(string id,Exception error,bool preparing,CancellationToken stopping,Func<Func<LocalUpload,LocalUpload>,LocalUpload?>? change=null)
    {
        LocalUpload? Update(Func<LocalUpload,LocalUpload> update)=>change is null?ChangeUpload(id,update,error):change(update);
        var current=_repository.Upload(id);if(current is null||current.State=="Excluded")return;
        if(current.State=="RemoteCommitted")Update(x=>x with{Error="远端已写入，本地状态尚待提交："+error.Message,LastCheckedUtc=DateTimeOffset.UtcNow});
        else if(error is SourceRequestRejectedException)Update(x=>x.CancelRequested?x with{State="Canceled"}:UploadRules.IsTransient(error)?UploadRules.WaitForTransient(x,error,DateTimeOffset.UtcNow):x with{State="Failed",Error=error.Message,LastCheckedUtc=null,NextAttemptUtc=null});
        else if(UploadRules.IsPermanentConnectionFailure(error)&&(error is not SourceOutcomeUnknownException||current.State!="Sending"))Update(x=>x with{State="Failed",Error=error.Message,LastCheckedUtc=null,NextAttemptUtc=null});
        else if(current.State=="Sending")Update(x=>x with{State="OutcomeUnknown",Error="写入结果待核对："+error.Message,LastCheckedUtc=stopping.IsCancellationRequested?null:DateTimeOffset.UtcNow});
        else if(current.CancelRequested)Update(x=>x with{State="Canceled",Error="本代上传已取消。"});
        else if(error is OperationCanceledException&&stopping.IsCancellationRequested)Update(x=>x with{State=x.SnapshotPath is null?"Queued":"Prepared",LastCheckedUtc=null});
        else if(UploadRules.IsTransient(error))Update(x=>UploadRules.WaitForTransient(x,error,DateTimeOffset.UtcNow));
        else Update(x=>x with{State=preparing&&error is IOException or System.ComponentModel.Win32Exception?"Waiting":"Failed",Error=error.Message,LastCheckedUtc=DateTimeOffset.UtcNow,NextAttemptUtc=preparing&&error is IOException or System.ComponentModel.Win32Exception?DateTimeOffset.UtcNow.AddSeconds(3):null});
    }
    /// <summary>未知结果先核对远端；仅经验证的条件写在安全基线下有限重试。</summary>
    private async Task ReconcileUploadAsync(Runtime root,LocalUpload item,CancellationToken token)
    {
        if(item.SnapshotPath is null&&!item.IsDirectory){ChangeUpload(item.Id,x=>x with{State="Conflict",Error="结果不明且缺少不可变快照，请保留本地文件并另存副本。"});return;}
        using var capacity=await _capacities.EnterAsync(FileSourceFactory.CapacityWriteScope(root.Definition),token).ConfigureAwait(false);capacity.Invalidate();
        try
        {
            var metadata=await root.Source!.TryGetMetadataAsync(item.SourcePath,token).ConfigureAwait(false);
            if(metadata is null)
            {
                if(!item.IsDirectory&&item.ExpectedVersion is null&&RetryConditionalUpload(root,item,true))return;
                ChangeUpload(item.Id,current=>current with{State=current.CancelRequested?"Canceled":"Conflict",Error="发送结果不明且远端目标不存在，无法区分未写入与写入后被删除；请显式重试或另存副本。",LastCheckedUtc=null});return;
            }
            if(item.IsDirectory&&metadata.IsDirectory||!item.IsDirectory&&!metadata.IsDirectory&&metadata.Size==item.Total&&await HashRemoteAsync(root.Source,metadata,token).ConfigureAwait(false)==item.SnapshotSha256)
            {
                var saved=ChangeUpload(item.Id,x=>x with{State="RemoteCommitted",Receipt=metadata,Error=x.Error==UploadRules.TypeConflictMessage?x.Error:null,LastCheckedUtc=null})!;
                await CommitUploadAsync(root,saved,token).ConfigureAwait(false);WakeCapacityWaiters(FileSourceFactory.CapacityWriteScope(root.Definition));return;
            }
            var unchanged=item.ExpectedVersion is not null&&root.Source.IsSameVersion(item.ExpectedVersion,metadata.Version);
            if(unchanged&&item.BaselineSha256 is not null)unchanged=await HashRemoteAsync(root.Source,metadata,token).ConfigureAwait(false)==item.BaselineSha256;
            if(unchanged&&!item.IsDirectory&&RetryConditionalUpload(root,item,false))return;
            ChangeUpload(item.Id,x=>x with{State=unchanged?(x.CancelRequested?"Canceled":x.Attention?.RetryRequested==true?"Prepared":"Failed"):"Conflict",Error=unchanged?"远端仍为原始版本，请显式重试后发送。":"远端内容与原始基线、发送快照均不一致，保留两端内容。",LastCheckedUtc=null});
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(SourceConflictException error){ChangeUpload(item.Id,x=>x with{State="Conflict",Error=error.Message,LastCheckedUtc=null},error);}
        catch(Exception error){ChangeUpload(item.Id,x=>x with{State="OutcomeUnknown",Error="只读核对未完成："+error.Message,LastCheckedUtc=DateTimeOffset.UtcNow},error);}
    }
    /// <summary>只读核对完成后有限重排已验证的条件写；取消或超出次数时不增加发送，不更换原始基线。</summary>
    private bool RetryConditionalUpload(Runtime root,LocalUpload item,bool create)
    {
        if(RenameOwnsUpload(item))return false;
        if((create?root.Source!.WriteCapabilities.CreateOnly:root.Source!.WriteCapabilities.ConditionalUpdate)!=CapabilitySupport.Supported)return false;
        var changed=false;
        ChangeUpload(item.Id,current=>
        {
            if(current.CancelRequested){changed=true;return current with{State="Canceled",LastCheckedUtc=null};}
            if(current.AutoRetryCount>=5)return current;
            var now=DateTimeOffset.UtcNow;var next=now+UploadRules.TransientDelay(current.AutoRetryCount);
            changed=true;return current with{State="Waiting",AutoRetryCount=current.AutoRetryCount+1,Bytes=0,Error=$"远端仍为发送前状态，下次重试 {next.ToLocalTime():HH:mm:ss}。",LastCheckedUtc=now,NextAttemptUtc=next};
        });return changed;
    }
    private static async Task<string> HashRemoteAsync(IFileSource source,SourceEntry metadata,CancellationToken token)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await source.ReadRangeSequentialAsync(metadata,0,metadata.Size,(bytes,cancel)=>{cancel.ThrowIfCancellationRequested();hash.AppendData(bytes.Span);return ValueTask.CompletedTask;},131072,token).ConfigureAwait(false);
        var after=await source.GetMetadataAsync(metadata.Path,token).ConfigureAwait(false);if(!source.IsSameVersion(metadata.Version,after.Version))throw new SourceConflictException("远端核对期间版本发生变化。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
