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
        public CloudMountSession? Session;
        public Action<string,LocalFileStamp>? LocalWriteCompleted;
        public Action<LocalRenameNotice>? LocalRenameCompleted;
        public CancellationTokenSource? Request;
        public string? CurrentId,PropertyPath;
        public int ConflictWaiters;
        public TaskCompletionSource IdleChanged=new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>状态释放后广播给等待取舍和调度器，不抢占上传消费用的Signal。</summary>
        public void PulseIdle(){TaskCompletionSource previous;lock(Sync){previous=IdleChanged;IdleChanged=new(TaskCreationOptions.RunContinuationsAsynchronously);}previous.TrySetResult();}
        public Task Work=Task.CompletedTask;
        public int Dirty=1;private int _stopping,_disposed;
        public readonly LocalUploadScanQueue Scans=new();
        public Task Cancellation=Task.CompletedTask,RequestCancellation=Task.CompletedTask;
        private readonly object _signalSync=new();
        public UploadRuntime(CancellationToken token)=>Stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        /// <summary>生产者在短锁内合并通知；消费者只递减计数，正常事件风暴不产生异常。</summary>
        public void Wake()=>Notify(false);
        /// <summary>监控或CFAPI变化要求扫描；短锁合并并唤醒，停止后的迟到通知忽略。</summary>
        public void RequestScan()=>Notify(true);
        /// <summary>无法定位路径的CFAPI变化立即请求全扫；停止后通知仍由原生命周期防护。</summary>
        public void RequestFullScan(){Scans.RequestFull();RequestScan();}
        /// <summary>消费者原子取走扫描请求；普通调度唤醒不会设置此标记。</summary>
        public bool TakeScanRequest()=>Interlocked.Exchange(ref Dirty,0)!=0;
        private void Notify(bool scan){lock(_signalSync){if(_stopping!=0||_disposed!=0)return;if(scan)Interlocked.Exchange(ref Dirty,1);if(Signal.CurrentCount==0)Signal.Release();}}
        public void CancelRequest(string? id=null){lock(Sync)if((id is null||CurrentId==id)&&Request is {IsCancellationRequested:false} request)RequestCancellation=request.CancelAsync();}
        public void RequestStop(){lock(_signalSync){if(_stopping!=0||_disposed!=0)return;_stopping=1;Cancellation=Stop.CancelAsync();}CancelRequest();}
        public void Dispose(){FileSystemWatcher? watcher;lock(_signalSync){if(Interlocked.Exchange(ref _disposed,1)!=0)return;watcher=Watcher;Watcher=null;}if(Session is not null){Session.LocalWriteCompleted-=LocalWriteCompleted;Session.LocalRenameCompleted-=LocalRenameCompleted;}watcher?.Dispose();Stop.Dispose();Signal.Dispose();}
    }
    private void StartUploads(Runtime root)
    {
        lock(root.ConnectionSync)
        {
        if(root.Uploads is not null||root.Session is null||root.Source is null||root.Definition.RemovalPending||root.RemovalRequested||_stop.IsCancellationRequested)return;
        var runtime=new UploadRuntime(_stop.Token);root.Uploads=runtime;
        runtime.Session=root.Session;runtime.LocalWriteCompleted=(path,stamp)=>runtime.Scans.Writes.Record(path,stamp,DateTimeOffset.UtcNow);runtime.Session.LocalWriteCompleted+=runtime.LocalWriteCompleted;
        runtime.LocalRenameCompleted=notice=>{runtime.Scans.RecordNativeRename(notice with{IntoExcluded=!FileExclusionRules.Matches(root.Definition.ExclusionRules,notice.OldPath)&&FileExclusionRules.Matches(root.Definition.ExclusionRules,notice.NewPath)});runtime.RequestScan();};runtime.Session.LocalRenameCompleted+=runtime.LocalRenameCompleted;
        var watcher=new FileSystemWatcher(root.Definition.LocalPath){IncludeSubdirectories=true,InternalBufferSize=65536,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.Size|NotifyFilters.LastWrite|NotifyFilters.CreationTime};
        BindUploadWatcher(watcher,runtime.RequestScan,change=>runtime.Scans.Record(root.Definition.LocalPath,change,DateTimeOffset.UtcNow,root.Definition.ExclusionRules??[]),runtime.Scans.RequestFull);
        runtime.Watcher=watcher;watcher.EnableRaisingEvents=true;runtime.Work=Task.Run(()=>UploadLoopAsync(root,runtime));
        }
    }
    /// <summary>集中适配所有文件系统变化及溢出；回调只请求扫描，不在监控线程读取正文。</summary>
    internal static void BindUploadWatcher(FileSystemWatcher watcher,Action requestScan,Action<FileSystemEventArgs>? queuePath=null,Action? requestFull=null)
    {
        void Changed(object sender,FileSystemEventArgs change){queuePath?.Invoke(change);requestScan();}
        watcher.Changed+=Changed;watcher.Created+=Changed;watcher.Deleted+=Changed;watcher.Renamed+=(_,change)=>Changed(watcher,change);watcher.Error+=(_,_)=>{requestFull?.Invoke();requestScan();};
    }
    private static void RequestUploadStop(Runtime root){lock(root.ConnectionSync)root.Uploads?.RequestStop();}
    private static async Task StopUploadsAsync(Runtime root)
    {
        UploadRuntime? runtime;lock(root.ConnectionSync){runtime=root.Uploads;if(runtime is null)return;runtime.RequestStop();}
        await Task.WhenAll(runtime.Cancellation,runtime.Work).ConfigureAwait(false);
        lock(root.ConnectionSync)if(ReferenceEquals(root.Uploads,runtime))root.Uploads=null;runtime.Dispose();
    }
    private void ObserveUploadSource(Runtime root)
    {
        if(root.Source is IMountSourceRuntime protocol)
        {
            var owner=root.Source;
            root.CapacityObserver=()=>{if(!IsStopping&&ReferenceEquals(root.Source,owner))InvalidateCapacity(root);};
            protocol.CapacityInvalidated+=root.CapacityObserver;
        }
        if(root.Source is not IUploadDiagnosticsSource source)return;
        root.BaselineObserver=value=>{try{_repository.SaveBaseline(root.Definition.Id,value);}catch(Exception e){_logger.QueueException("上传基线","Warning","操作未完成",e);}};
        root.WriteObserver=value=>_logger.Queue("协议写入","Warning",value);
        source.ContentBaseline+=root.BaselineObserver;source.WriteWarning+=root.WriteObserver;
    }
    private static void UnobserveUploadSource(Runtime root)
    {
        if(root.Source is IUploadDiagnosticsSource source){source.ContentBaseline-=root.BaselineObserver;source.WriteWarning-=root.WriteObserver;}
        if(root.Source is IMountSourceRuntime protocol)protocol.CapacityInvalidated-=root.CapacityObserver;
        root.BaselineObserver=null;root.WriteObserver=null;root.CapacityObserver=null;
    }
    /// <summary>上传记录与普通任务共享任务页；持久阶段优先于旧任务历史的中断转换。</summary>
    private static MountOperationSnapshot UploadSnapshot(LocalUpload item)=>item.ToSnapshot();
    private void PublishUpload(LocalUpload? item,Exception? error=null)
    {if(item is null)return;item=_repository.Upload(item.Id)??item;
        if(_roots.TryGetValue(item.MountId,out var root)&&FileExclusionRules.Matches(root.Definition,item)&&FileExclusionRules.Pause(item)!=item)
            item=_repository.UpdateUpload(item.Id,FileExclusionRules.Pause)??item;
        _repository.SaveOperation(UploadSnapshot(item));RecordUploadDiagnostic(item,error);Notify();}
    private LocalUpload SaveUpload(LocalUpload item){_repository.SaveUpload(item);PublishUpload(item);return item;}
    private LocalUpload? ChangeUpload(string id,Func<LocalUpload,LocalUpload> change,Exception? error=null){var item=_repository.UpdateUpload(id,change);PublishUpload(item,error);return item;}
    /// <summary>首次启用仅扫描本地元数据；不读取干净占位的正文，也不提交远端写入。</summary>
    public Task<IReadOnlyList<UploadCandidate>> ScanUploadChangesAsync(Guid id,CancellationToken token=default)=>OwnAsync(cancel=>ScanUploadChangesCoreAsync(id,cancel),token);
    private async Task<IReadOnlyList<UploadCandidate>> ScanUploadChangesCoreAsync(Guid id,CancellationToken token)
    {
        var root=Root(id);await ScanLocalUploadsAsync(root,token,true).ConfigureAwait(false);
        return _repository.Uploads(id).Where(x=>x.State=="AwaitingConfirmation").OrderBy(x=>x.LocalPath,StringComparer.Ordinal).Select(x=>new UploadCandidate(x.LocalPath,x.IsDirectory,x.ExpectedVersion is null?"本地新增或改名":"现有文件本地修改")).ToArray();
    }
    /// <summary>连接持有根锁时迁移旧授权；本版本已获用户授权纳入所有旧改动，但取消、冲突和未知结果保持保护。</summary>
    private void MigrateContinuousUploads(Runtime root)
    {
        if(root.Definition.ContinuousUploadMigrated)return;
        var entries=_repository.Entries(root.Definition.Id);
        foreach(var item in _repository.Uploads(root.Definition.Id).Where(item=>item.State is "AwaitingConfirmation" or "Ignored"&&!item.CancelRequested))
            ChangeUpload(item.Id,current=>current with{State=UploadRules.DeletedSourceAncestor(entries,current.SourcePath) is null?"Queued":"Conflict",Error=UploadRules.DeletedSourceAncestor(entries,current.SourcePath) is null?null:UploadRules.RemoteDeletedMessage,LastCheckedUtc=null});
        root.Definition=root.Definition with{AutoUploadEnabled=true,UploadInitialReviewCompleted=true,ContinuousUploadMigrated=true};
        _repository.SaveDefinition(root.Definition);
    }
    /// <summary>仅供同程序集的确定性测试暂停发送；不持久化，也不向生产界面或 IPC 暴露。</summary>
    internal async Task SetSchedulingForTestsAsync(Guid id,bool enabled,CancellationToken token=default)
    {
        var root=Root(id);await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try{root.SchedulingEnabled=enabled;}finally{root.Gate.Release();}
        root.Uploads?.Wake();
    }
    /// <summary>显式重试保持原始基线；结果不明的请求仍先执行只读核对。</summary>
    public bool RetryUpload(string id){lock(_lifetime){if(IsStopping)return false;return RetryUploadCore(id);}}
    private bool RetryUploadCore(string id)
    {
        if(IsStopping)return false;if(RetryExcludedRename(id) is {} renameRetry)return renameRetry;if(_repository.Upload(id)?.Resolution is not null)return false;if(RetryRemoteChange(id))return true;var item=_repository.Upload(id);if(item is null||!_roots.TryGetValue(item.MountId,out var root)||root.RemovalRequested||root.Definition.RemovalPending)return false;
        if(!UploadRules.CanRetry(item)||(RenameOwnsUpload(item)||FileExclusionRules.Matches(root.Definition,item))&&!UploadRules.InFlight(item))return false;
        if(item.State=="WaitingCapacity")Interlocked.Exchange(ref root.CapacityForce,1);
        ChangeUpload(id,x=>x with{State=UploadRules.RetryState(x),CancelRequested=false,ExplicitRequest=true,Error=null,LastCheckedUtc=null,NextAttemptUtc=null,Attention=x.Attention is {} a?a with{RetryRequested=true}:null});root.Uploads?.Wake();return true;
    }
    /// <summary>已有快照时另存冻结内容；无快照时按对话框提示使用准备时本地内容，原始冲突和索引不变。</summary>
    public string UploadAsCopy(string id,string destination){lock(_lifetime){ThrowIfStopping();return UploadAsCopyCore(id,destination);}}
    private string UploadAsCopyCore(string id,string destination)
    {
        ThrowIfStopping();var original=_repository.Upload(id)??throw new IOException("上传记录不存在。");var root=Root(original.MountId);
        if(RenameOwnsUpload(original))throw new IOException("原文件已改名进入排除规则，旧上传不能通过另存副本继续发送。 ");
        if(original.Resolution is not null||original.IsDirectory||root.RemovalRequested||root.Definition.RemovalPending)throw new IOException("当前条目不能另存副本。");
        destination=RelativePaths.Normalize(destination);if(destination.Length==0||destination.Equals(original.SourcePath,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("请填写不同的新文件相对路径。");
        if(FileExclusionRules.Matches(root.Definition,original)||FileExclusionRules.Matches(root.Definition.ExclusionRules,destination))throw new IOException("文件或副本目标匹配排除规则，请先修改规则或文件名。");
        var copy=_repository.CreateUploadCopy(id,destination,ValidateUploadSnapshot);
        PublishUpload(copy);root.Uploads?.Wake();return copy.Id;
    }
    private bool CancelUpload(string id)
    {
        var item=_repository.Upload(id);if(item is null||item.Resolution is not null||item.State is "Completed" or "Canceled" or "Ignored")return false;
        ChangeUpload(id,x=>x with{CancelRequested=true,State=x.State is "Sending" or "RemoteCommitted" or "OutcomeUnknown"?x.State:"Canceled",Error="已取消本代内容；再次修改或显式重试后恢复。"});
        if(_roots.TryGetValue(item.MountId,out var root))root.Uploads?.CancelRequest(id);return true;
    }
    /// <summary>只有明确不存在才视为删除；访问拒绝、占用或临时 oplock 竞争不能取消仍存在的上传。</summary>
    private static bool LocalMissing(string path)
    {
        try{return LocalFileMetadata.TryRead(path) is null;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return false;}
    }
    /// <summary>扫描不跟随外部链接；仅自己的云占位可递归，已同步占位不打开正文。</summary>
    private async Task ScanLocalUploadsAsync(Runtime root,CancellationToken token,bool requireComplete=false,LocalUploadScanBatch? batch=null)
    {
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try{ScanLocalUploadsCore(root,token,requireComplete,batch);}
        finally{root.Gate.Release();}
    }
    /// <summary>调用方持有根锁；连接或刷新前先记录本地意图，不能以刚枚举的远端版本替换原基线。</summary>
    private void ScanLocalUploadsCore(Runtime root,CancellationToken token,bool requireComplete=false,LocalUploadScanBatch? batch=null)
    {
            if(root.RemovalRequested||root.Definition.RemovalPending||root.Session is null)
            {if(requireComplete)throw new IOException("请先连接挂载，再完整扫描并确认已有改动。");return;}
            MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
            var queue=root.Uploads?.Scans;var scan=batch??queue?.StartFull()??LocalUploadScanBatch.Full;
            var legacyRenames=!RenameLegacyChecked(root);
            var incomplete=false;var entries=_repository.Entries(root.Definition.Id);var seen=new HashSet<string>(StringComparer.Ordinal);
            var index=new LocalUploadScanIndex(entries);
            var records=_repository.Uploads(root.Definition.Id).Where(x=>!x.IsCopy).GroupBy(x=>x.LocalPath,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.First(),StringComparer.Ordinal);
            // 先捕获本批所有改名，再扫描旧名称的新对象；枚举顺序不能让新文件继承旧云基线。
            foreach(var target in scan.Renames.Select(x=>x.NewPath).Concat(legacyRenames?entries.Where(x=>x.LocalPath!=x.SourcePath).Select(x=>x.LocalPath):[]).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var targetPath=RelativePaths.ToLocal(root.Definition.LocalPath,target);if(LocalFileMetadata.TryRead(targetPath) is null)continue;
                    MountPathGuard.ValidateOperation(root.Definition,targetPath);var info=CloudFilesNative.Inspect(targetPath);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                    var mapped=marker is null?index.ActivePath(target):index.ById(marker.EntryId);
                    ObserveExcludedRename(root,scan,target,LocalFileState.Read(targetPath),mapped,legacyRenames);
                }
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){incomplete=true;root.ExclusionError=error.Message;_logger.QueueException("排除扫描","Warning","排除改名证据核对未完成",error);}
            }
            void Scan(string directory)
            {
                foreach(var path in Directory.EnumerateFileSystemEntries(directory))Visit(path,true);
            }
            void Visit(string path,bool recursive,bool scopeRoot=false)
            {
                    token.ThrowIfCancellationRequested();try
                    {
                        if(scopeRoot)MountPathGuard.ValidateOperation(root.Definition,Path.GetDirectoryName(path)!);
                        var attributes=File.GetAttributes(path);var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                        if((attributes&FileAttributes.ReparsePoint)!=0&&(info is null||marker?.MountId!=root.Definition.Id))return;
                        MountPathGuard.ValidateOperation(root.Definition,path);
                        var relative=Path.GetRelativePath(root.Definition.LocalPath,path).Replace('\\','/');RelativePaths.Normalize(relative);seen.Add(relative);
                        records.TryGetValue(relative,out var prior);
                        if(prior?.Resolution is not null)return; // 显式取舍的持久屏障：中断时不把部分替换误当作新上传。
                        var entry=marker is null?index.ActivePath(relative):index.ById(marker.EntryId);
                        entry??=marker is null?index.ReplacementBaseline(relative,prior?.EntryId):null;
                        var stamp=LocalFileState.Read(path);
                        try{ObserveExcludedRename(root,scan,relative,stamp,entry,legacyRenames);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){incomplete=true;root.ExclusionError=error.Message;_logger.QueueException("排除扫描","Warning","排除改名证据核对未完成",error);}
                        if(IsNewObjectAtRenamedSource(root.Definition.Id,relative,stamp)&&prior?.ObservedStamp.Split(':')[0]!=stamp.Identity){prior=null;entry=null;}
                        var excluded=FileExclusionRules.Matches(root.Definition.ExclusionRules,relative,stamp.IsDirectory);
                        stamp=ReconcileFileExclusion(root,relative,stamp,excluded,token);
                        if(prior is not null)prior=_repository.Upload(prior.Id);
                        var clean=info is not null&&info.Value.Info.InSyncState==1&&info.Value.Info.ModifiedSize==0&&entry is {Deleted:false,Protected:false}&&entry.LocalPath==relative;
                        if(excluded)
                        {
                            if(prior is not null&&FileExclusionRules.Pause(prior)!=prior)ChangeUpload(prior.Id,FileExclusionRules.Pause);
                            if(!clean&&entry is not null&&!entry.Protected)_repository.SaveEntry(root.Definition.Id,entry with{Protected=true});
                            return;
                        }
                        if(prior?.State=="Excluded")prior=ChangeUpload(prior.Id,FileExclusionRules.Resume);
                        if(index.DeletedAncestor(relative) is not null)
                        {ObserveBlockedUpload(root,entry,prior,relative,stamp,clean);if(recursive&&stamp.IsDirectory)Scan(path);return;}
                        // 修复旧版把同名替换误建为新增的未发送意图；只补历史基线，不清除失败、取消或重排同代内容。
                        if(prior is {ExpectedVersion:null,EntryId:null,Receipt:null,BaselineSha256:null}&&!UploadRules.InFlight(prior)&&
                            prior.SourcePath==relative&&entry is {Deleted:true,Materialized:true}&&entry.SourcePath==relative&&prior.IsDirectory==entry.IsDirectory)
                            prior=ChangeUpload(prior.Id,x=>x with{EntryId=entry.Id,ExpectedVersion=entry.Version,BaselineSha256=_repository.Baseline(x.MountId,x.SourcePath,entry.Version)});
                        if(prior is null&&entry is not null&&entry.SourcePath==relative&&entry.IsDirectory!=stamp.IsDirectory)
                        {
                            SaveUpload(new(Guid.NewGuid().ToString("N"),root.Definition.Id,relative,relative,entry.IsDirectory,stamp.Key,1,"Conflict",DateTimeOffset.UtcNow,entry.Id,entry.Version,Error:UploadRules.TypeConflictMessage));
                            if(!entry.Protected)_repository.SaveEntry(root.Definition.Id,entry with{Protected=true});
                            if(recursive&&stamp.IsDirectory)Scan(path);return;
                        }
                        if(prior is not null&&(prior.IsDirectory!=stamp.IsDirectory||prior.State=="Conflict"&&prior.Error==UploadRules.TypeConflictMessage))
                        {
                            // 已发出的请求继续核对结果；类型冲突不能被普通扫描重新改成创建请求。
                            if(prior.ObservedStamp!=stamp.Key||prior.Error!=UploadRules.TypeConflictMessage||prior.State is not("Conflict" or "Sending" or "OutcomeUnknown"))
                                ChangeUpload(prior.Id,x=>x with{ObservedStamp=stamp.Key,Generation=x.Generation+(x.ObservedStamp==stamp.Key?0:1),
                                    State=x.State is "Sending" or "OutcomeUnknown"?x.State:"Conflict",Error=UploadRules.TypeConflictMessage,LastCheckedUtc=null});
                            if(entry is not null&&!entry.Protected)_repository.SaveEntry(root.Definition.Id,entry with{Protected=true});
                            if(recursive&&stamp.IsDirectory)Scan(path);return;
                        }
                        if(prior is {State:"Ignored"}&&prior.ObservedStamp!=stamp.Key&&!UploadRules.ContentChanged(prior.ObservedStamp,stamp))
                        {ChangeUpload(prior.Id,x=>x with{ObservedStamp=stamp.Key});}
                        else if(prior?.Attention?.ShellOriginalStamp==prior?.ObservedStamp&&prior?.Attention?.ShellOriginalStamp is not null&&
                            prior.ObservedStamp!=stamp.Key&&!UploadRules.ContentChanged(prior.ObservedStamp,stamp))
                        {
                            // 中断发生在属性写入与日志确认之间；只吸收已记录意图的元数据变化，正文变化仍建立新代次。
                            ChangeUpload(prior.Id,x=>x.Generation!=prior.Generation||x.ObservedStamp!=prior.ObservedStamp?x:x with
                            {ObservedStamp=stamp.Key,SnapshotStamp=x.SnapshotStamp==x.ObservedStamp&&x.SnapshotGeneration==x.Generation?stamp.Key:x.SnapshotStamp});
                        }
                        else if(!clean&&(prior is null||prior.ObservedStamp!=stamp.Key||prior.State=="Completed"))
                        {
                            var sourcePath=relative;var samePath=entry is not null&&entry.LocalPath.Equals(relative,StringComparison.Ordinal)&&entry.SourcePath.Equals(relative,StringComparison.Ordinal);
                            var baseline=samePath?entry!.Version:null;
                            var state="Queued";
                            if(prior is null)SaveUpload(new(Guid.NewGuid().ToString("N"),root.Definition.Id,relative,sourcePath,stamp.IsDirectory,stamp.Key,1,state,DateTimeOffset.UtcNow,entry?.Id,baseline,baseline is null?null:_repository.Baseline(root.Definition.Id,sourcePath,baseline)));
                            else ChangeUpload(prior.Id,x=>x with{ObservedStamp=stamp.Key,Generation=x.Generation+1,State=x.State is "Sending" or "RemoteCommitted" or "OutcomeUnknown"?x.State:state,ChangedUtc=DateTimeOffset.UtcNow,CancelRequested=false,Error=null});
                            if(entry is not null&&!entry.Protected)_repository.SaveEntry(root.Definition.Id,entry with{Protected=true});
                        }
                        if(recursive&&stamp.IsDirectory)Scan(path);
                    }
                    catch(Exception e)when(e is FileNotFoundException or DirectoryNotFoundException){}
                    catch(Exception e)when(e is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException){incomplete=true;_logger.QueueException("上传扫描","Warning","操作未完成",e);}
            }
            if(scan.IsFull)Scan(root.Definition.LocalPath);
            else foreach(var scope in scan.Scopes)
            {
                if(scope.Path.Length!=0)Visit(RelativePaths.ToLocal(root.Definition.LocalPath,scope.Path),scope.Recursive,true);
            }
            if(scan.IsFull&&!incomplete&&root.ExclusionMarkers is {} markers)
                try{if(markers.HasMarks)markers.Prune(root.Definition);}catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){root.ExclusionError="排除标记日志收尾未完成："+error.Message;_logger.QueueException("排除标记","Warning","恢复记录收尾未完成",error);}
            if(scan.IsFull&&!incomplete)try{RenameJournal(root.Definition.Id).MarkLegacyChecked();}catch(IOException){RenameIntents(root.Definition.Id);}
            queue?.Completed(scan,DateTimeOffset.UtcNow,!incomplete);
            if(requireComplete&&incomplete)throw new IOException("部分本地条目无法读取元数据，首次确认尚未完成；请解除占用或权限问题后重新扫描。");
            foreach(var item in scan.MissingCandidates(_repository.Uploads(root.Definition.Id),seen))
            {
                // 改名收尾已接管原文件；已发送回执仍由原上传恢复器提交，不按普通本地删除取消。
                if(RenameOwnsUpload(item)||!LocalMissing(RelativePaths.ToLocal(root.Definition.LocalPath,item.LocalPath)))continue;
                ChangeUpload(item.Id,x=>x with{CancelRequested=true,State=x.State is "Sending" or "RemoteCommitted" or "OutcomeUnknown"?x.State:"Canceled",Error="本地项已删除；未传播远端删除。"});root.Uploads?.CancelRequest(item.Id);
            }
    }
}
