using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

/// <summary>上传后只转换和标记已核对的当前文件；不删除重建、不脱水、不改变固定状态。</summary>
internal static class CloudUploadState
{
    /// <summary>持久冲突提示同时供扫描与显式重试识别；不自动改变已有上传意图的类型。</summary>
    internal const string TypeConflictMessage="本地对象、上传意图、快照或远端回执的文件/目录类型不一致；请恢复原类型后重试，或改名后上传。";

    /// <summary>校验当前对象、不可变快照与远端回执的类型；任何不一致均拒绝发送或提交。</summary>
    public static void ValidateTypes(LocalUpload intent,LocalFileStamp? local=null,bool? remoteDirectory=null)
    {
        if(local is not null&&local.IsDirectory!=intent.IsDirectory||remoteDirectory is not null&&remoteDirectory!=intent.IsDirectory)
            throw new SourceConflictException(TypeConflictMessage);
        if(intent.SnapshotStamp is not null)
        {
            var parts=intent.SnapshotStamp.Split(':');
            if(parts.Length!=6||!bool.TryParse(parts[5],out var directory)||directory!=intent.IsDirectory)
                throw new SourceConflictException(TypeConflictMessage);
        }
    }

    /// <summary>保护引用内核验内容并返回提交后稳定戳；并发编辑返回空值，类型冲突拒绝原生转换。</summary>
    public static LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared)
    {
        var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);
        using var handle=CloudFilesNative.Open(path,true);
        if(!CloudFilesNative.CfReferenceProtectedHandle(handle))throw new IOException("上传提交句柄无法引用，请稍后重试。");
        try
        {
            var raw=CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle);
            using var borrowed=new SafeFileHandle(raw,false);var stamp=LocalFileState.Read(borrowed);
            ValidateTypes(prepared,stamp,entry.IsDirectory);
            if(stamp.Identity!=prepared.SnapshotIdentity)return null;
            var info=CloudFilesNative.Inspect(path);
            var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            var alreadyCommitted=marker?.MountId==mount.Id&&marker.EntryId==entry.Id&&info?.Info.InSyncState==1;
            if(!entry.IsDirectory&&stamp.Key!=prepared.SnapshotStamp)
            {
                if(!alreadyCommitted)return null;
                using var stream=new ProtectedResourceStream(raw,stamp.Length);
                if(System.Convert.ToHexString(SHA256.HashData(stream))!=prepared.SnapshotSha256)return null;
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
            usn=0;var sync=SetInSync(handle,1,0,ref usn);if(sync<0)throw new IOException($"上传同步状态提交失败：0x{sync:X8}");
            // 转换和同步标记会推进 USN；必须在保护引用释放前从同一对象取戳。
            return LocalFileState.Read(borrowed);
        }
        finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
    }
    [DllImport("CldApi.dll",EntryPoint="CfConvertToPlaceholder")] private static extern int Convert(CloudFilesNative.ProtectedHandle handle,byte[] identity,uint length,uint flags,out long usn,nint overlap);
    [DllImport("CldApi.dll",EntryPoint="CfUpdatePlaceholder")] private static extern int UpdateIdentity(CloudFilesNative.ProtectedHandle handle,nint metadata,byte[] identity,uint length,nint ranges,uint count,uint flags,ref long usn,nint overlap);
    [DllImport("CldApi.dll",EntryPoint="CfSetInSyncState")] private static extern int SetInSync(CloudFilesNative.ProtectedHandle handle,uint state,uint flags,ref long usn);
}
