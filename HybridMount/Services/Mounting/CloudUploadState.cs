using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

/// <summary>上传后只转换和标记已核对的当前文件；不删除重建、不脱水、不改变固定状态。</summary>
internal static class CloudUploadState
{
    /// <summary>排他 oplock 下比较身份和代次；并发编辑时返回 false，保留本地脏状态。</summary>
    public static bool Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared)
    {
        var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);
        using var handle=CloudFilesNative.Open(path,true);
        if(!CloudFilesNative.CfReferenceProtectedHandle(handle))throw new IOException("上传提交句柄无法引用，请稍后重试。");
        try
        {
            var raw=CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle);
            using var borrowed=new SafeFileHandle(raw,false);var stamp=LocalFileState.Read(borrowed);
            if(stamp.Identity!=prepared.SnapshotIdentity)return false;
            var info=CloudFilesNative.Inspect(path);
            var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            var alreadyCommitted=marker?.MountId==mount.Id&&marker.EntryId==entry.Id&&info?.Info.InSyncState==1;
            if(!entry.IsDirectory&&stamp.Key!=prepared.SnapshotStamp)
            {
                if(!alreadyCommitted)return false;
                using var stream=new ProtectedResourceStream(raw,stamp.Length);
                if(System.Convert.ToHexString(SHA256.HashData(stream))!=prepared.SnapshotSha256)return false;
            }
            var identity=JsonSerializer.SerializeToUtf8Bytes(new MountPathGuard.EntryMarker(mount.Id,entry.Id));
            long usn=stamp.Usn;
            if(info is null)CloudFilesNative.Check(Convert(handle,identity,(uint)identity.Length,0,out usn,0));
            else
            {
                if(marker?.MountId!=mount.Id)throw new IOException("占位身份不属于此挂载，拒绝提交。");
                if(marker.EntryId!=entry.Id)
                {
                    // 已在保护句柄内核对快照；身份更新自身会推进 USN，不用旧 USN 再限制此次元数据变更。
                    usn=0;var update=UpdateIdentity(handle,0,identity,(uint)identity.Length,0,0,2,ref usn,0);
                    if(update<0)throw new IOException($"上传占位身份提交失败：0x{update:X8}");
                }
            }
            usn=0;var sync=SetInSync(handle,1,0,ref usn);if(sync<0)throw new IOException($"上传同步状态提交失败：0x{sync:X8}");return true;
        }
        finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
    }
    [DllImport("CldApi.dll",EntryPoint="CfConvertToPlaceholder")] private static extern int Convert(CloudFilesNative.ProtectedHandle handle,byte[] identity,uint length,uint flags,out long usn,nint overlap);
    [DllImport("CldApi.dll",EntryPoint="CfUpdatePlaceholder")] private static extern int UpdateIdentity(CloudFilesNative.ProtectedHandle handle,nint metadata,byte[] identity,uint length,nint ranges,uint count,uint flags,ref long usn,nint overlap);
    [DllImport("CldApi.dll",EntryPoint="CfSetInSyncState")] private static extern int SetInSync(CloudFilesNative.ProtectedHandle handle,uint state,uint flags,ref long usn);
}
