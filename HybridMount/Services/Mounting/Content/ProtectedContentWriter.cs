using System.ComponentModel;
using System.Runtime.InteropServices;
namespace HybridMount.Services.Mounting;

/// <summary>显式冲突替换专用：借用CFAPI保护句柄写入完整已验证快照，不重新绑定I/O完成端口。</summary>
internal static class ProtectedContentWriter
{
    [StructLayout(LayoutKind.Sequential)]private struct Overlap{public nint Internal,InternalHigh;public long Offset;public nint Event;}
    [StructLayout(LayoutKind.Sequential)]private struct BasicInfo{public long Created,Accessed,Written,Changed;public uint Attributes;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool WriteFile(nint file,nint buffer,uint count,out uint written,nint overlap);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetOverlappedResult(nint file,nint overlap,out uint written,bool wait);
    [DllImport("kernel32.dll",EntryPoint="SetFileInformationByHandle",SetLastError=true)]private static extern bool SetEnd(nint file,int type,ref long length,uint size);
    [DllImport("kernel32.dll",EntryPoint="SetFileInformationByHandle",SetLastError=true)]private static extern bool SetBasic(nint file,int type,ref BasicInfo info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool FlushFileBuffers(nint file);
    /// <summary>一旦开始修改正文则不响应取消，必须完成或留下明确恢复阶段；独立低位事件不把完成包交给CFAPI。</summary>
    internal static void Replace(nint handle,Stream source,long length,DateTimeOffset modified,Action? written=null)
    {
        var bytes=new byte[262144];var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);var memory=Marshal.AllocHGlobal(Marshal.SizeOf<Overlap>());
        using var signal=new EventWaitHandle(false,EventResetMode.ManualReset);long position=0;
        try
        {
            int count;
            while((count=source.Read(bytes,0,bytes.Length))!=0)
            {
                var consumed=0;
                while(consumed<count)
                {
                    signal.Reset();Marshal.StructureToPtr(new Overlap{Offset=position,Event=signal.SafeWaitHandle.DangerousGetHandle()|1},memory,false);
                    if(!WriteFile(handle,pin.AddrOfPinnedObject()+consumed,(uint)(count-consumed),out var amount,memory))
                    {var error=Marshal.GetLastWin32Error();if(error!=997)throw new Win32Exception(error);if(!GetOverlappedResult(handle,memory,out amount,true))throw new Win32Exception(Marshal.GetLastWin32Error());}
                    if(amount==0)throw new IOException("替换正文写入未取得进展，保留恢复备份。");position+=amount;consumed+=checked((int)amount);
                }
                written?.Invoke();
            }
            if(position!=length)throw new IOException("远端快照长度变化，保留恢复备份。");
            if(!SetEnd(handle,6,ref length,8))throw new Win32Exception(Marshal.GetLastWin32Error());
            var basic=new BasicInfo{Written=modified.UtcDateTime.ToFileTimeUtc()};if(!SetBasic(handle,0,ref basic,(uint)Marshal.SizeOf<BasicInfo>()))throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!FlushFileBuffers(handle))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally{Marshal.FreeHGlobal(memory);pin.Free();}
    }
}
