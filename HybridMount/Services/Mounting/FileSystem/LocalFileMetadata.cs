using System.ComponentModel;
using System.Runtime.InteropServices;
namespace HybridMount.Services.Mounting;

/// <summary>只读元数据查询；正常缺失返回空，权限、占用及其他系统错误仍明确失败，不打开正文或触发水合。</summary>
internal static class LocalFileMetadata
{
    internal readonly record struct Value(FileAttributes Attributes,long Length);
    public static Value? TryRead(string path)
    {
        if(GetFileAttributesEx(path,0,out var info))return new((FileAttributes)info.Attributes,checked(((long)info.SizeHigh<<32)|info.SizeLow));
        var error=Marshal.GetLastWin32Error();if(error is 2 or 3)return null;throw new Win32Exception(error);
    }
    [StructLayout(LayoutKind.Sequential)]private struct AttributesData
    {public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,SizeHigh,SizeLow;}
    [DllImport("kernel32.dll",EntryPoint="GetFileAttributesExW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileAttributesEx(string path,int level,out AttributesData data);
}
