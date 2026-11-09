using HybridMount.Common;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>每根串行消费，网络不持根锁；失败只阻挡相关文件，未知结果只自动核对一次。</summary>
    private async Task<bool> TryRunExcludedRenameAsync(Runtime root,UploadRuntime runtime,CancellationToken token)
    {
        var row=RenameIntents(root.Definition.Id).Where(x=>x.Runnable&&(root.SchedulingEnabled||x.ReadOnlyRequested||x.CancelRequested)&&
            !(x.Stage=="WaitingUpload"&&x.UploadId is {} id&&_repository.Upload(id) is {} upload&&UploadRules.InFlight(upload)))
            .OrderByDescending(x=>x.RetryRequested||x.ReadOnlyRequested).ThenBy(x=>x.CreatedUtc).FirstOrDefault();
        if(row is null)return false;
        var uploadCandidate=UploadRules.NextUpload(UploadCandidates(root),DateTimeOffset.UtcNow);
        var user=row.RetryRequested||row.ReadOnlyRequested;
        if(uploadCandidate is not null&&(uploadCandidate.ExplicitRequest&&!user||uploadCandidate.ExplicitRequest==user&&uploadCandidate.ChangedUtc<row.CreatedUtc))return false;
        using var request=CancellationTokenSource.CreateLinkedTokenSource(token);
        lock(runtime.Sync){if(runtime.PropertyPath is not null||runtime.ConflictWaiters>0){return false;}runtime.CurrentId=row.Id;runtime.Request=request;runtime.RequestCancellation=Task.CompletedTask;}
        try{await RunExcludedRenameAsync(root,row,request.Token).ConfigureAwait(false);}
        finally{Task cancellation;lock(runtime.Sync){runtime.CurrentId=null;runtime.Request=null;cancellation=runtime.RequestCancellation;}await cancellation.ConfigureAwait(false);runtime.PulseIdle();}
        return true;
    }
    /// <summary>跨会话、账户或根身份不沿用原改名授权；只有同一连接作用域才能收尾。</summary>
    private void ValidateRenameSession(Runtime root,ExcludedRenameIntent row,IFileSource source,CloudMountSession session,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();if(root.RemovalRequested||root.Definition.RemovalPending||root.Session!=session||root.Source!=source)throw new OperationCanceledException(token);
        if(row.SourceScope!=ExcludedRenameIntent.ScopeFor(root.Definition))throw new SourceConflictException("连接配置或凭据作用域已变化，不能在新连接上执行旧改名。");
        if(row.RootPath!=root.Definition.LocalPath||row.RootIdentity!=root.Definition.RootIdentity||row.Owner!=ExcludedRenameJournal.CurrentOwner)throw new IOException("改名收尾的同步根或账户身份已改变。");
        MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
    }
    /// <summary>Moving先落盘；任何未知结果均只读核对两端及回执，显式重试才允许继续写操作。</summary>
    private async Task RunExcludedRenameAsync(Runtime root,ExcludedRenameIntent initial,CancellationToken token)
    {
        using var queueTask=TasksQueue.EnterTask(initial.MountId,initial.Id,initial.RetryRequested||initial.ReadOnlyRequested?MountingPriority.User:MountingPriority.Transaction,"excludedRename",initial.SourcePath);
        var journal=RenameJournal(initial.MountId);var row=initial;var source=root.Source!;var session=root.Session!;var readingOutcome=false;
        try
        {
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                row=journal.Read().First(x=>x.Id==row.Id);ValidateRenameSession(root,row,source,session,token);
                if(row.Stage=="Moving")row=RenameChange(row,x=>x with{Stage="OutcomeUnknown",CheckedUtc=null,Error="上次远端改名已进入发送阶段，先核对两端。",IssueUtc=x.IssueUtc??DateTimeOffset.UtcNow});
                if(row.CancelRequested&&!row.MoveStarted&&!row.RemoteMoved){CancelExcludedRenameCore(root,row);return;}
                if(row.UploadId is {} uploadId&&_repository.Upload(uploadId) is {} upload)
                {
                    if(UploadRules.InFlight(upload)){RenameChange(row,x=>x with{Stage="WaitingUpload",CheckedUtc=DateTimeOffset.UtcNow});return;}
                    if(row.WaitForUploadResult&&upload.State=="Completed"&&upload.ExpectedVersion is {} written)
                        row=RenameChange(row,x=>x with{ExpectedVersion=written,EntryId=x.EntryId??upload.EntryId??upload.Id,WaitForUploadResult=false});
                }
            }
            finally{root.Gate.Release();}
            if(row.ReadOnlyRequested||row.MoveStarted&&!row.RemoteMoved)
            {
                var readOnly=row.ReadOnlyRequested;readingOutcome=true;row=await InspectExcludedMoveAsync(root,row,source,session,token).ConfigureAwait(false);
                if(readOnly)return;
                if(!row.RemoteMoved&&row.Stage!="PreparingMove")return;
                readingOutcome=false;
            }
            if(row.WaitForUploadResult&&row.ExpectedVersion is null)
            {
                if(await source.TryGetMetadataAsync(row.SourcePath,token).ConfigureAwait(false) is not null)throw new SourceConflictException("原上传结果未确认，不能移动同名云端对象。");
                row=RenameChange(row,x=>x with{WaitForUploadResult=false});
            }
            if(!row.RemoteMoved&&row.ExpectedVersion is not null)
            {
                var old=await source.GetMetadataAsync(row.SourcePath,token).ConfigureAwait(false);
                if(old.IsDirectory||!source.IsSameVersion(old.Version,row.ExpectedVersion))throw new SourceConflictException("远端源版本变化，保留两端。");
                if(await source.TryGetMetadataAsync(row.LocalPath,token).ConfigureAwait(false) is not null)throw new SourceConflictException("远端改名目标已存在，未覆盖。");
                if(source is not IExcludedRenameSource mover)throw new NotSupportedException("此文件源尚不支持可恢复的条件改名。");
                row=RenameChange(row,x=>x with{Stage="PreparingMove"});await RenameCheckpoint("BeforeMove",row).ConfigureAwait(false);
                await root.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    row=journal.Read().First(x=>x.Id==row.Id);ValidateRenameSession(root,row,source,session,token);
                    if(row.CancelRequested||!FileExclusionRules.Matches(root.Definition.ExclusionRules,row.LocalPath)){CancelExcludedRenameCore(root,row);return;}
                    var path=RelativePaths.ToLocal(root.Definition.LocalPath,row.LocalPath);MountPathGuard.ValidateOperation(root.Definition,path);
                    if(LocalFileState.Read(path).Identity!=row.FileId)throw new SourceConflictException("改名目标身份已变化，未操作远端。");
                    var started=journal.Update(row.Id,x=>x with{Stage="Moving",MoveStarted=true,DestinationPath=x.LocalPath,RetryRequested=false,CheckedUtc=null},row);
                    if(started is null)return;row=started;PublishRename(row);
                }
                finally{root.Gate.Release();}
                var receipt=await mover.MoveForExclusionAsync(row.SourcePath,row.DestinationPath!,row.ExpectedVersion??throw new IOException("缺少已确认源版本。"),async value=>
                {
                    // S3在源删除前交付；即便断开或取消，也先保全已确认的目标版本。
                    row=RenameChange(row,x=>x with{MoveReceipt=value,Stage="Copied",CheckedUtc=null});await RenameCheckpoint("MoveReceipt",row).ConfigureAwait(false);
                },token).ConfigureAwait(false);
                row=RenameChange(row,x=>x with{MoveReceipt=receipt,RemoteMoved=true,Stage="RemoteMoved",Error=null,CheckedUtc=null});InvalidateCapacity(root);
                await RenameCheckpoint("Moved",row).ConfigureAwait(false);
            }
            await LocalizeExcludedRenameAsync(root,row,source,session,token).ConfigureAwait(false);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or OperationCanceledException or NotSupportedException)
        {
            var latest=journal.Read().First(x=>x.Id==row.Id);var uncertain=latest.MoveStarted&&!latest.RemoteMoved;
            var explicitReject=error is SourceConflictException or SourceRequestRejectedException or NotSupportedException;
            if(error is OperationCanceledException&&!uncertain){RenameChange(latest,x=>x with{Stage=x.RemoteMoved?"RemoteMoved":"Queued",CheckedUtc=null});return;}
            var state=uncertain&&!explicitReject?latest.MoveReceipt is null?"OutcomeUnknown":"Copied":error is SourceConflictException or NotSupportedException?"Conflict":"Failed";
            RenameChange(latest,x=>x with{Stage=state,MoveStarted=x.RemoteMoved||x.MoveReceipt is not null||uncertain&&!explicitReject,RetryRequested=false,ReadOnlyRequested=false,Error=error.Message,IssueUtc=x.IssueUtc??DateTimeOffset.UtcNow,CheckedUtc=state is "OutcomeUnknown" or "Copied"&&!readingOutcome?null:DateTimeOffset.UtcNow},error);
            if(uncertain)InvalidateCapacity(root);
        }
    }
    /// <summary>只读核对结果；仅S3已持久回执的复制允许显式继续删源，绝不重新复制目标。</summary>
    private async Task<ExcludedRenameIntent> InspectExcludedMoveAsync(Runtime root,ExcludedRenameIntent row,IFileSource source,CloudMountSession session,CancellationToken token)
    {
        var old=await source.TryGetMetadataAsync(row.SourcePath,token).ConfigureAwait(false);
        var target=await source.TryGetMetadataAsync(row.DestinationPath??row.LocalPath,token).ConfigureAwait(false);
        ValidateRenameSession(root,row,source,session,token);
        var original=old is not null&&row.ExpectedVersion is not null&&source.IsSameVersion(old.Version,row.ExpectedVersion);
        if(row.MoveReceipt is {} receipt)
        {
            if(target is null||target.IsDirectory||!source.IsSameVersion(target.Version,receipt.Version))throw new SourceConflictException("远端改名目标与回执不符，未继续移动或覆盖。");
            if(old is not null&&!original)throw new SourceConflictException("原路径出现其他版本，保留新对象，停止源移除。");
            if(old is null)return RenameChange(row,x=>x with{RemoteMoved=true,Stage=x.ReadOnlyRequested?"Failed":"RemoteMoved",ReadOnlyRequested=false,RetryRequested=false,CheckedUtc=x.ReadOnlyRequested?DateTimeOffset.UtcNow:null,Error=x.ReadOnlyRequested?x.Error??"远端改名已确认，请显式重试本地收尾。":x.Error,IssueUtc=x.ReadOnlyRequested?x.IssueUtc??DateTimeOffset.UtcNow:x.IssueUtc,Verification="目标版本与回执一致，原路径已不存在。"});
            if(row.CancelRequested&&!row.ReadOnlyRequested)
            {
                await root.Gate.WaitAsync(token).ConfigureAwait(false);
                try{ValidateRenameSession(root,row,source,session,token);var entry=_repository.RebindExcludedRename(row);row=RenameChange(row,x=>x with{EntryId=entry.Id,MappingRebound=true,Verification="目标副本已保留，原云路径未移除。"});CancelExcludedRenameCore(root,row);return RenameJournal(row.MountId).Read().First(x=>x.Id==row.Id);}
                finally{root.Gate.Release();}
            }
            if(row.RetryRequested&&!row.ReadOnlyRequested&&source is S3FileSource)
            {
                await RenameCheckpoint("BeforeSourceDelete",row).ConfigureAwait(false);
                await root.Gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    row=RenameJournal(row.MountId).Read().First(x=>x.Id==row.Id);ValidateRenameSession(root,row,source,session,token);
                    if(row.CancelRequested)return RenameChange(row,x=>x with{RetryRequested=false,Stage="Copied",CheckedUtc=DateTimeOffset.UtcNow});
                    row=RenameChange(row,x=>x with{Stage="Moving",RetryRequested=false,CheckedUtc=null});
                }
                finally{root.Gate.Release();}
                await source.DeleteAsync(row.SourcePath,row.ExpectedVersion!,token).ConfigureAwait(false);
                if(await source.TryGetMetadataAsync(row.SourcePath,token).ConfigureAwait(false) is not null)throw new SourceConflictException("源移除后路径又出现，保留新对象。");
                return RenameChange(row,x=>x with{RemoteMoved=true,Stage="RemoteMoved",CheckedUtc=null});
            }
            return RenameChange(row,x=>x with{Stage="Copied",ReadOnlyRequested=false,RetryRequested=false,CheckedUtc=DateTimeOffset.UtcNow,Error="目标已确认，原版本仍存在；可显式重试源收尾，取消不会撤销已复制目标。",IssueUtc=x.IssueUtc??DateTimeOffset.UtcNow,Verification="目标与回执一致，原版本仍存在。"});
        }
        if(!row.MoveStarted)
            return RenameChange(row,x=>x with{ReadOnlyRequested=false,CheckedUtc=DateTimeOffset.UtcNow,Verification=original&&target is null?"仍为改名前状态；可显式重试。":"两端已读取，未重新授权写入。"});
        if(original&&target is null)
        {
            if(row.CancelRequested){CancelExcludedRenameCore(root,row);return RenameJournal(row.MountId).Read().First(x=>x.Id==row.Id);}
            if(row.RetryRequested&&!row.ReadOnlyRequested)return RenameChange(row,x=>x with{MoveStarted=false,Stage="PreparingMove",CheckedUtc=null,RetryRequested=false});
        }
        return RenameChange(row,x=>x with{Stage="OutcomeUnknown",ReadOnlyRequested=false,RetryRequested=false,CheckedUtc=DateTimeOffset.UtcNow,Error="未取得可靠移动回执，保留两端；不能根据名称猜测或自动重发。",IssueUtc=x.IssueUtc??DateTimeOffset.UtcNow,Verification=$"原路径{(old is null?"不存在":"存在")}，目标{(target is null?"不存在":"存在")}。"});
    }
    /// <summary>远端已确认后切换下载映射，仅补齐缺失范围；本地改动不上传也不被远端正文覆盖。</summary>
    private async Task LocalizeExcludedRenameAsync(Runtime root,ExcludedRenameIntent row,IFileSource source,CloudMountSession session,CancellationToken token)
    {
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            row=RenameJournal(row.MountId).Read().First(x=>x.Id==row.Id);ValidateRenameSession(root,row,source,session,token);
            if(row.RemoteMoved&&row.MoveReceipt is {} receipt)
            {
                var entry=_repository.RebindExcludedRename(row);row=RenameChange(row,x=>x with{EntryId=entry.Id,MappingRebound=true});CompleteOwnUploadEcho(root,receipt);
                if(!row.CancelRequested&&row.LocalPath!=row.DestinationPath&&FileExclusionRules.Matches(root.Definition.ExclusionRules,row.LocalPath))
                {
                    var next=new ExcludedRenameIntent(Guid.NewGuid().ToString("N"),row.MountId,row.RootPath,row.RootIdentity,row.Owner,row.FileId,row.DestinationPath!,row.LocalPath,row.DestinationPath!,entry.Id,receipt.Version,null,0,"Queued",DateTimeOffset.UtcNow,OriginalVersion:receipt.Version,InitialTarget:row.LocalPath,SourceScope:row.SourceScope);
                    next=RenameJournal(row.MountId).Continue(row,next);PublishRename(RenameJournal(row.MountId).Read().First(x=>x.Id==row.Id));PublishRename(next);return;
                }
            }
            if(row.CancelRequested||!FileExclusionRules.Matches(root.Definition.ExclusionRules,row.LocalPath)){CancelExcludedRenameCore(root,row);return;}
            if(LocalFileState.Read(RelativePaths.ToLocal(root.Definition.LocalPath,row.LocalPath)).Identity!=row.FileId)throw new SourceConflictException("本地改名文件身份已变化，未替换本地内容。");
            row=RenameChange(row,x=>x with{Stage="Downloading",CheckedUtc=null});
        }
        finally{root.Gate.Release();}
        await RenameCheckpoint("BeforeLocalize",row).ConfigureAwait(false);
        var local=RelativePaths.ToLocal(root.Definition.LocalPath,row.LocalPath);MountPathGuard.ValidateOperation(root.Definition,local);var length=new FileInfo(local).Length;
        if(CloudFilesNative.Inspect(local) is not null&&!CloudFilesNative.HasFullContent(local,length))
        {
            if(row.MoveReceipt is null||row.EntryId is null)throw new IOException("占位文件缺少已确认的新云路径，保留现场。");
            var metadata=await source.GetMetadataAsync(row.DestinationPath!,token).ConfigureAwait(false);
            if(metadata.IsDirectory||!source.IsSameVersion(metadata.Version,row.MoveReceipt.Version))throw new SourceConflictException("下载前远端目标版本变化，保留两端。");
            using(session.PrioritizeDownload(row.EntryId))await session.Runtime.InvokeAsync("hydrateFile",session.EntryArgs(new MountEntry(row.EntryId,row.DestinationPath!,row.LocalPath,row.MoveReceipt.Version,false,false,true,true)),token:token).ConfigureAwait(false);
            if(!CloudFilesNative.HasFullContent(local,length))throw new IOException("改名文件尚未完整下载，云端新文件保持。");
        }
        var proof=await CloudExcludedRenameLocalizer.VerifyAsync(root.Definition,row,token).ConfigureAwait(false);
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current=RenameJournal(row.MountId).Read().First(x=>x.Id==row.Id);ValidateRenameSession(root,current,source,session,token);
            if(current.LocalPath!=row.LocalPath)return;
            if(current.CancelRequested||!FileExclusionRules.Matches(root.Definition.ExclusionRules,current.LocalPath)){CancelExcludedRenameCore(root,current);return;}
            row=RenameChange(current,x=>x with{Stage="Localizing",LocalSha256=proof.Sha256});CloudExcludedRenameLocalizer.Detach(root.Definition,row,proof);
            ReconcileFileExclusion(root,row.LocalPath,LocalFileState.Read(local),true,token);
            if(CloudFilesNative.Inspect(local) is not null||CloudShellState.Read(local).ProviderState!=9)throw new IOException("本地排除状态未确认；远端已改名，待继续本地收尾。");
            row=RenameChange(row,x=>x with{LocalDetached=true});_repository.RetireExcludedRename(row);
            if(row.UploadId is {} oldId&&_repository.Upload(oldId) is {Attention.ResolvedUtc:not null} oldUpload&&oldUpload.ObservedStamp.Split(':')[0]==row.FileId)
                await CloudUploadIssueProperty.SetAsync(root.Definition,oldUpload with{LocalPath=row.LocalPath,ObservedStamp=LocalFileState.Read(local).Key},
                    stamp=>stamp.Identity==row.FileId,(_,_)=>ChangeUpload(oldId,x=>x.Generation==oldUpload.Generation&&x.Attention?.ResolvedUtc is not null?x with{Attention=x.Attention with{ShellState="Cleared",ShellError=null}}:x),token,"").ConfigureAwait(false);
            row=RenameChange(row,x=>x with{Stage="Completed",Error=null,RetryRequested=false,ReadOnlyRequested=false});
            root.Uploads?.RequestFullScan();root.RemoteSync?.Wake();
        }
        finally{root.Gate.Release();}
        CloudFilesNative.NotifyItemUpdated(local);await RenameCheckpoint("LocalDetached",row).ConfigureAwait(false);
    }
    /// <summary>取消不回滚远端移动；已确认目标保留下载映射，未发送时保留原云路径。</summary>
    private void CancelExcludedRenameCore(Runtime root,ExcludedRenameIntent row)
    {
        RenameChange(row,x=>x with{Stage="Canceled",Error=null,RetryRequested=false,ReadOnlyRequested=false});root.Uploads?.RequestFullScan();
    }
}
