using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Models.Mounting.Windows;

/// <summary>Windows SDK 26100 CFAPI ABI；结构使用原生八字节对齐，回调不可向原生传播异常。</summary>
public static class CloudFilesNative
{
    /// <summary>SDK CF_PIN_STATE_EXCLUDED；仅提供程序用于排除上传，不代表同步完成。</summary>
    internal const uint PinStateExcluded=3;
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
    public static void Check(int result) { if(result<0)Marshal.ThrowExceptionForHR(result); }
#if HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>保护句柄只能由 CfCloseHandle 释放。</summary>
    public sealed class ProtectedHandle : SafeHandleZeroOrMinusOneIsInvalid
    { public ProtectedHandle() : base(true) { } protected override bool ReleaseHandle() { CfCloseHandle(handle); return true; } }
    public static ProtectedHandle Open(string path, bool exclusive = false)
    { Check(CfOpenFileWithOplock(path, (uint)(exclusive ? 3 : 10), out var handle)); return handle; }
    /// <summary>后台资源读取只请求读权限和排他 oplock；调用方引用句柄后再核对完整性。</summary>
    public static ProtectedHandle OpenRead(string path)
    { Check(CfOpenFileWithOplock(path,1,out var handle));return handle; }
#endif
    private static (PlaceholderInfo Info,byte[] Identity)? FromNative(NativePlaceholder? value)=>value is null?null:(new PlaceholderInfo{OnDiskSize=value.OnDiskSize,ValidatedSize=value.ValidatedSize,ModifiedSize=value.ModifiedSize,PropertiesSize=value.PropertiesSize,PinState=value.PinState,InSyncState=value.InSyncState,FileId=value.FileId,RootFileId=value.RootFileId,IdentityLength=(uint)value.Identity.Length},value.Identity);
    internal static NativeFileSnapshot ReadNative(string path,bool ranges=false,string? expectedIdentity=null)
    {
        try{return NativeRuntimeClient.Get().InspectAsync(path,persisted:true,ranges:ranges,expectedIdentity:expectedIdentity).GetAwaiter().GetResult();}
        catch(COMException error)when((unchecked((uint)error.HResult)&0xffff0000)==0x80070000){throw new Win32Exception(error.HResult&0xffff);}
    }
    /// <summary>查询持久占位身份由原生提供程序完成，避免跨进程标签过滤误判为普通文件。</summary>
    public static (PlaceholderInfo Info,byte[] Identity)? Inspect(string path)=>FromNative(ReadNative(path).Placeholder);
    /// <summary>属性句柄只提供路径和文件ID证明；原生端从同一身份复核后返回模型，不借出句柄。</summary>
    public static (PlaceholderInfo Info,byte[] Identity)? Inspect(SafeFileHandle handle)=>FromNative(ReadNative(HandlePath(handle),expectedIdentity:LocalFileState.Read(handle).Identity).Placeholder);
    internal static string HandlePath(SafeFileHandle handle)
    {
        var text=new System.Text.StringBuilder(32768);var count=GetFinalPathNameByHandle(handle,text,(uint)text.Capacity,0);if(count==0||count>=text.Capacity)throw new Win32Exception(Marshal.GetLastWin32Error());var path=text.ToString();return path.StartsWith(@"\\?\",StringComparison.Ordinal)?path[4..]:path;
    }
    [DllImport("kernel32.dll",EntryPoint="GetFinalPathNameByHandleW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,System.Text.StringBuilder path,uint count,uint flags);
    internal static (PlaceholderInfo Info,byte[] Identity) InspectPersisted(string path)=>Inspect(path)??throw new Win32Exception(376);
    public static byte[]? RootIdentity(string path,bool strict=false)
    {
        var result=NativeRuntimeClient.Get().RootInfoAsync(path).GetAwaiter().GetResult();var code=result.GetProperty("hresult").GetInt32();
        if(code<0){if(strict&&code is not(unchecked((int)0x80070186) or unchecked((int)0x80070195)))CheckRootQueryResult(code,path);return null;}
        return result.GetProperty("identity").GetBytesFromBase64();
    }
    /// <summary>严格根查询保留原HRESULT；卷未提供CFAPI功能时提示核验过滤器实例，不将失败当成“没有同步根”。</summary>
    internal static void CheckRootQueryResult(int result,string path)
    {
        if(result==unchecked((int)0x80070001))
        {
            var volume=Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            var command=string.IsNullOrEmpty(volume)?"fltmc.exe instances -f CldFlt":"fltmc.exe attach CldFlt "+volume;
            throw new COMException($"本地 Cloud Files 同步根查询失败（0x80070001）。请确认 CldFlt 已附加到目标卷 {volume}；服务处于 RUNNING 不代表已附加到此卷。管理员终端可核验实例并按需执行：{command}",result);
        }
        Check(result);
    }
    [DllImport("shell32.dll",EntryPoint="SHChangeNotify",CharSet=CharSet.Unicode)] private static extern void ShellChangeNotify(uint change,uint flags,string path,nint second);
    /// <summary>Unicode 路径通知采用不等待 Shell 的刷新模式；调用前必须释放资源句柄。</summary>
    public static void NotifyItemUpdated(string path)=>ShellChangeNotify(0x2000,0x3005,path,0);
    /// <summary>缩略图已落库后只排队单文件更新，由Shell缓冲合并；不强制刷新或阻塞Explorer的输入与光标。</summary>
    internal static void NotifyThumbnailUpdated(string path)=>ShellChangeNotify(0x2000,0x0005,path,0);
    public static bool IsCloudTag(uint tag) => (tag & 0xffff0fff) == 0x9000001a;
    public static bool IsCloudPath(string path){var value=ReadNative(path);return value.Placeholder is not null||IsCloudTag(value.Tag);}
    public static bool HasFullContent(string path,long size){var result=ReadNative(path,ranges:true);return result.Stamp.Length==size&&(result.Placeholder is null||result.Full);}
#if HYBRIDMOUNT_HEADLESS_TEST
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
    [DllImport("CldApi.dll")][return:MarshalAs(UnmanagedType.U1)] public static extern bool CfReferenceProtectedHandle(ProtectedHandle handle);
    [DllImport("CldApi.dll")] public static extern void CfReleaseProtectedHandle(ProtectedHandle handle);
    [DllImport("CldApi.dll")] public static extern nint CfGetWin32HandleFromProtectedHandle(ProtectedHandle handle);
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
    /// <summary>仅属性句柄设置原生固定意图，不因申请正文访问而隐式水合；调用者负责核对根及条目身份。</summary>
    internal static int SetPinIntent(SafeFileHandle handle,uint state,uint flags,nint overlap)
    {
        if(flags!=0||overlap!=0)throw new NotSupportedException();try{NativeRuntimeClient.Get().Invoke("testSetPin",new{path=HandlePath(handle),expectedIdentity=LocalFileState.Read(handle).Identity,pin=state});return 0;}catch(COMException error){return error.HResult;}
    }
    [DllImport("CldApi.dll")] public static extern int CfGetPlaceholderRangeInfo(SafeHandle handle,uint infoClass,long offset,long length,nint buffer,uint size,out int returned);
#endif
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)] public static extern SafeFileHandle CreateFile(string path,uint access,uint share,nint security,uint creation,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)] public static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int infoClass,out AttributeTag info,uint size);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)] public static extern bool GetFileId(SafeFileHandle handle,int infoClass,[Out] byte[] info,uint size);
}
