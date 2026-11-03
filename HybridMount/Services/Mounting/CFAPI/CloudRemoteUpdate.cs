using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;
namespace HybridMount.Services.Mounting;

/// <summary>固定文件的原生版本切换；同一占位身份、排他保护和持久日志覆盖整个提交，绝不普通覆盖文件。</summary>
internal static class CloudRemoteUpdate
{
    /// <summary>调用者持有根锁，且网络快照已完整校验。不可逆提交开始后安全收尾，不因用户取消中途释放保护。</summary>
    internal static async Task ApplyAsync(MountDefinition mount,MountEntry entry,RemotePendingChange pending,CloudMountSession session,
        Action<RemotePendingChange,MountEntry?> save)
    {
        if(pending.Entry is not {} metadata||metadata.IsDirectory||entry.IsDirectory||pending.SnapshotPath is null||pending.Sha256 is null)throw new IOException("远端更新日志不完整。");
        var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);
        // 在引用前打开快照，持有期间拒绝写入和删除；后续回调只读同一文件。
        await using var snapshot=new FileStream(pending.SnapshotPath,FileMode.Open,FileAccess.Read,FileShare.Read,131072,FileOptions.Asynchronous);
        if(snapshot.Length!=metadata.Size||Convert.ToHexString(await SHA256.HashDataAsync(snapshot).ConfigureAwait(false))!=pending.Sha256)throw new IOException("远端更新快照已损坏，保留原占位。");
        using var handle=CloudFilesNative.Open(path,true);
        if(!CloudFilesNative.CfReferenceProtectedHandle(handle))throw new IOException("无法引用远端更新保护句柄。");
        var installed=false;var restorePin=false;
        try
        {
            var raw=CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle);using var borrowed=new SafeFileHandle(raw,false);
            if(mount.RootIdentity!=MountPathGuard.FileIdentity(mount.LocalPath))throw new SourceConflictException("提交保护取得后挂载根身份变化，未应用快照。");
            var local=LocalFileState.Read(borrowed);var info=CloudFilesNative.Inspect(borrowed);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            if(marker?.MountId!=mount.Id||marker.EntryId!=entry.Id||local.IsDirectory||entry.Protected||entry.Deleted||info!.Value.Info.ModifiedSize!=0||info.Value.Info.InSyncState!=1)
                throw new SourceConflictException("占位身份、类型或本地内容已变化，保留本地内容和远端快照。");
            var recovery=pending.State=="Hydrating"&&local.Identity==pending.OriginalStamp?.Split(':')[0]&&local.Length==metadata.Size&&local.LastWriteTime==metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc()&&info.Value.Info.PinState==pending.PinState;
            if(local.Key!=pending.OriginalStamp&&!recovery)throw new SourceConflictException("下载或恢复期间本地状态已变化，需要重新核对，未覆盖本地文件。");
            session.InstallRemoteSnapshot(entry,pending);installed=true;
            if(pending.State!="Hydrating")
            {
                pending=pending with{State="Committing",EntryId=entry.Id,OldVersion=entry.Version,PinState=info.Value.Info.PinState};save(pending,null);
                restorePin=info.Value.Info.PinState==1;
                if(restorePin)CloudFilesNative.Check(CloudFilesNative.CfSetPinState(handle,2,0,0));
                UpdateMetadata(handle,borrowed,metadata);
                if(pending.PinState==1){CloudFilesNative.Check(CloudFilesNative.CfSetPinState(handle,1,0,0));restorePin=false;}
                entry=entry with{Version=metadata.Version};pending=pending with{State="Hydrating",OriginalStamp=LocalFileState.Read(borrowed).Key};save(pending,entry);
            }
            if(metadata.Size!=0)
            {
                using var hydrationLimit=new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await CloudFilesNative.HydrateAsync(handle,hydrationLimit.Token).ConfigureAwait(false);
            }
            using(var content=new ProtectedResourceStream(raw,metadata.Size))
                if(Convert.ToHexString(SHA256.HashData(content))!=pending.Sha256)throw new IOException("原生水合摘要不符，保留恢复日志。");
            var final=CloudFilesNative.Inspect(borrowed);
            if(final?.Info.ModifiedSize!=0||pending.PinState==1&&final?.Info.PinState!=1)throw new IOException("水合后修改状态或固定状态不符。");
            long completedUsn=0;CloudFilesNative.Check(SetInSync(handle,1,0,ref completedUsn));
            save(pending with{State="Completed",OriginalStamp=LocalFileState.Read(borrowed).Key,Error=null},entry);
        }
        finally
        {
            try
            {
                if(restorePin)CloudFilesNative.Check(CloudFilesNative.CfSetPinState(handle,1,0,0));
                if(installed)session.RemoveRemoteSnapshot(entry.Id);
            }
            finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
        }
        CloudFilesNative.NotifyItemUpdated(path);
    }
    /// <summary>干净占位的受保护元数据切换；零字节使用显式传递以实际截断，并保存其他文件系统字段。</summary>
    internal static void UpdateMetadata(CloudFilesNative.ProtectedHandle handle,SafeFileHandle borrowed,SourceEntry metadata)
    {
        var time=metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc();
        var fs=new CloudFilesNative.FsMetadata{Basic=new(){LastWriteTime=time},FileSize=metadata.Size};
        if(metadata.Size==0)
        {
            if(!GetBasic(borrowed,0,out var basic,40))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            basic.LastWriteTime=time;fs.Basic=basic;
        }
        long usn=0; // 入口已核对 USN，排他引用仍在；解除固定自身会推进元数据 USN。
        var update=Update(handle,fs,0,0,metadata.Size==0?[]:[new(){Offset=0,Length=-1}],metadata.Size==0?0u:1u,metadata.Size==0?259u:7u,ref usn,0);
        if(update<0)
        {
            var status=CloudFilesNative.Inspect(borrowed)!.Value.Info;
            throw new IOException($"固定文件原生更新失败：0x{update:X8}；InSync={status.InSyncState}，Modified={status.ModifiedSize}，Pin={status.PinState}。");
        }
    }
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)] private static extern bool GetBasic(SafeFileHandle handle,int infoClass,out CloudFilesNative.BasicInfo basic,uint size);
    [DllImport("CldApi.dll",EntryPoint="CfUpdatePlaceholder")] private static extern int Update(CloudFilesNative.ProtectedHandle handle,in CloudFilesNative.FsMetadata metadata,nint identity,uint length,[In] CloudFilesNative.FileRange[] ranges,uint count,uint flags,ref long usn,nint overlap);
    [DllImport("CldApi.dll",EntryPoint="CfSetInSyncState")] private static extern int SetInSync(CloudFilesNative.ProtectedHandle handle,uint state,uint flags,ref long usn);
}
