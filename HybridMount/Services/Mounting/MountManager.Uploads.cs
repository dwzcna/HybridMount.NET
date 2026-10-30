using System.Collections.Concurrent;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly SemaphoreSlim _uploadSlots=new(2,2);
    /// <summary>每根一个调度器；监控回调只唤醒，扫描和网络操作在后台进行。</summary>
    private sealed class UploadRuntime : IDisposable
    {
        public readonly CancellationTokenSource Stop;
        public readonly SemaphoreSlim Signal=new(0,1);
        public readonly object Sync=new();
        public FileSystemWatcher? Watcher;
        public CancellationTokenSource? Request;
        public string? CurrentId;
        public Task Work=Task.CompletedTask;
        public int Dirty=1;private int _stopping,_disposed;
        private readonly object _signalSync=new();
        public UploadRuntime(CancellationToken token)=>Stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        /// <summary>生产者在短锁内合并通知；消费者只递减计数，正常事件风暴不产生异常。</summary>
        public void Wake(){lock(_signalSync){if(_stopping!=0||_disposed!=0)return;Interlocked.Exchange(ref Dirty,1);if(Signal.CurrentCount==0)Signal.Release();}}
        public void CancelRequest(string? id=null){lock(Sync)if(id is null||CurrentId==id)try{Request?.Cancel();}catch(ObjectDisposedException){}}
        public void RequestStop(){if(Interlocked.Exchange(ref _stopping,1)!=0)return;Watcher?.Dispose();Stop.Cancel();CancelRequest();Wake();}
        public void Dispose(){lock(_signalSync){if(Interlocked.Exchange(ref _disposed,1)!=0)return;Watcher?.Dispose();Stop.Dispose();Signal.Dispose();}}
    }
    private void StartUploads(Runtime root)
    {
        lock(root.ConnectionSync)
        {
        if(root.Uploads is not null||root.Session is null||root.Source is null||root.Definition.RemovalPending||root.RemovalRequested||_stop.IsCancellationRequested)return;
        var runtime=new UploadRuntime(_stop.Token);root.Uploads=runtime;
        var watcher=new FileSystemWatcher(root.Definition.LocalPath){IncludeSubdirectories=true,InternalBufferSize=65536,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.Size|NotifyFilters.LastWrite|NotifyFilters.CreationTime};
        watcher.Changed+=(_,_)=>runtime.Wake();watcher.Created+=(_,_)=>runtime.Wake();watcher.Deleted+=(_,_)=>runtime.Wake();watcher.Renamed+=(_,_)=>runtime.Wake();watcher.Error+=(_,_)=>runtime.Wake();
        runtime.Watcher=watcher;watcher.EnableRaisingEvents=true;runtime.Work=Task.Run(()=>UploadLoopAsync(root,runtime));
        }
    }
    private static void RequestUploadStop(Runtime root){lock(root.ConnectionSync)root.Uploads?.RequestStop();}
    private static async Task StopUploadsAsync(Runtime root)
    {
        UploadRuntime? runtime;lock(root.ConnectionSync){runtime=root.Uploads;if(runtime is null)return;runtime.RequestStop();}
        await runtime.Work.ConfigureAwait(false);
        lock(root.ConnectionSync)if(ReferenceEquals(root.Uploads,runtime))root.Uploads=null;runtime.Dispose();
    }
    private void ObserveUploadSource(Runtime root)
    {
        if(root.Source is not IUploadDiagnosticsSource source)return;
        root.BaselineObserver=value=>{try{_repository.SaveBaseline(root.Definition.Id,value);}catch(Exception e){_logger.Queue("上传基线","Warning",e.Message);}};
        root.WriteObserver=value=>_logger.Queue("协议写入","Warning",value);
        source.ContentBaseline+=root.BaselineObserver;source.WriteWarning+=root.WriteObserver;
    }
    private static void UnobserveUploadSource(Runtime root)
    {
        if(root.Source is IUploadDiagnosticsSource source){source.ContentBaseline-=root.BaselineObserver;source.WriteWarning-=root.WriteObserver;}
        root.BaselineObserver=null;root.WriteObserver=null;
    }
    /// <summary>上传记录与普通任务共享任务页；持久阶段优先于旧任务历史的中断转换。</summary>
    private static MountOperationSnapshot UploadSnapshot(LocalUpload item)=>item.ToSnapshot();
    private void PublishUpload(LocalUpload? item)
    {if(item is null)return;_repository.SaveOperation(UploadSnapshot(item));Notify();}
    private LocalUpload SaveUpload(LocalUpload item){_repository.SaveUpload(item);PublishUpload(item);return item;}
    private LocalUpload? ChangeUpload(string id,Func<LocalUpload,LocalUpload> change){var item=_repository.UpdateUpload(id,change);PublishUpload(item);return item;}
    /// <summary>首次启用仅扫描本地元数据；不读取干净占位的正文，也不提交远端写入。</summary>
    public async Task<IReadOnlyList<UploadCandidate>> ScanUploadChangesAsync(Guid id,CancellationToken token=default)
    {
        var root=Root(id);await ScanLocalUploadsAsync(root,token,true).ConfigureAwait(false);
        return _repository.Uploads(id).Where(x=>x.State=="AwaitingConfirmation").OrderBy(x=>x.LocalPath,StringComparer.Ordinal).Select(x=>new UploadCandidate(x.LocalPath,x.IsDirectory,x.ExpectedVersion is null?"本地新增或改名":"现有文件本地修改")).ToArray();
    }
    /// <summary>首次开关启用必须明确选择旧改动；未选择的相同代次保持本地保护。</summary>
    public async Task SetAutoUploadAsync(Guid id,bool enabled,IReadOnlyCollection<string>? selectedExistingPaths=null,CancellationToken token=default)
    {
        var root=Root(id);await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("移除过程中不能更改自动上传。");
            if(enabled&&!root.Definition.UploadInitialReviewCompleted)
            {
                if(selectedExistingPaths is null)throw new IOException("请先扫描并确认要纳入上传的现有更改。");
                ScanLocalUploadsCore(root,token,true);
                foreach(var item in _repository.Uploads(id).Where(x=>x.State=="AwaitingConfirmation"))
                {
                    var selected=selectedExistingPaths.Any(path=>RelativePaths.Contains(path,item.LocalPath));
                    SaveUpload(item with{State=selected?"Queued":"Ignored",ExplicitRequest=selected,ChangedUtc=DateTimeOffset.UtcNow});
                }
            }
            root.Definition=root.Definition with{AutoUploadEnabled=enabled,UploadInitialReviewCompleted=root.Definition.UploadInitialReviewCompleted||enabled};_repository.SaveDefinition(root.Definition);
        }
        finally{root.Gate.Release();}
        root.Uploads?.Wake();Notify();
    }
    /// <summary>显式重试保持原始基线；结果不明的请求仍先执行只读核对。</summary>
    public bool RetryUpload(string id)
    {
        var item=_repository.Upload(id);if(item is null||!_roots.TryGetValue(item.MountId,out var root)||root.RemovalRequested||root.Definition.RemovalPending)return false;
        if(item.State is not("Failed" or "Conflict" or "Canceled" or "OutcomeUnknown" or "Waiting" or "Ignored" or "AwaitingConfirmation"))return false;
        ChangeUpload(id,x=>x with{State=x.State=="OutcomeUnknown"?x.State:x.Receipt is not null?"RemoteCommitted":"Queued",CancelRequested=false,ExplicitRequest=true,Error=null,LastCheckedUtc=null});root.Uploads?.Wake();return true;
    }
    /// <summary>冲突文件上传到新的相对路径；创建保护保证不覆盖目标，原始冲突和索引不变。</summary>
    public string UploadAsCopy(string id,string destination)
    {
        var original=_repository.Upload(id)??throw new IOException("上传记录不存在。");var root=Root(original.MountId);
        if(original.IsDirectory||root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("当前条目不能另存副本。");
        destination=RelativePaths.Normalize(destination);if(destination.Length==0||destination.Equals(original.SourcePath,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请填写不同的新文件相对路径。");
        var copy=new LocalUpload(Guid.NewGuid().ToString("N"),original.MountId,original.LocalPath,destination,false,original.ObservedStamp,1,"Queued",DateTimeOffset.UtcNow,ExplicitRequest:true,IsCopy:true);
        SaveUpload(copy);root.Uploads?.Wake();return copy.Id;
    }
    private bool CancelUpload(string id)
    {
        var item=_repository.Upload(id);if(item is null||item.State is "Completed" or "Canceled" or "Ignored" or "Conflict" or "Failed")return false;
        ChangeUpload(id,x=>x with{CancelRequested=true,State=x.State is "Sending" or "RemoteCommitted" or "OutcomeUnknown"?x.State:"Canceled",Error="已取消本代内容；再次修改或显式重试后恢复。"});
        if(_roots.TryGetValue(item.MountId,out var root))root.Uploads?.CancelRequest(id);return true;
    }
    /// <summary>未纳入的旧改动不会因固定、属性或访问等元数据变化自动获得上传授权。</summary>
    private static bool ContentChanged(string observed,LocalFileStamp current)
    {
        var parts=observed.Split(':');if(parts.Length!=6||parts[0]!=current.Identity)return true;
        if(current.IsDirectory)return false;
        if(!long.TryParse(parts[2],out var length)||length!=current.Length||!long.TryParse(parts[3],out var time)||time!=current.LastWriteTime)return true;
        return long.TryParse(parts[1],out var usn)&&current.Usn!=usn&&(current.Reasons&0x77)!=0;
    }
    /// <summary>只有明确不存在才视为删除；访问拒绝、占用或临时 oplock 竞争不能取消仍存在的上传。</summary>
    private static bool LocalMissing(string path)
    {
        try{File.GetAttributes(path);return false;}catch(FileNotFoundException){return true;}catch(DirectoryNotFoundException){return true;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return false;}
    }
    /// <summary>扫描不跟随外部链接；仅自己的云占位可递归，已同步占位不打开正文。</summary>
    private async Task ScanLocalUploadsAsync(Runtime root,CancellationToken token,bool requireComplete=false)
    {
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try{ScanLocalUploadsCore(root,token,requireComplete);}
        finally{root.Gate.Release();}
    }
    /// <summary>调用方持有根锁；连接或刷新前先记录本地意图，不能以刚枚举的远端版本替换原基线。</summary>
    private void ScanLocalUploadsCore(Runtime root,CancellationToken token,bool requireComplete=false)
    {
            if(root.RemovalRequested||root.Definition.RemovalPending||root.Session is null)
            {if(requireComplete)throw new IOException("请先连接挂载，再完整扫描并确认已有改动。");return;}
            MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
            var incomplete=false;var entries=_repository.Entries(root.Definition.Id);var seen=new HashSet<string>(StringComparer.Ordinal);
            var records=_repository.Uploads(root.Definition.Id).Where(x=>!x.IsCopy).GroupBy(x=>x.LocalPath,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.First(),StringComparer.Ordinal);
            void Scan(string directory)
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();try
                    {
                        var attributes=File.GetAttributes(path);var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                        if((attributes&FileAttributes.ReparsePoint)!=0&&(info is null||marker?.MountId!=root.Definition.Id))continue;
                        MountPathGuard.ValidateOperation(root.Definition,path);
                        var relative=Path.GetRelativePath(root.Definition.LocalPath,path).Replace('\\','/');RelativePaths.Normalize(relative);seen.Add(relative);
                        var entry=marker is null?entries.FirstOrDefault(x=>x.LocalPath.Equals(relative,StringComparison.OrdinalIgnoreCase)&&!x.Deleted):entries.FirstOrDefault(x=>x.Id==marker.EntryId);
                        records.TryGetValue(relative,out var prior);
                        var stamp=LocalFileState.Read(path);var clean=info is not null&&info.Value.Info.InSyncState==1&&info.Value.Info.ModifiedSize==0&&entry is {Deleted:false,Protected:false}&&entry.LocalPath==relative;
                        if(DeletedSourceAncestor(entries,relative) is not null)
                        {ObserveBlockedUpload(root,entry,prior,relative,stamp,clean);if(stamp.IsDirectory)Scan(path);continue;}
                        if(prior is {State:"Ignored"}&&prior.ObservedStamp!=stamp.Key&&!ContentChanged(prior.ObservedStamp,stamp))
                        {ChangeUpload(prior.Id,x=>x with{ObservedStamp=stamp.Key});}
                        else if(!clean&&(prior is null||prior.ObservedStamp!=stamp.Key))
                        {
                            var sourcePath=relative;var samePath=entry is not null&&entry.LocalPath.Equals(relative,StringComparison.Ordinal)&&entry.SourcePath.Equals(relative,StringComparison.Ordinal);
                            var baseline=samePath?entry!.Version:null;
                            var state=root.Definition.UploadInitialReviewCompleted?"Queued":"AwaitingConfirmation";
                            if(prior is null)SaveUpload(new(Guid.NewGuid().ToString("N"),root.Definition.Id,relative,sourcePath,stamp.IsDirectory,stamp.Key,1,state,DateTimeOffset.UtcNow,entry?.Id,baseline,baseline is null?null:_repository.Baseline(root.Definition.Id,sourcePath,baseline)));
                            else ChangeUpload(prior.Id,x=>x with{ObservedStamp=stamp.Key,Generation=x.Generation+1,State=x.State is "Sending" or "RemoteCommitted" or "OutcomeUnknown"?x.State:state,ChangedUtc=DateTimeOffset.UtcNow,CancelRequested=false,Error=null});
                            if(entry is not null&&!entry.Protected)_repository.SaveEntry(root.Definition.Id,entry with{Protected=true});
                        }
                        if(stamp.IsDirectory)Scan(path);
                    }
                    catch(Exception e)when(e is FileNotFoundException or DirectoryNotFoundException){}
                    catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException){incomplete=true;_logger.Queue("上传扫描","Warning",e.Message);}
                }
            }
            Scan(root.Definition.LocalPath);
            if(requireComplete&&incomplete)throw new IOException("部分本地条目无法读取元数据，首次确认尚未完成；请解除占用或权限问题后重新扫描。");
            foreach(var item in _repository.Uploads(root.Definition.Id).Where(x=>!x.IsCopy&&!seen.Contains(x.LocalPath)&&x.State is not("Completed" or "Canceled" or "Ignored")))
            {if(!LocalMissing(RelativePaths.ToLocal(root.Definition.LocalPath,item.LocalPath)))continue;ChangeUpload(item.Id,x=>x with{CancelRequested=true,State=x.State is "Sending" or "RemoteCommitted" or "OutcomeUnknown"?x.State:"Canceled",Error="本地项已删除；未传播远端删除。"});root.Uploads?.CancelRequest(item.Id);}
    }
}
