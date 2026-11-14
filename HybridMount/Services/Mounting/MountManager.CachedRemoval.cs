namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>离线保留完整缓存和普通文件；不完整且无保护的干净占位才删除。根锁内运行，网络源已排空。</summary>
    private async Task<bool> PreserveCachedAndRemoveAsync(Runtime root,Job job)
    {
        if(RemovalCompletion().Read(root.Definition.Id) is {ContentCompleted:true})
        {
            var completed=PrepareRemoval(root,MountRemovalMode.PreserveCachedFiles);
            await CompleteRemovalAsync(root,completed,job.Cancel.Token).ConfigureAwait(false);return true;
        }
        await DisconnectCoreAsync(root).ConfigureAwait(false);
        Status(root,"未连接");
        var mount=root.Definition;
        // 已发出的修改、回执和改名必须由原流程核对；移除不能销毁这些恢复证据。
        var uploads=_repository.Uploads(mount.Id);
        if(uploads.Any(item=>UploadRules.InFlight(item)||item.Receipt is not null&&item.State!="Completed"||item.Resolution is not null||item.SnapshotPath is not null&&UploadRules.Unfinished(item))||
            RenameIntents(mount.Id).Any(item=>!item.Finished)||_renameErrors.ContainsKey(mount.Id)||
            RemotePendingFor(root).Any(item=>item.State is "Committing" or "Hydrating"||item.SnapshotPath is not null&&item.State is not("Completed" or "Superseded")))
            throw new IOException(global::HybridMount.Services.Localization.UiText.Text("RemoveCachedUnresolved"));
        // 不可逆内容处理开始前才固定移除意图；未决回执拒绝后仍可重新连接核对。
        var journal=PrepareRemoval(root,MountRemovalMode.PreserveCachedFiles);
        var uploadPaths=uploads.Where(UploadRules.Unfinished).Select(item=>item.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(journal.Root.Registered)
        {
            // 无源会话从接纳起拒绝FETCH，不会为移除下载未缓存正文。
            root.Session=new CloudMountSession(mount,null,_repository,_logger,blockHydration:true);
            ConnectSession(root);await root.Session.BeginClearAsync().ConfigureAwait(false);
        }
        Status(root,"移除中",global::HybridMount.Services.Localization.UiText.Text("RemoveCachedOperation"));
        using var rootLease=MountContentCleaner.HoldRoot(mount);
        var count=0;var failures=WalkCached(mount.LocalPath,0);
        if(failures!=0)
        {
            RemovalCompletion().Failure(journal,new IOException(job.Results.LastOrDefault(item=>item.Status=="Failed")?.Message??"缓存保留未完成。"));
            return false;
        }
        journal=RemovalCompletion().ContentComplete(journal);
        await CompleteRemovalAsync(root,journal,job.Cancel.Token).ConfigureAwait(false);return true;

        int WalkCached(string directory,int depth)
        {
            if(depth>256)throw new IOException("目录深度超过安全上限。");
            var failed=0;
            foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                job.Cancel.Token.ThrowIfCancellationRequested();
                if(++count>1000000)throw new IOException("目录条目超过安全上限。");
                job.Snapshot=job.Snapshot with{Total=count};
                try
                {
                    var tag=LocalFileMetadata.ReadTag(path);
                    // 外部联接和符号链接只保留本体，绝不进入其目标或交给CFAPI查询。
                    if((tag.Attributes&0x400)!=0&&tag.Tag!=0&&!CloudFilesNative.IsCloudTag(tag.Tag))
                    {Result(job,path,"Completed",global::HybridMount.Services.Localization.UiText.Text("RemoveCachedLinkKept"));continue;}
                    var snapshot=CloudFilesNative.ReadNative(path,ranges:true);
                    var childrenFailed=0;
                    if(snapshot.Stamp.IsDirectory)
                    {
                        using(var handle=MountContentCleaner.HoldDirectory(path,snapshot.Stamp.Identity))childrenFailed=WalkCached(path,depth+1);
                        failed+=childrenFailed;
                        if(childrenFailed!=0){Result(job,path,"Failed",global::HybridMount.Services.Localization.UiText.Text("RemoveCachedChildrenFailed"));continue;}
                        snapshot=CloudFilesNative.ReadNative(path,ranges:true,expectedIdentity:snapshot.Stamp.Identity);
                    }
                    if(snapshot.Placeholder is {} info)
                    {
                        var marker=MountPathGuard.ReadEntry(info.Identity);
                        var entry=marker is null?null:_repository.EntryById(mount.Id,marker.EntryId);
                        var relative=Path.GetRelativePath(mount.LocalPath,path).Replace('\\','/');
                        if(root.Session is null||marker?.MountId!=mount.Id||entry is null||!entry.LocalPath.Equals(relative,StringComparison.OrdinalIgnoreCase))
                            throw new IOException(global::HybridMount.Services.Localization.UiText.Text("RemoveCachedOwnership"));
                        var discard=!snapshot.Stamp.IsDirectory&&!snapshot.Full;
                        if(discard&&(info.ModifiedSize!=0||info.InSyncState!=1||entry.Protected||entry.Deleted||uploadPaths.Contains(relative)||_repository.HasPendingRemote(mount.Id,entry.SourcePath)))
                            throw new IOException(global::HybridMount.Services.Localization.UiText.Text("RemoveCachedPartialProtected"));
                        root.Session.Runtime.Invoke("preserveCachedLocal",root.Session.EntryArgs(entry,new{discard,proofStamp=snapshot.Stamp.Key}),token:job.Cancel.Token);
                        Result(job,path,"Completed",global::HybridMount.Services.Localization.UiText.Text(discard?"RemoveCachedPartialRemoved":"RemoveCachedKept"));
                    }
                    else Result(job,path,"Completed",global::HybridMount.Services.Localization.UiText.Text("RemoveCachedKept"));
                }
                catch(OperationCanceledException){throw;}
                catch(Exception error){failed++;Result(job,path,"Failed",error.Message);}
            }
            return failed;
        }
    }
}
