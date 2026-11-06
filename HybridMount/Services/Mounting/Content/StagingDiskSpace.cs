using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

internal sealed class StagingSpaceException(string message,Exception? inner=null):IOException(StagingDiskSpace.ErrorPrefix+message,inner);

internal static class StagingDiskSpace
{
    internal const string ErrorPrefix="暂存空间检查：";
    internal const ulong MarginBytes=64UL*1024*1024;
    internal static readonly TimeSpan RecheckDelay=TimeSpan.FromMinutes(1);
    internal static bool IsWaiting(string state,string? error)=>state=="Waiting"&&error?.StartsWith(ErrorPrefix,StringComparison.Ordinal)==true;
    internal static bool CanRecheck(LocalUpload item,DateTimeOffset now)=>!IsWaiting(item.State,item.Error)||item.LastCheckedUtc is null||now-item.LastCheckedUtc>=RecheckDelay;
    internal static LocalUpload Wait(LocalUpload item,StagingSpaceException error,DateTimeOffset now)=>item with{State=item.CancelRequested?"Canceled":"Waiting",Error=error.Message,LastCheckedUtc=now};
    internal static void Require(string target,long size,Func<string,ulong>? availableBytes=null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);ulong available;
        try
        {
            var directory=Path.GetDirectoryName(Path.GetFullPath(target))!;
            while(!Directory.Exists(directory))directory=Path.GetDirectoryName(directory)??throw new DirectoryNotFoundException();
            available=(availableBytes??AvailableBytes)(directory);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or Win32Exception)
        {throw new StagingSpaceException("无法读取目标卷可用空间，稍后自动重试。",error);}
        var required=(ulong)size+MarginBytes;
        if(available<required)throw new StagingSpaceException($"磁盘空间不足，需要 {required:N0} 字节（文件大小加 64 MiB 余量），可用 {available:N0} 字节；释放空间后稍后自动重试。");
    }
    internal static ulong AvailableBytes(string directory)
    {
        if(!GetDiskFreeSpaceEx(directory,out var available,out _,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
        return available;
    }
    [DllImport("kernel32.dll",EntryPoint="GetDiskFreeSpaceExW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory,out ulong available,out ulong total,out ulong free);
}

internal sealed class StagingRetrySchedule
{
    private readonly ConcurrentDictionary<string,DateTimeOffset> _due=new(StringComparer.Ordinal);
    internal void Defer(string id,DateTimeOffset now)=>_due[id]=now+StagingDiskSpace.RecheckDelay;
    internal void Reset(string id)=>_due.TryRemove(id,out _);
    internal bool ShouldWait(RemotePendingChange item,DateTimeOffset now)
    {
        if(!StagingDiskSpace.IsWaiting(item.State,item.Error)){Reset(item.Id);return false;}
        return _due.TryGetValue(item.Id,out var due)&&now<due;
    }
}
