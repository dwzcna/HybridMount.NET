using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

/// <summary>Windows SDK 26100 CFAPI ABI；结构使用原生八字节对齐，回调不可向原生传播异常。</summary>
public static class CloudFilesNative
{
    [StructLayout(LayoutKind.Sequential)] public struct BasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }
    [StructLayout(LayoutKind.Sequential)] public struct FsMetadata { public BasicInfo Basic; public long FileSize; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct PlaceholderCreateInfo
    { [MarshalAs(UnmanagedType.LPWStr)] public string RelativeName; public FsMetadata Metadata; public nint Identity; public uint IdentityLength, Flags; public int Result; public long CreateUsn; }
    [StructLayout(LayoutKind.Sequential)] public struct CallbackInfo
    {
        public uint StructSize; public long ConnectionKey; public nint Context, VolumeGuidName, VolumeDosName; public uint VolumeSerialNumber;
        public long SyncRootFileId; public nint RootIdentity; public uint RootIdentityLength; public long FileId, FileSize;
        public nint FileIdentity; public uint FileIdentityLength; public nint NormalizedPath; public long TransferKey; public byte PriorityHint;
        public nint CorrelationVector, ProcessInfo; public long RequestKey;
    }
    [StructLayout(LayoutKind.Sequential)] public struct CallbackRegistration { public uint Type; public nint Callback; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Callback(in CallbackInfo info, nint parameters);
    [StructLayout(LayoutKind.Sequential)] public struct OperationInfo
    { public uint StructSize, Type; public long ConnectionKey, TransferKey; public nint CorrelationVector, SyncStatus; public long RequestKey; }
    [StructLayout(LayoutKind.Explicit,Size=48)] public struct OperationParameters
    {
        [FieldOffset(0)] public uint ParamSize; [FieldOffset(8)] public uint Flags; [FieldOffset(12)] public int Status;
        [FieldOffset(16)] public nint Buffer; [FieldOffset(24)] public long Offset; [FieldOffset(32)] public long Length;
    }
    [StructLayout(LayoutKind.Sequential)] public struct PlaceholderInfo
    { public long OnDiskSize, ValidatedSize, ModifiedSize, PropertiesSize; public uint PinState, InSyncState; public long FileId, RootFileId; public uint IdentityLength; }
    [StructLayout(LayoutKind.Sequential)] public struct AttributeTag { public uint Attributes, Tag; }
    /// <summary>保护句柄只能由 CfCloseHandle 释放。</summary>
    public sealed class ProtectedHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public ProtectedHandle() : base(true) { } protected override bool ReleaseHandle() { CfCloseHandle(handle); return true; } }
    public static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    public static ProtectedHandle Open(string path, bool exclusive = false)
    { Check(CfOpenFileWithOplock(path, (uint)(exclusive ? 3 : 10), out var handle)); return handle; }
    public static (PlaceholderInfo Info, byte[] Identity)? Inspect(string path)
    {
        using var handle = CreateFile(path, 0x80, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag tag, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((CfGetPlaceholderStateFromAttributeTag(tag.Attributes, tag.Tag) & 1) == 0) return null;
        var buffer = Marshal.AllocHGlobal(8192);
        try
        {
            Check(CfGetPlaceholderInfo(handle, 1, buffer, 8192, out _));
            var info = Marshal.PtrToStructure<PlaceholderInfo>(buffer);
            if (info.IdentityLength > 4096) throw new IOException("占位身份过长。");
            var bytes = new byte[info.IdentityLength]; Marshal.Copy(buffer + 60, bytes, 0, bytes.Length); return (info, bytes);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static byte[]? RootIdentity(string path, bool strict = false)
    {
        var buffer = Marshal.AllocHGlobal(8192);
        try
        {
            var result = CfGetSyncRootInfoByPath(path, 1, buffer, 8192, out _);
            // 只有明确的“未位于同步根”可用于安全删除判定，其他原生错误必须保留。
            if (result < 0)
            {
                if (strict && result is not (unchecked((int)0x80070186) or unchecked((int)0x80070195))) Check(result);
                return null;
            }
            // SDK CF_SYNC_ROOT_STANDARD_INFO：IdentityLength 位于 1052，字节数据位于 1056。
            var length = Marshal.ReadInt32(buffer,1052); if (length is < 1 or > 4096) return [];
            var bytes = new byte[length]; Marshal.Copy(buffer+1056,bytes,0,length); return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static bool IsCloudTag(uint tag) => (tag & 0xffff0fff) == 0x9000001a;
    public static bool IsCloudPath(string path)
    {
        using var handle = CreateFile(path,0x80,7,0,3,0x02200000,0);
        return !handle.IsInvalid && GetFileInformationByHandleEx(handle,9,out AttributeTag tag,8) && IsCloudTag(tag.Tag);
    }
    public static bool HasFullContent(string path, long size)
    {
        if (size == 0) return true;
        using var handle = CreateFile(path,0x80,7,0,3,0x02200000,0); if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error()); var buffer = Marshal.AllocHGlobal(65536);
        try
        {
            Check(CfGetPlaceholderRangeInfo(handle,1,0,size,buffer,65536,out var returned));
            long end = 0;
            for (var offset=0; offset+16<=returned; offset+=16)
            { var start=Marshal.ReadInt64(buffer,offset); var length=Marshal.ReadInt64(buffer,offset+8); if (start>end) return false; end=Math.Max(end,start+length); }
            return end>=size;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Overlap { public nint Internal,InternalHigh; public long Offset; public nint Event; }
    /// <summary>使用重叠 I/O 支持取消，取消后仍等待原生完成才释放缓冲和句柄。</summary>
    public static async Task HydrateAsync(ProtectedHandle handle,CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); using var signal=new EventWaitHandle(false,EventResetMode.ManualReset);
        var memory=Marshal.AllocHGlobal(Marshal.SizeOf<Overlap>());
        try
        {
            Marshal.StructureToPtr(new Overlap{Event=signal.SafeWaitHandle.DangerousGetHandle()},memory,false);
            var result=CfHydratePlaceholder(handle,0,-1,0,memory);
            if(result!=unchecked((int)0x800703e5)) { Check(result); return; }
            var complete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait=ThreadPool.RegisterWaitForSingleObject(signal,(_,_)=>complete.TrySetResult(),null,Timeout.Infinite,true);
            using var cancel=token.Register(()=>CancelIoEx(CfGetWin32HandleFromProtectedHandle(handle),memory));
            try
            {
                await complete.Task.ConfigureAwait(false);
                if(!GetOverlappedResult(CfGetWin32HandleFromProtectedHandle(handle),memory,out _,false)) { token.ThrowIfCancellationRequested(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            }
            finally { wait.Unregister(null); }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [DllImport("CldApi.dll")] private static extern nint CfGetWin32HandleFromProtectedHandle(ProtectedHandle handle);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool CancelIoEx(nint handle,nint overlap);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetOverlappedResult(nint handle,nint overlap,out uint count,bool wait);
    [DllImport("CldApi.dll",CharSet=CharSet.Unicode)] public static extern int CfConnectSyncRoot(string path,[In] CallbackRegistration[] callbacks,nint context,uint flags,out long key);
    [DllImport("CldApi.dll")] public static extern int CfDisconnectSyncRoot(long key);
    [DllImport("CldApi.dll")] public static extern int CfExecute(in OperationInfo info,ref OperationParameters parameters);
    [DllImport("CldApi.dll",CharSet=CharSet.Unicode)] public static extern int CfCreatePlaceholders(string path,[In,Out] PlaceholderCreateInfo[] entries,uint count,uint flags,out uint processed);
    [DllImport("CldApi.dll",CharSet=CharSet.Unicode)] public static extern int CfOpenFileWithOplock(string path,uint flags,out ProtectedHandle handle);
    [DllImport("CldApi.dll")] private static extern void CfCloseHandle(nint handle);
    [DllImport("CldApi.dll")] public static extern int CfGetPlaceholderInfo(SafeHandle handle,uint infoClass,nint buffer,uint length,out uint returned);
    [DllImport("CldApi.dll",CharSet=CharSet.Unicode)] public static extern int CfGetSyncRootInfoByPath(string path,uint infoClass,nint buffer,uint length,out uint returned);
    [DllImport("CldApi.dll")] public static extern uint CfGetPlaceholderStateFromAttributeTag(uint attributes,uint tag);
    [DllImport("CldApi.dll")] public static extern int CfHydratePlaceholder(ProtectedHandle handle,long offset,long length,uint flags,nint overlap);
    [DllImport("CldApi.dll")] public static extern int CfDehydratePlaceholder(ProtectedHandle handle,long offset,long length,uint flags,nint overlap);
    [StructLayout(LayoutKind.Sequential)] public struct FileRange { public long Offset,Length; }
    [DllImport("CldApi.dll")] public static extern int CfUpdatePlaceholder(ProtectedHandle handle,in FsMetadata metadata,nint identity,uint identityLength,[In] FileRange[] ranges,uint count,uint flags,nint usn,nint overlap);
    [DllImport("CldApi.dll")] public static extern int CfRevertPlaceholder(ProtectedHandle handle,uint flags,nint overlap);
    [DllImport("CldApi.dll")] public static extern int CfSetPinState(ProtectedHandle handle,uint state,uint flags,nint overlap);
    [DllImport("CldApi.dll")] public static extern int CfGetPlaceholderRangeInfo(SafeHandle handle,uint infoClass,long offset,long length,nint buffer,uint size,out int returned);
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] public static extern SafeFileHandle CreateFile(string path,uint access,uint share,nint security,uint creation,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int infoClass,out AttributeTag info,uint size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)] public static extern bool GetFileId(SafeFileHandle handle,int infoClass,[Out] byte[] info,uint size);
}
