using System.Collections.Concurrent;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>一个文件操作尝试拥有展示和取消证据；网络/FETCH的实际所有者仍在会话内，资源释放后才发布终态。</summary>
    private sealed class FileOperation(Runtime root,CloudMountSession session,MountEntry entry,MountOperationSnapshot snapshot)
    {
        internal readonly Runtime Root=root;internal readonly CloudMountSession Session=session;internal readonly MountEntry Entry=entry;
        internal readonly object Sync=new();internal MountOperationSnapshot Snapshot=snapshot;
        internal (string? Stage,int? Code,string? Message)? LastLogged=snapshot.State is "Deferred" or "Failed"?(snapshot.FailureStage,snapshot.FailureHResult,snapshot.Error):null;
        internal readonly long Generation=DateTimeOffset.UtcNow.UtcTicks;
        internal bool Canceling;internal Action? StopDownload;internal Task? CancelWork;internal readonly long Started=System.Diagnostics.Stopwatch.GetTimestamp();
    }
    private readonly ConcurrentDictionary<string,FileOperation> _fileOperations=new();
    private readonly object _fileOperationCreation=new();
    private readonly ConcurrentDictionary<Guid,SemaphoreSlim> _releaseGates=new();
    private FileOperation FileTask(Runtime root,CloudMountSession session,MountEntry entry,string operation,string id,bool renew=false)
    {
        lock(_fileOperationCreation)
        {
            if(_fileOperations.TryGetValue(id,out var active)&&ReferenceEquals(active.Session,session)&&active.Snapshot.State is not("Completed" or "Canceled" or "Failed" or "Superseded")&&(!renew||active.Canceling||operation=="freeSpace"))return active;
            var file=LocalFileState.Read(RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath));
            var previous=renew?null:_repository.FindRecoverableFileOperation(id,root.Definition.Id,entry,operation);
            var snapshot=previous??new MountOperationSnapshot(id,operation,"Running",0,1,[],null,root.Definition.Id,entry.LocalPath,TotalBytes:entry.IsDirectory?0:file.Length,IsDirectory:entry.IsDirectory)
            {OperationEntryId=entry.Id,OperationVersion=entry.Version,OperationFileId=file.Identity,AttemptId=Guid.NewGuid().ToString("N")};
            var task=new FileOperation(root,session,entry,snapshot);_fileOperations[id]=task;_repository.SaveOperation(snapshot);_fileDispatcher.Observe(Workflow(root,id,entry.LocalPath,operation,task.Generation,snapshot.State));return task;
        }
    }
    /// <summary>短内存锁内串行发布同一尝试；持久化成功后通知UI，取消中的迟到完成不能覆盖取消结果。</summary>
    private void PublishFileTask(FileOperation task,string state,string? stage=null,Exception? error=null,string? reason=null)
    {
        lock(task.Sync)
        {
            if(task.Canceling&&state is "Running" or "Completed")return;
            if(!_fileOperations.TryGetValue(task.Snapshot.Id,out var current)||!ReferenceEquals(current,task))return;
            var message=reason??error?.Message;
            var previous=task.Snapshot;
            var next=previous with{State=state,Completed=state=="Completed"?1:0,BytesTransferred=state=="Completed"?previous.TotalBytes:previous.BytesTransferred,DurationSeconds=state is "Completed" or "Canceled"?System.Diagnostics.Stopwatch.GetElapsedTime(task.Started).TotalSeconds:previous.DurationSeconds,Error=message,FailureStage=stage,FailureHResult=error?.HResult,RetryAfterUtc=state=="Deferred"?DateTimeOffset.UtcNow.AddSeconds(30):null};
            if(previous.State==next.State&&previous.Error==next.Error&&previous.FailureHResult==next.FailureHResult)return;
            OperationSavingForTests?.Invoke(next);_repository.SaveOperation(next);task.Snapshot=next;
            if(error is not null&&state is "Failed" or "Deferred")
            {
                var signature=(stage,(int?)error.HResult,message);
                if(task.LastLogged!=signature){_logger.QueueException("文件任务",state=="Failed"?"Error":"Warning",message!,error,previous.Id);task.LastLogged=signature;}
            }
        }
        if(state is "Completed" or "Canceled" or "Superseded" or "Failed")_fileOperations.TryRemove(new KeyValuePair<string,FileOperation>(task.Snapshot.Id,task));
        _fileDispatcher.Observe(Workflow(task.Root,task.Snapshot.Id,task.Entry.LocalPath,task.Snapshot.Operation,task.Generation,state));Notify();
    }
    /// <summary>任务页的明确取消同时撤销固定意图；低层共享等待者取消仍保留独立所有权语义。</summary>
    internal bool CancelDisplayedDownload(MountOperationSnapshot selected)
    {
        if(selected.Operation!="keepOnDevice"||selected.IsDirectory is not false||selected.MountId is not {} mount||!_roots.TryGetValue(mount,out var root)||root.Session is not {} session)return false;
        if(selected.State is "Completed" or "Canceled" or "Failed" or "Superseded")return false;
        if(_fileOperations.TryGetValue(selected.Id,out _))return CancelFileOperation(selected.Id);
        if(!_jobs.TryGetValue(selected.Id,out var job)||job.Snapshot.State is not("Queued" or "Running"))return false;
        var relative=(selected.RelativeItemPath??selected.ItemPath??"").Replace('\\','/');var entry=_repository.EntriesAtPaths(mount,[relative]).FirstOrDefault();if(entry is null)return false;
        var task=FileTask(root,session,entry,"keepOnDevice",selected.Id,renew:true);task.StopDownload=()=>{try{job.Cancel.Cancel();}catch(ObjectDisposedException){}};
        return CancelFileOperation(selected.Id);
    }
    /// <summary>按展示任务的实际EntryId定位固定所有者，旧尝试由任务页证据拦截；不依赖解析路径或任意ID前缀授权。</summary>
    private bool CancelFileOperation(string id)
    {
        if(!_fileOperations.TryGetValue(id,out var task))
        {
            var live=TasksQueue.QueueSnapshots.FirstOrDefault(row=>row.Task.Id==id&&row.Task.Operation=="keepOnDevice"&&row.Task.EntryId is not null)?.Task;
            if(live is null||!_roots.TryGetValue(live.MountId,out var root)||root.Session is not {} session||root.QueueBinding?.Generation!=live.Generation)return false;
            var entry=_repository.EntryById(live.MountId,live.EntryId!);if(entry is null)return false;
            task=FileTask(root,session,entry,"keepOnDevice",id);
        }
        lock(task.Sync)
        {
            if(task.Snapshot.Operation!="keepOnDevice")return false;
            if(task.Canceling)return true;
            if(task.Snapshot.State is "Completed" or "Canceled" or "Failed" or "Superseded")return false;
            task.Canceling=true;PublishFileTask(task,"Canceling");
            task.StopDownload?.Invoke();
            task.CancelWork=OwnAsync(async token=>
            {
                try
                {
                    if(!ReferenceEquals(task.Root.Session,task.Session)||task.Root.RemovalRequested)throw new IOException("取消前下载连接已变化。");
                    await task.Session.CancelPinnedAsync(task.Entry,task.Snapshot.OperationFileId!,token).ConfigureAwait(false);
                    if(_jobs.TryGetValue(id,out var parent))await parent.Work.ConfigureAwait(false);
                    PublishFileTask(task,"Canceled");NotifyFileState(RelativePaths.ToLocal(task.Root.Definition.LocalPath,task.Entry.LocalPath));
                }
                catch(OperationCanceledException)when(token.IsCancellationRequested){PublishFileTask(task,"Interrupted","cancelPin");}
                catch(Exception error){if(task.Root.Pins is {} pins)lock(pins.Sync)pins.Retry[task.Entry.Id]=(task.Entry.Version,DateTimeOffset.MaxValue);PublishFileTask(task,"Failed","cancelPin",error);}
            },CancellationToken.None);
            return true;
        }
    }
    /// <summary>仅重试仍匹配的释放意图；本操作只唤醒本地核验，不读取远端或解除保护。</summary>
    private bool RetryFileOperation(string id)
    {
        var snapshot=_fileOperations.TryGetValue(id,out var task)?task.Snapshot:_repository.FindOperation(id);
        if(snapshot is not {Operation:"freeSpace",OperationEntryId:{} entryId,MountId:{} mount}||!_roots.TryGetValue(mount,out var root)||root.Session is null)return false;
        var entry=_repository.EntryById(mount,entryId);if(entry is null||entry.Version!=snapshot.OperationVersion)return false;
        task??=FileTask(root,root.Session,entry,"freeSpace",id);
        PublishFileTask(task,"Deferred",reason:"等待重新核验释放条件。");root.Pins?.Record(entry.LocalPath);
        _=OwnAsync(async cancel=>{await CompletePendingUnpinsAsync(root,cancel,[entry]).ConfigureAwait(false);},CancellationToken.None);return true;
    }
    private static bool ReleaseTransient(Exception error)=>error.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) or unchecked((int)0x800700aa) or unchecked((int)0x80070322) or unchecked((int)0x8007012b)||error is System.ComponentModel.Win32Exception {NativeErrorCode:32 or 33 or 170};
}
