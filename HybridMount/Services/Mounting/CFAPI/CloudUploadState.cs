using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

/// <summary>上传后只转换和标记已核对的当前文件；不删除重建、不脱水、不改变固定状态。</summary>
internal static class CloudUploadState
{
    /// <summary>持久冲突提示同时供扫描与显式重试识别；不自动改变已有上传意图的类型。</summary>
    internal const string TypeConflictMessage=UploadRules.TypeConflictMessage;

    /// <summary>兼容既有内部入口；实际类型规则由领域模型统一提供，不访问原生状态。</summary>
    public static void ValidateTypes(LocalUpload intent,LocalFileStamp? local=null,bool? remoteDirectory=null)
        =>UploadRules.ValidateTypes(intent,local,remoteDirectory);

    internal static bool IsCommitted(MountDefinition mount,MountEntry entry,string path)
    {
        var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        return marker?.MountId==mount.Id&&marker.EntryId==entry.Id&&info?.Info.InSyncState==1;
    }
    internal static bool MatchesRecovery(LocalFileStamp stamp,LocalFileStamp? verifiedStamp,bool alreadyCommitted)
        =>alreadyCommitted&&verifiedStamp is not null&&stamp.Key==verifiedStamp.Key;

    /// <summary>保护引用内核验内容并返回提交后稳定戳；并发编辑返回空值，类型冲突拒绝原生转换。</summary>
    public static LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp? verifiedStamp=null)
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
                if(!MatchesRecovery(stamp,verifiedStamp,alreadyCommitted))return null;
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

/// <summary>默认 CFAPI 提交适配器；身份、保护句柄和同步标记仍由同一原生实现负责。</summary>
internal sealed class CloudUploadCommitter : ILocalUploadCommitter,IRecoveryUploadCommitter
{
    public LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared)=>CloudUploadState.Commit(mount,entry,prepared);
    public bool NeedsRecovery(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp stamp)
        =>!entry.IsDirectory&&stamp.Identity==prepared.SnapshotIdentity&&stamp.Key!=prepared.SnapshotStamp&&CloudUploadState.IsCommitted(mount,entry,RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath));
    public LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp? verifiedStamp)=>CloudUploadState.Commit(mount,entry,prepared,verifiedStamp);
}
