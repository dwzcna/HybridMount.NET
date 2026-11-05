using Microsoft.Win32.SafeHandles;
namespace HybridMount.Services.Mounting;

public sealed partial class CloudMountSession
{
    /// <summary>完成已有UNPINNED意图，不改变固定或同步标记。调用者持根锁并排除持久未决工作；返回实际释放字节，空值表示本轮不适合处理。</summary>
    internal long? CompletePendingUnpin(MountEntry entry)
    {
        if(entry.IsDirectory||entry.Deleted||entry.Protected||entry.RemotePresence==RemotePresence.Deleted||!BeginMetadataUpdate(entry.Id))return null;
        try
        {
            var path=RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(_mount,path);
            LocalFileStamp before;
            using(var attributes=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0))
            {
                if(attributes.IsInvalid)throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                var observed=CloudFilesNative.Inspect(attributes);if(observed?.Info.PinState!=2)return null;
                if(!CloudFilesNative.GetFileInformationByHandleEx(attributes,9,out var priorTag,8))throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                // 冷占位先用属性句柄排除，避免再次申请读数据oplock使Windows自动清除UNPINNED意图。
                if(observed.Value.Info.OnDiskSize==0&&(CloudFilesNative.CfGetPlaceholderStateFromAttributeTag(priorTag.Attributes,priorTag.Tag)&0x10)!=0)return null;
                before=LocalFileState.Read(attributes);
            }
            // 只读排他oplock不会因打开冷占位而触发正文水合；引用期间阻止外部写入或替换。
            using var handle=CloudFilesNative.OpenRead(path);
            if(!CloudFilesNative.CfReferenceProtectedHandle(handle))return null;
            try
            {
                using var borrowed=new SafeFileHandle(CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle),false);
                var local=LocalFileState.Read(borrowed);var info=CloudFilesNative.Inspect(borrowed);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                if(local.Key!=before.Key||local.IsDirectory||marker?.MountId!=_mount.Id||marker.EntryId!=entry.Id||
                    info!.Value.Info.PinState!=2||info.Value.Info.InSyncState!=1||info.Value.Info.ModifiedSize!=0||IsActive(entry.Id))return null;
                if(!CloudFilesNative.GetFileInformationByHandleEx(borrowed,9,out var tag,8))throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                // 已经仅联机时不反复修改USN；零字节的完整占位也需要真正完成脱水。
                if(info.Value.Info.OnDiskSize==0&&(CloudFilesNative.CfGetPlaceholderStateFromAttributeTag(tag.Attributes,tag.Tag)&0x10)!=0)return null;
                MountPathGuard.ValidateOperation(_mount,path);
                CloudFilesNative.Check(CloudFilesNative.CfDehydratePlaceholder(handle,0,-1,1,0));
                var after=CloudFilesNative.Inspect(borrowed)??throw new IOException("释放空间后占位身份不可读。");
                if(after.Info.InSyncState!=1||after.Info.PinState!=2||after.Info.ModifiedSize!=0||!after.Identity.SequenceEqual(info.Value.Identity))
                    throw new IOException("释放空间后状态核对失败，未更改同步标记。");
                return Math.Max(0,info.Value.Info.OnDiskSize-after.Info.OnDiskSize);
            }
            finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
        }
        finally{EndMetadataUpdate(entry.Id);}
    }
}
