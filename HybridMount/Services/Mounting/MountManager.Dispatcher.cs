namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private FileTaskRecord Workflow(Runtime root,string id,string path,string operation,long generation,string state="Ready")
        =>new(id,root.Definition.Id,path,operation,generation,state,root.Session?.SessionId??Guid.Empty.ToString("N"),DateTimeOffset.UtcNow);
    private void RegisterNativeTasks(Runtime root,CloudMountSession session)
    {
        _fileDispatcher.Register(root.Definition.Id,session.SessionId,NativeRuntimeClient.Get(),(intent,token)=>RouteNativeTaskAsync(root,session,intent,token),accepted:intent=>
        {
            if(intent.EventKind=="renamed"&&root.Uploads is {} uploads)
                uploads.Scans.RecordNativeRename(new(intent.OldPath!,intent.Path,IntoExcluded:!FileExclusionRules.Matches(root.Definition.ExclusionRules,intent.OldPath!)&&FileExclusionRules.Matches(root.Definition.ExclusionRules,intent.Path)));
        },routeBatch:(intents,token)=>RouteNativeTaskBatchAsync(root,session,intents,token));
    }
    /// <summary>持久接纳之后在本挂载路由线程核对路径与索引；初次连接扫描完成前只保留意图。</summary>
    private async Task<IReadOnlyCollection<string>?> RouteNativeTaskAsync(Runtime root,CloudMountSession session,NativeTaskIntent intent,CancellationToken token)
    {
        var result=await RouteNativeTaskBatchAsync(root,session,[intent],token).ConfigureAwait(false);
        return result?.GetValueOrDefault(intent.TaskId);
    }
    /// <summary>一批意图共用扫描、上传和路径索引；初始占位发布风暴不会变成逐文件全表扫描。</summary>
    private async Task<IReadOnlyDictionary<string,IReadOnlyCollection<string>>?> RouteNativeTaskBatchAsync(Runtime root,CloudMountSession session,IReadOnlyList<NativeTaskIntent> intents,CancellationToken token)
    {
        if(IsStopping||root.RemovalRequested||!ReferenceEquals(root.Session,session)||root.Uploads is not {} uploads)return null;
        var pins=root.Pins;var full=intents.Any(item=>item.EventKind=="rescan"||item.RescanRequired);
        var scopes=new List<LocalUploadScanScope>();var renames=new List<LocalRenameNotice>();
        foreach(var intent in intents)
        {
            if(intent.EventKind=="rescan")continue;
            RecordNativeUploadChange(root.Definition.LocalPath,intent,
                change=>uploads.Scans.Record(root.Definition.LocalPath,change,DateTimeOffset.UtcNow,root.Definition.ExclusionRules??[]),uploads.RequestScan,uploads.Scans.RequestFull);
            scopes.Add(new(intent.Path,true));scopes.Add(new(RelativePaths.Parent(intent.Path),false));pins?.Record(intent.Path);
            if(intent.OldPath is {} old)
            {
                scopes.Add(new(old,true));pins?.Record(old);
                renames.Add(new(old,intent.Path,IntoExcluded:!FileExclusionRules.Matches(root.Definition.ExclusionRules,old)&&FileExclusionRules.Matches(root.Definition.ExclusionRules,intent.Path)));
            }
        }
        if(full){uploads.Scans.RequestFull();pins?.RequestFull();}
        await ScanLocalUploadsAsync(root,token,batch:new(full,scopes,renames)).ConfigureAwait(false);
        var byPath=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var allTasks=new HashSet<string>(StringComparer.Ordinal);
        void Index(string path,string task)
        {
            allTasks.Add(task);
            foreach(var ancestor in TaskStateSnapshot.Ancestors(path))
            {
                if(!byPath.TryGetValue(ancestor,out var tasks))byPath[ancestor]=tasks=new(StringComparer.Ordinal);tasks.Add(task);
            }
        }
        foreach(var item in _repository.UnfinishedUploads(root.Definition.Id))Index(item.LocalPath,item.Id);
        foreach(var item in _fileOperations.Values.Where(item=>item.Root.Definition.Id==root.Definition.Id&&!MountRepository.TaskStateTerminal(item.Snapshot.State)))Index(item.Entry.LocalPath,item.Snapshot.Id);
        foreach(var item in RenameIntents(root.Definition.Id).Where(item=>!item.Finished)){Index(item.LocalPath,item.Id);Index(item.SourcePath,item.Id);}
        // 只有精确命中已有索引的属性变化才核对固定状态；溢出补扫由既有64项维护消费者继续。
        var paths=intents.Where(item=>item.Path.Length>0).Select(item=>item.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entries=new Dictionary<string,MountEntry>(StringComparer.Ordinal);
        foreach(var chunk in paths.Chunk(128))foreach(var entry in _repository.EntriesAtPaths(root.Definition.Id,chunk))
        {
            if(entry.IsDirectory)continue;
            entries[entry.Id]=entry;
        }
        if(pins is not null)foreach(var entry in entries.Values.Where(item=>item.Materialized&&!item.Deleted))
        {
            token.ThrowIfCancellationRequested();var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);if(LocalMissing(path))continue;
            var info=CloudFilesNative.Inspect(path);var identity=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            if(identity?.MountId!=root.Definition.Id||identity.EntryId!=entry.Id)continue;
            var operation=info!.Value.Info.PinState switch{1 when session.ReadPinIntent(entry).NeedsDownload=>"keepOnDevice",2 when info.Value.Info.OnDiskSize>0=>"freeSpace",_=>null};
            if(operation is null)continue;var id=(operation=="freeSpace"?"release:":"pin:")+root.Definition.Id+":"+entry.Id;
            var task=FileTask(root,session,entry,operation,id);Index(entry.LocalPath,task.Snapshot.Id);pins.Record(entry.LocalPath);
        }
        return intents.ToDictionary(intent=>intent.TaskId,intent=>(IReadOnlyCollection<string>)(intent.EventKind=="rescan"||intent.RescanRequired?allTasks:
            (byPath.TryGetValue(intent.Path,out var tasks)?tasks:[]).Concat(intent.OldPath is {} old&&byPath.TryGetValue(old,out var previous)?previous:[]).ToHashSet(StringComparer.Ordinal)).ToArray(),StringComparer.Ordinal);
    }
    /// <summary>详情按需读取步骤及依赖，列表快照不增加逐行查询，公共IPC不包含此字段。</summary>
    internal string FileWorkflowDetails(string id)=>string.Join("\n",_repository.TaskSteps(id).Select(step=>global::HybridMount.Services.Localization.UiText.Text("TaskWorkflowStep",step.Ordinal+1,step.Name,step.State,step.DependsOn??"—",step.AttemptId??"—")));
    /// <summary>FETCH按独立回调身份执行，只有实际传输与原生结束成功才核验；不重放退出前的读取。</summary>
    private async Task DispatchNativeReadAsync(Runtime root,CloudMountSession session,string id,string entryId,Func<CancellationToken,Task> execute,CancellationToken token)
    {
        var entry=_repository.EntryById(root.Definition.Id,entryId)??throw new IOException("读取条目已不存在。");var completed=false;
        await _fileDispatcher.ExecuteAsync(Workflow(root,id,entry.LocalPath,"download",DateTimeOffset.UtcNow.UtcTicks),async cancel=>
        {await execute(cancel).ConfigureAwait(false);completed=true;},cancel=>Task.FromResult(new TaskVerification(completed?"Completed":"Interrupted","fetch-completion")),token).ConfigureAwait(false);
    }
    /// <summary>上传后独立只读核对实际对象；弱版本用已冻结正文摘要，不把线索升级为强验证器。</summary>
    private async Task<TaskVerification> VerifyUploadReceiptAsync(Runtime root,LocalUpload upload,SourceEntry? receipt,CancellationToken token)
    {
        if(receipt is null||root.Source is not {} source)return new("OutcomeUnknown","upload-receipt");
        var actual=await source.TryGetMetadataAsync(upload.SourcePath,token).ConfigureAwait(false);
        if(actual is null||actual.IsDirectory!=upload.IsDirectory||!upload.IsDirectory&&actual.Size!=upload.Total)return new("OutcomeUnknown","remote-object");
        if(upload.IsDirectory)return new("Completed","directory-verified");
        if(source is IRemoteChangeSource changes&&changes.HasReliableVersion(receipt)&&changes.HasReliableVersion(actual))
            return new(source.IsSameVersion(receipt.Version,actual.Version)?"Completed":"Conflict","remote-version");
        var digest=await HashRemoteAsync(source,actual,token).ConfigureAwait(false);
        return new(digest==upload.SnapshotSha256?"Completed":"Conflict","remote-content");
    }
    /// <summary>读取安全上传适配器已保存的回执及当前索引；未完成本地提交仍不能核验为完成。</summary>
    private Task<TaskVerification> VerifyUploadWorkflowAsync(Runtime root,string id,CancellationToken token)
    {
        var item=_repository.Upload(id);if(item is null)return Task.FromResult(new TaskVerification("OutcomeUnknown","upload-record"));
        if(item.State!="Completed")return Task.FromResult(new TaskVerification(item.State,"upload"));
        if(!item.IsCopy)
        {
            var entry=_repository.EntryBySource(root.Definition.Id,item.SourcePath);
            if(entry is null||entry.Deleted||entry.Protected||!entry.Materialized||item.Receipt is null||!root.Source!.IsSameVersion(entry.Version,item.Receipt.Version))
                return Task.FromResult(new TaskVerification("OutcomeUnknown","local-commit"));
        }
        return Task.FromResult(new TaskVerification("Completed","upload-verified"));
    }
    private Task<TaskVerification> VerifyPinnedWorkflowAsync(Runtime root,CloudMountSession session,MountEntry entry,CancellationToken token)
    {
        var current=_repository.EntryById(root.Definition.Id,entry.Id);
        var completed=ReferenceEquals(root.Session,session)&&current is not null&&session.SamePinVersion(entry,current)&&session.ReadPinIntent(current) is {Pinned:true,NeedsDownload:false};
        return Task.FromResult(new TaskVerification(completed?"Completed":"Deferred","pin-verification"));
    }
    private Task<TaskVerification> VerifyReleasedWorkflowAsync(Runtime root,CloudMountSession session,MountEntry entry,CancellationToken token)
    {
        if(!ReferenceEquals(root.Session,session))return Task.FromResult(new TaskVerification("Interrupted","release-session"));
        var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(root.Definition,path);
        var info=CloudFilesNative.InspectPersisted(path);var identity=MountPathGuard.ReadEntry(info.Identity);
        if(identity?.MountId!=root.Definition.Id||identity.EntryId!=entry.Id)return Task.FromResult(new TaskVerification("Failed","release-identity"));
        var state=info.Info.PinState!=2?"Superseded":info.Info.OnDiskSize==0&&!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length)?"Completed":"Deferred";
        return Task.FromResult(new TaskVerification(state,"release-verification"));
    }
}
