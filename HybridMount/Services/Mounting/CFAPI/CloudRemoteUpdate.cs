namespace HybridMount.Services.Mounting;

/// <summary>远端快照提交的领域检查点；完整原生更新和水合由C++在保护引用内完成。</summary>
internal static class CloudRemoteUpdate
{
    internal static async Task ApplyAsync(MountDefinition mount,MountEntry entry,RemotePendingChange pending,CloudMountSession session,Action<RemotePendingChange,MountEntry?> save)
    {
        if(pending.Entry is not {} metadata||metadata.IsDirectory||entry.IsDirectory||pending.SnapshotPath is null||pending.Sha256 is null||pending.OriginalStamp is null)throw new IOException("远端更新日志不完整。");
        var args=session.EntryArgs(entry,new{size=metadata.Size,time=metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc(),snapshotPath=pending.SnapshotPath,sha256=pending.Sha256,originalStamp=pending.OriginalStamp,recovering=pending.State=="Hydrating",pinState=pending.PinState});var installed=false;
        try
        {
            await session.Runtime.InvokeAsync("applyRemoteSnapshot",args,(stage,data)=>
            {
                switch(stage)
                {
                    case "installRemote":session.InstallRemoteSnapshot(entry,pending);installed=true;break;
                    case "remoteCommitting":pending=pending with{State="Committing",EntryId=entry.Id,OldVersion=entry.Version,PinState=data.GetProperty("pinState").GetUInt32()};save(pending,null);break;
                    case "remoteHydrating":entry=entry with{Version=metadata.Version};pending=pending with{State="Hydrating",OriginalStamp=NativeRuntimeArguments.Stamp(data).Key};save(pending,entry);break;
                    case "remoteCompleted":var stamp=NativeRuntimeArguments.Stamp(data);save(pending with{State="Completed",OriginalStamp=stamp.Key,Error=null},entry);session.RecordLocalWrite(entry,stamp);break;
                    default:throw new IOException("远端更新原生阶段无效。");
                }
                return Task.FromResult<object?>(null);
            }).ConfigureAwait(false);
        }
        catch(System.Runtime.InteropServices.COMException error)when(error.HResult is unchecked((int)0x800703ee) or unchecked((int)0x80070178)){throw new SourceConflictException("本地身份或内容已变化，保留本地内容与远端快照。");}
        finally{if(installed)session.RemoveRemoteSnapshot(entry.Id);}
        CloudFilesNative.NotifyItemUpdated(RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath));
    }
}
