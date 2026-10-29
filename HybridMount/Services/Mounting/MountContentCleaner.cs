using System.ComponentModel;
using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

/// <summary>在后台线程清空已确认的挂载内容；锁住路径、按句柄删除，始终保留根目录。</summary>
internal static class MountContentCleaner
{
    // 不申请内容读取权限；不共享写入／删除，防止检查后目录被改成链接或移动。
    private const uint ReadAttributes=0x80, DeleteAccess=0x10000, ShareRead=1, OpenNoFollow=0x02200000;
    private sealed class PathLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles=[];
        internal PathLease(MountDefinition mount)
        {
            try
            {
                var current=Path.GetPathRoot(mount.LocalPath)!;
                foreach(var segment in Path.GetRelativePath(current,mount.LocalPath).Split(Path.DirectorySeparatorChar))
                {
                    current=Path.Combine(current,segment);
                    var handle=Open(current,false);_handles.Add(handle);
                    var tag=Tag(handle);
                    if((tag.Attributes&0x10)==0 || ((tag.Attributes&0x400)!=0 && (!current.Equals(mount.LocalPath,StringComparison.OrdinalIgnoreCase)||!CloudFilesNative.IsCloudTag(tag.Tag))))
                        throw new IOException("挂载路径包含非目录或不受信任的重解析点。");
                }
                if(_handles.Count==0 || Identity(_handles[^1])!=mount.RootIdentity)throw new IOException("挂载根身份发生变化，拒绝清空。");
            }
            catch { Dispose();throw; }
        }
        public void Dispose() { for(var index=_handles.Count-1;index>=0;index--)_handles[index].Dispose();_handles.Clear(); }
    }
    private static SafeFileHandle Open(string path,bool delete)
    {
        var handle=CloudFilesNative.CreateFile(path,ReadAttributes|(delete?DeleteAccess:0),ShareRead,0,3,OpenNoFollow,0);
        if(!handle.IsInvalid)return handle;
        var error=Marshal.GetLastWin32Error();handle.Dispose();throw new Win32Exception(error);
    }
    private static CloudFilesNative.AttributeTag Tag(SafeFileHandle handle)
    {
        if(!CloudFilesNative.GetFileInformationByHandleEx(handle,9,out var tag,8))throw new Win32Exception(Marshal.GetLastWin32Error());
        return tag;
    }
    private static string Identity(SafeFileHandle handle)
    {
        var bytes=new byte[24];if(!CloudFilesNative.GetFileId(handle,18,bytes,24))throw new Win32Exception(Marshal.GetLastWin32Error());return Convert.ToHexString(bytes);
    }
    /// <summary>清空目录并返回失败项数；回调报告动态总数及逐项结果，取消不恢复已删内容。</summary>
    /// <param name="registered">同步根仍注册时验证提供程序归属；解除阶段仅接受无云占位的普通目录。</param>
    internal static int Clear(MountDefinition mount,bool registered,CancellationToken token,Action<int> discovered,Action<string,string,string> result)
    {
        using var lease=new PathLease(mount);
        if(registered)MountPathGuard.ValidateOperation(mount,mount.LocalPath);
        else if(CloudFilesNative.RootIdentity(mount.LocalPath) is not null)throw new IOException("路径重新属于同步根，停止清空。");
        return ClearDirectory(mount.LocalPath);

        int ClearDirectory(string directory)
        {
            token.ThrowIfCancellationRequested();
            var children=Directory.EnumerateFileSystemEntries(directory).ToArray();discovered(children.Length);
            var failures=0;
            foreach(var path in children)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using(var handle=Open(path,true))
                    {
                        var tag=Tag(handle);var cloud=CloudFilesNative.IsCloudTag(tag.Tag);
                        if(cloud)
                        {
                            if(!registered)throw new IOException("注销阶段仍发现云占位，停止处理该项。");
                            MountPathGuard.ValidateOperation(mount,path);
                        }
                        // 普通重解析点只删除链接自身；云目录属于本根时才遍历其实际子项。
                        if((tag.Attributes&0x10)!=0 && ((tag.Attributes&0x400)==0 || cloud))failures+=ClearDirectory(path);
                        token.ThrowIfCancellationRequested();
                        var disposition=new Disposition{DeleteFile=1};
                        if(!SetFileInformationByHandle(handle,4,ref disposition,1))throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    result(path,"Completed","已删除本地内容。");
                }
                catch(OperationCanceledException) { throw; }
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or Win32Exception)
                { failures++;result(path,"Failed",error.Message); }
            }
            return failures;
        }
    }
    /// <summary>在锁住根路径时检查是否清空；身份或链接变化会拒绝完成移除。</summary>
    internal static bool IsEmpty(MountDefinition mount)
    { using var lease=new PathLease(mount);return !Directory.EnumerateFileSystemEntries(mount.LocalPath).Any(); }
    [StructLayout(LayoutKind.Sequential)] private struct Disposition { public byte DeleteFile; }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file,int infoClass,ref Disposition info,uint size);
}
