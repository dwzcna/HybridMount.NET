using System.ComponentModel;
using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;
namespace HybridMount.Services.Mounting;

/// <summary>远端删除后的本地清理；只删除已核对的自家占位，不读取正文、不跟随链接。</summary>
internal static class CloudRemoteCleanup
{
    /// <summary>调用方持有根锁并确认没有上传或外部读取；保护句柄内复核身份及同步状态后按句柄删除。</summary>
    public static void Delete(MountDefinition mount,MountEntry entry)
    {
        var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);if(path==mount.LocalPath)throw new IOException("不能清理挂载根。");
        MountPathGuard.ValidateOperation(mount,path);var before=LocalFileState.Read(path);
        if(entry.Protected||entry.Deleted)throw new IOException("本地修改或删除意图受到保护。");
        if(entry.IsDirectory&&Directory.EnumerateFileSystemEntries(path).Any())throw new IOException("目录仍含本地内容，已保留。");
        CloudFilesNative.Check(CloudFilesNative.CfOpenFileWithOplock(path,5,out var handle));using(handle)
        {
            if(!CloudFilesNative.CfReferenceProtectedHandle(handle))throw new IOException("无法引用删除保护句柄。");
            try
            {
                var raw=CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle);using var borrowed=new SafeFileHandle(raw,false);
                var current=LocalFileState.Read(borrowed);var info=CloudFilesNative.Inspect(borrowed);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                if(current.Identity!=before.Identity||current.Key!=before.Key||marker?.MountId!=mount.Id||marker.EntryId!=entry.Id)
                    throw new IOException("清理前文件身份发生变化或不属于此挂载。");
                if(!entry.IsDirectory&&(info!.Value.Info.InSyncState!=1||info.Value.Info.ModifiedSize!=0))throw new IOException("本地内容已修改，保留文件。");
                // DELETE + IGNORE_READONLY_ATTRIBUTE，不使用 POSIX 强制删除；外部占用由系统共享规则拒绝。
                uint disposition=0x11;if(!SetDisposition(borrowed,21,ref disposition,4))throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
        }
    }
    [DllImport("kernel32.dll",EntryPoint="SetFileInformationByHandle",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetDisposition(SafeFileHandle handle,int infoClass,ref uint info,uint size);
}
