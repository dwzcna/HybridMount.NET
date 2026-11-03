using System.ComponentModel;
using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

/// <summary>通过文件句柄读取身份、USN 和元数据；不会打开文件正文或触发水合。</summary>
internal static class LocalFileState
{
    [StructLayout(LayoutKind.Sequential)] private struct StandardInfo{public long AllocationSize,EndOfFile;public uint Links;public byte DeletePending,Directory;}
    public static LocalFileStamp Read(string path)
    {
        using var handle=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());return Read(handle);
    }
    public static LocalFileStamp Read(SafeFileHandle handle)
    {
        if(!GetBasic(handle,0,out var basic,40)||!GetStandard(handle,1,out var standard,24))throw new Win32Exception(Marshal.GetLastWin32Error());
        var identity=new byte[24];if(!CloudFilesNative.GetFileId(handle,18,identity,24))throw new Win32Exception(Marshal.GetLastWin32Error());
        var output=new byte[4096];long usn=0;uint reasons=0;
        if(DeviceIoControl(handle,0x000900eb,new byte[]{2,0,4,0},4,output,(uint)output.Length,out var returned,0)&&returned>=32)
        {var major=BitConverter.ToUInt16(output,4);if(major==2){usn=BitConverter.ToInt64(output,24);if(returned>=44)reasons=BitConverter.ToUInt32(output,40);}else if(major==3&&returned>=48){usn=BitConverter.ToInt64(output,40);if(returned>=60)reasons=BitConverter.ToUInt32(output,56);}}
        return new(Convert.ToHexString(identity),usn,standard.EndOfFile,basic.LastWriteTime,basic.ChangeTime,standard.Directory!=0,reasons);
    }
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)] private static extern bool GetBasic(SafeFileHandle handle,int infoClass,out CloudFilesNative.BasicInfo info,uint size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)] private static extern bool GetStandard(SafeFileHandle handle,int infoClass,out StandardInfo info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle handle,uint code,byte[] input,uint inputSize,byte[] output,uint outputSize,out uint returned,nint overlap);
}
