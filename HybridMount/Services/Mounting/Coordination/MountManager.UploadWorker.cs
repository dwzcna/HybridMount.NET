using System.Security.Cryptography;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>每根串行上传、全局两路；网络传输期间不持有根锁，窗口和读取不被队列阻塞。</summary>
    private async Task UploadLoopAsync(Runtime root,UploadRuntime runtime)
    {
        var token=runtime.Stop.Token;var scanned=DateTimeOffset.MinValue;
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
                if(Interlocked.Exchange(ref runtime.Dirty,0)!=0||DateTimeOffset.UtcNow-scanned>TimeSpan.FromSeconds(60))
                {await ScanLocalUploadsAsync(root,token).ConfigureAwait(false);scanned=DateTimeOffset.UtcNow;}
                var item=_repository.Uploads(root.Definition.Id).Where(x=>
                    (x.State=="OutcomeUnknown"||x.State=="RemoteCommitted"&&string.IsNullOrEmpty(x.Error)||(x.State is "Queued" or "Waiting" or "Prepared"&&(root.SchedulingEnabled||x.ExplicitRequest)&&!x.CancelRequested&&!_repository.HasPendingRemote(root.Definition.Id,x.SourcePath)))&&
                    DateTimeOffset.UtcNow-x.ChangedUtc>=TimeSpan.FromSeconds(2)&&
                    (x.LastCheckedUtc is null||DateTimeOffset.UtcNow-x.LastCheckedUtc>=TimeSpan.FromSeconds(x.State is "OutcomeUnknown" or "WaitingCapacity"?60:3)))
                    .OrderBy(x=>x.SourcePath.Count(c=>c=='/')).ThenBy(x=>x.IsDirectory?0:1).ThenBy(x=>x.ChangedUtc).FirstOrDefault();
                if(item is null){await runtime.Signal.WaitAsync(500,token).ConfigureAwait(false);continue;}
                await RunUploadAsync(root,runtime,item,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){}
        catch(Exception error){_logger.Queue("自动上传","Error",error.Message);Status(root,root.Status,"自动上传监控已停止，请重新连接。" );}
    }
    private sealed class UploadReporter(Action<UploadProgress> action) : IProgress<UploadProgress>
    {public void Report(UploadProgress value)=>action(value);}
    /// <summary>发送前持久化快照及阶段；返回成功后先保存回执，再修改本地占位和索引。</summary>
    private async Task RunUploadAsync(Runtime root,UploadRuntime runtime,LocalUpload selected,CancellationToken stopping)
    {
        using var request=CancellationTokenSource.CreateLinkedTokenSource(stopping);
        lock(runtime.Sync){if(runtime.PropertyPath is not null)return;runtime.CurrentId=selected.Id;runtime.Request=request;runtime.RequestCancellation=Task.CompletedTask;}
        var token=request.Token;LocalUpload? prepared=selected;var preparing=true;UploadCapacityPermit? capacity=null;var transferSlot=false;var receiptSaved=false;var sendingStarted=false;
        LocalUpload? Fail(Func<LocalUpload,LocalUpload> update)=>ChangeUpload(selected.Id,current=>preparing&&current!=prepared?current:update(current));
        try
        {
            prepared=_repository.Upload(selected.Id);if(prepared is null)return;
            if(prepared.State=="OutcomeUnknown"){await ReconcileUploadAsync(root,prepared,token).ConfigureAwait(false);return;}
            if(prepared.State=="RemoteCommitted"){await CommitUploadAsync(root,prepared,token).ConfigureAwait(false);return;}
            if(prepared.CancelRequested||(!root.SchedulingEnabled&&!prepared.ExplicitRequest))return;
            await UploadCheckpointAsync("BeforePrepare",prepared).ConfigureAwait(false);
            prepared=await PrepareUploadAsync(root,prepared,token).ConfigureAwait(false);
            if(prepared is null)return;preparing=false;
            await UploadCheckpointAsync("Prepared",prepared).ConfigureAwait(false);
            capacity=await EnterUploadCapacityAsync(root,prepared,token).ConfigureAwait(false);
            await _uploadSlots.WaitAsync(token).ConfigureAwait(false);transferSlot=true;
            await PrepareUploadDestinationAsync(root,prepared,token,capacity.Definition).ConfigureAwait(false);
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
        catch(CapacitySettingsChangedException){ChangeUpload(selected.Id,x=>x.State=="Prepared"?x with{LastCheckedUtc=null}:x);}
        catch(CapacityWaitingException error){ChangeUpload(selected.Id,x=>x.CancelRequested?x with{State="Canceled"}:x.State=="Prepared"?x with{State="WaitingCapacity",Error=error.Message,LastCheckedUtc=DateTimeOffset.UtcNow}:x);}
        catch(SourceRequestRejectedException error){Fail(x=>x with{State="Failed",Error=error.Message,LastCheckedUtc=null});}
        catch(SourceConnectionException error)when(error.Diagnostic.HttpStatus is >=400 and <500){Fail(x=>x with{State="Failed",Error=error.Message,LastCheckedUtc=null});}
        catch(SourceConflictException error){Fail(x=>x with{State="Conflict",Error=error.Message,LastCheckedUtc=null});}
        catch(NotSupportedException error){Fail(x=>x with{State="Conflict",Error=error.Message+" 可另存为新文件。",LastCheckedUtc=null});}
        catch(Exception error)
        {
            var current=_repository.Upload(selected.Id);
            if(current is not null)
            {
                if(current.State=="RemoteCommitted")Fail(x=>x with{Error="远端已写入，本地状态尚待提交："+error.Message,LastCheckedUtc=DateTimeOffset.UtcNow});
                else if(current.State=="Sending"||error is SourceOutcomeUnknownException)
                    Fail(x=>x with{State="OutcomeUnknown",Error="写入结果待核对："+error.Message,LastCheckedUtc=stopping.IsCancellationRequested?null:DateTimeOffset.UtcNow});
                else if(current.CancelRequested)Fail(x=>x with{State="Canceled",Error="本代上传已取消。"});
                else if(error is OperationCanceledException&&stopping.IsCancellationRequested)Fail(x=>x with{State=x.SnapshotPath is null?"Queued":"Prepared",LastCheckedUtc=null});
                else Fail(x=>x with{State=preparing&&error is IOException or System.ComponentModel.Win32Exception?"Waiting":"Failed",Error=error.Message,LastCheckedUtc=DateTimeOffset.UtcNow});
            }
        }
        finally
        {
            if(capacity is not null)
            {if(!receiptSaved&&sendingStarted)capacity.Invalidate();capacity.Dispose();}
            if(transferSlot)_uploadSlots.Release();
            Task canceled;lock(runtime.Sync){runtime.Request=null;runtime.CurrentId=null;canceled=runtime.RequestCancellation;}
            await canceled.ConfigureAwait(false);
        }
    }
    /// <summary>未知结果只能用远端内容核对；读取失败保留快照，绝不直接重复发送。</summary>
    private async Task ReconcileUploadAsync(Runtime root,LocalUpload item,CancellationToken token)
    {
        if(item.SnapshotPath is null&&!item.IsDirectory){ChangeUpload(item.Id,x=>x with{State="Conflict",Error="结果不明且缺少不可变快照，请保留本地文件并另存副本。"});return;}
        using var capacity=await _capacities.EnterAsync(FileSourceFactory.CapacityWriteScope(root.Definition),token).ConfigureAwait(false);capacity.Invalidate();
        try
        {
            var metadata=await root.Source!.TryGetMetadataAsync(item.SourcePath,token).ConfigureAwait(false);
            if(metadata is null)
            {ChangeUpload(item.Id,current=>current with{State="Conflict",Error="发送结果不明且远端目标不存在，无法区分未写入与写入后被删除；请显式重试或另存副本。",LastCheckedUtc=null});return;}
            if(item.IsDirectory&&metadata.IsDirectory||!item.IsDirectory&&!metadata.IsDirectory&&metadata.Size==item.Total&&await HashRemoteAsync(root.Source,metadata,token).ConfigureAwait(false)==item.SnapshotSha256)
            {
                var saved=ChangeUpload(item.Id,x=>x with{State="RemoteCommitted",Receipt=metadata,Error=x.Error==UploadRules.TypeConflictMessage?x.Error:null,LastCheckedUtc=null})!;
                await CommitUploadAsync(root,saved,token).ConfigureAwait(false);WakeCapacityWaiters(FileSourceFactory.CapacityWriteScope(root.Definition));return;
            }
            var unchanged=item.ExpectedVersion is not null&&root.Source.IsSameVersion(item.ExpectedVersion,metadata.Version);
            if(unchanged&&item.BaselineSha256 is not null)unchanged=await HashRemoteAsync(root.Source,metadata,token).ConfigureAwait(false)==item.BaselineSha256;
            ChangeUpload(item.Id,x=>x with{State=unchanged?(x.CancelRequested?"Canceled":x.Attention?.RetryRequested==true?"Prepared":"Failed"):"Conflict",Error=unchanged?"远端仍为原始版本，请显式重试后发送。":"远端内容与原始基线、发送快照均不一致，保留两端内容。",LastCheckedUtc=null});
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(SourceConflictException error){ChangeUpload(item.Id,x=>x with{State="Conflict",Error=error.Message,LastCheckedUtc=null});}
        catch(Exception error){ChangeUpload(item.Id,x=>x with{State="OutcomeUnknown",Error="只读核对未完成："+error.Message,LastCheckedUtc=DateTimeOffset.UtcNow});}
    }
    private static async Task<string> HashRemoteAsync(IFileSource source,SourceEntry metadata,CancellationToken token)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[131072];long offset=0;
        while(offset<metadata.Size){var count=await source.ReadRangeAsync(metadata,offset,buffer.AsMemory(0,(int)Math.Min(buffer.Length,metadata.Size-offset)),token).ConfigureAwait(false);if(count<=0)throw new IOException("远端核对提前结束。");hash.AppendData(buffer,0,count);offset+=count;}
        var after=await source.GetMetadataAsync(metadata.Path,token).ConfigureAwait(false);if(!source.IsSameVersion(metadata.Version,after.Version))throw new SourceConflictException("远端核对期间版本发生变化。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
