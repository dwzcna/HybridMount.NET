using System.Diagnostics;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>群晖列表只提供线索：零正文的干净占位只更新元数据，已有范围则在根锁外取得摘要。</summary>
    private async Task<RemotePendingChange> ResolveListedMetadataAsync(Runtime root,RemoteSyncRuntime runtime,RemotePendingChange pending,CancellationToken token)
    {
        if(runtime.Source is not Models.Mounting.Synology.SynologyFileSource source||pending.Entry is not {IsDirectory:false} metadata||source.HasReliableVersion(metadata))return pending;
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);
            var entry=_repository.EntryBySource(root.Definition.Id,pending.Path);if(entry is null||!entry.Materialized)return pending;
            if(!entry.Protected&&!entry.Deleted&&!runtime.Session.IsActive(entry.Id))
            {
                var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(root.Definition,path);var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                if(marker?.MountId==root.Definition.Id&&marker.EntryId==entry.Id&&info!.Value.Info.OnDiskSize==0&&info.Value.Info.ValidatedSize==0&&info.Value.Info.ModifiedSize==0&&info.Value.Info.InSyncState==1)
                {
                    var stamp=LocalFileState.Read(path);
                    if(source.IsSameVersionHint(entry.Version,metadata.Version)&&stamp.Length==metadata.Size&&stamp.LastWriteTime==metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc())
                    {
                        if(entry.Version!=metadata.Version)_previews.Invalidate(root.Definition,entry.Id);
                        _repository.SaveEntry(root.Definition.Id,entry with{Version=metadata.Version});return SaveRemote(pending with{State="Completed",MetadataOnly=true,Error=null});
                    }
                    // 冷占位线索变化走原生元数据刷新，首次读取仍必须取得已验证摘要。
                    return pending;
                }
            }
        }
        finally{root.Gate.Release();}
        var verified=await source.GetMetadataAsync(pending.Path,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);var current=RequireRemotePending(pending.Id);
            if(current.State is "Canceled" or "Superseded" or "Completed")return current;
            return SaveRemote(current with{Entry=verified,Error=null});
        }
        finally{root.Gate.Release();}
    }
    /// <summary>仅恢复创建回执未落盘的同版本自有占位；外部文件、脏内容及身份变化保持保护。</summary>
    private MountEntry? RecoverPreparedPlaceholder(Runtime root,MountEntry? entry,SourceEntry metadata)
    {
        if(entry is null||entry.Materialized||entry.Protected||entry.Deleted||entry.RemotePresence==RemotePresence.Deleted||entry.IsDirectory!=metadata.IsDirectory||!root.Source!.IsSameVersion(entry.Version,metadata.Version))return entry;
        var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);if(LocalMissing(path))return entry;
        MountPathGuard.ValidateOperation(root.Definition,path);var info=CloudFilesNative.Inspect(path);var identity=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        if(identity?.MountId!=root.Definition.Id||identity.EntryId!=entry.Id||info!.Value.Info.ModifiedSize!=0||info.Value.Info.InSyncState!=1)return entry;
        var stamp=LocalFileState.Read(path);if(stamp.IsDirectory!=metadata.IsDirectory||!metadata.IsDirectory&&(stamp.Length!=metadata.Size||stamp.LastWriteTime!=metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc()))return entry;
        var recovered=entry with{Materialized=true};_repository.SaveEntry(root.Definition.Id,recovered);return recovered;
    }
    /// <summary>发现／创建／发布进度最多每250ms发布；不重新打开任何页面消息。</summary>
    private void PublishDiscoveryProgress(Runtime root,RemoteSyncRuntime runtime,bool force=false)
    {
        if(!RemoteSessionCurrent(root,runtime))return;var now=Stopwatch.GetTimestamp();if(!force&&runtime.LastPublicationProgress!=0&&Stopwatch.GetElapsedTime(runtime.LastPublicationProgress,now)<TimeSpan.FromMilliseconds(250))return;
        if(runtime.ProgressTaskId is {} taskId)_taskProgress[taskId]=global::HybridMount.Services.Localization.UiText.Text("TaskRemoteScanProgress",runtime.Discovered);
        runtime.LastPublicationProgress=now;root.Detection=root.Detection with{Mode=$"{runtime.Engine.Mode}；发现 {runtime.Discovered}／创建 {runtime.Created}／发布 {runtime.Published}"};Notify();
    }
    /// <summary>只快速发布新建的冷占位，已有内容仍走原保护路径；同一层最多256项，父目录先发布。</summary>
    private async Task<IReadOnlyList<MountEntry>> PublishDiscoveryBatchAsync(Runtime root,RemoteSyncRuntime runtime,IReadOnlyList<RemotePendingChange> changes,CancellationToken token)
    {
        var published=new List<MountEntry>();if(changes.Count==0)return published;if(changes.Count>256)throw new ArgumentOutOfRangeException(nameof(changes));
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
            var candidates=_repository.RemotePendingByIds(changes.Select(item=>item.Id).ToArray()).Where(item=>item.MountId==root.Definition.Id&&item.Scope==runtime.Engine.Scope&&item.Entry is not null&&item.State=="Pending").OrderBy(item=>item.Path.Count(c=>c=='/')).ToArray();
            var paths=candidates.SelectMany(item=>new[]{item.Path,RelativePaths.Parent(item.Path)}).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var index=_repository.EntriesBySources(root.Definition.Id,paths).ToDictionary(item=>item.SourcePath,StringComparer.OrdinalIgnoreCase);
            var uploads=_repository.Uploads(root.Definition.Id).Where(UploadRules.Unfinished).ToArray();var renames=RenameIntents(root.Definition.Id);
            foreach(var level in candidates.GroupBy(item=>item.Path.Count(c=>c=='/')))
            {
                token.ThrowIfCancellationRequested();if(!RemoteSessionCurrent(root,runtime))throw new OperationCanceledException(token);
                var prepared=new List<(MountEntry Entry,SourceEntry Metadata)>();var pendingByEntry=new Dictionary<string,RemotePendingChange>(StringComparer.Ordinal);
                foreach(var item in level)
                {
                    var metadata=item.Entry!;if(uploads.Any(upload=>UploadRules.SourceContains(item.Path,upload.SourcePath)||UploadRules.SourceContains(upload.SourcePath,item.Path))||renames.Any(rename=>UploadRules.SourceContains(rename.SourcePath,item.Path)||rename.DestinationPath is {} destination&&UploadRules.SourceContains(destination,item.Path)))continue;
                    index.TryGetValue(item.Path,out var entry);var parentPath=RelativePaths.Parent(item.Path);index.TryGetValue(parentPath,out var parent);
                    if(entry is {Materialized:true} or {Deleted:true} or {Protected:true}||parent is {Protected:true} or {Deleted:true}||parentPath.Length>0&&(parent is null||!parent.Materialized))continue;
                    if(entry is not null&&entry.IsDirectory!=metadata.IsDirectory)continue;
                    var local=parent is null?metadata.Path:parent.LocalPath+"/"+Path.GetFileName(metadata.Path);var path=RelativePaths.ToLocal(root.Definition.LocalPath,local);
                    if(!LocalMissing(path))continue;
                    entry??=new(Guid.NewGuid().ToString("N"),metadata.Path,local,metadata.Version,metadata.IsDirectory);entry=entry with{Version=metadata.Version};prepared.Add((entry,metadata));pendingByEntry[entry.Id]=item;
                }
                if(prepared.Count==0)continue;
                // 先持久准备身份；CFAPI在事务外执行。崩溃后按已保存ID重新核验，不用新ID覆盖现有占位。
                _repository.CommitDiscoveryBatch(root.Definition.Id,prepared.Select(item=>item.Entry).ToArray(),[],[]);
                var receipts=root.Session!.CreatePlaceholders(prepared);var completed=new List<RemotePendingChange>();var materialized=new List<MountEntry>();
                foreach(var receipt in receipts)
                {
                    var prior=pendingByEntry[receipt.Entry.Id];
                    if(receipt.HResult>=0){var entry=receipt.Entry with{Materialized=true,RemotePresence=RemotePresence.Present};materialized.Add(entry);published.Add(entry);index[entry.SourcePath]=entry;completed.Add(prior with{State="Completed",Error=null});runtime.Created++;}
                    else completed.Add(prior with{State="Waiting",Error=$"占位创建失败，HRESULT=0x{receipt.HResult:X8}；保留准备身份等待重试。"});
                }
                _repository.CommitDiscoveryBatch(root.Definition.Id,materialized,completed,completed.Select(item=>RemoteTask(item)).ToArray());runtime.Published+=materialized.Count;PublishDiscoveryProgress(root,runtime);
            }
            return published;
        }
        finally{root.Gate.Release();}
    }
}
