using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.Content;

/// <summary>暂存空间不足或查询失败的可恢复IO故障；由上传／远端任务所有者保留记录并延后复查。</summary>
internal sealed class StagingSpaceException(string message,Exception? inner=null):IOException(StagingDiskSpace.ErrorPrefix+message,inner);

/// <summary>无持久副作用的暂存空间策略，按目标所在卷的调用者可用字节核对；此检查不预留或保证后续分配。</summary>
internal static class StagingDiskSpace
{
    internal const string ErrorPrefix="暂存空间检查：";
    internal const ulong MarginBytes=64UL*1024*1024;
    internal static readonly TimeSpan RecheckDelay=TimeSpan.FromMinutes(1);
    /// <summary>兼容持久化Waiting原因，仅识别本策略生成的固定前缀。</summary>
    internal static bool IsWaiting(string state,string? error)=>state=="Waiting"&&error?.StartsWith(ErrorPrefix,StringComparison.Ordinal)==true;
    /// <summary>等待一分钟再查询；显式重试清除检查时间后可立即重新检查。</summary>
    internal static bool CanRecheck(LocalUpload item,DateTimeOffset now)=>!IsWaiting(item.State,item.Error)||item.LastCheckedUtc is null||now-item.LastCheckedUtc>=RecheckDelay;
    /// <summary>保留原快照和基线，产生可持久的等待截止；主动取消不重新入队。</summary>
    internal static LocalUpload Wait(LocalUpload item,StagingSpaceException error,DateTimeOffset now)=>item with{State=item.CancelRequested?"Canceled":"Waiting",Error=error.Message,LastCheckedUtc=now,NextAttemptUtc=item.CancelRequested?null:now+RecheckDelay};
    /// <summary>在目标最近存在的祖先卷核对正文大小加64MiB余量；负长度拒绝，空间或查询错误以专用异常报告。</summary>
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
    /// <summary>同步查询当前Windows用户可用字节，失败保留原生错误；调用方在根锁外执行。</summary>
    internal static ulong AvailableBytes(string directory)
    {
        if(!GetDiskFreeSpaceEx(directory,out var available,out _,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
        return available;
    }
    [DllImport("kernel32.dll",EntryPoint="GetDiskFreeSpaceExW",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory,out ulong available,out ulong total,out ulong free);
}

/// <summary>线程安全的远端暂存等待合并；同版本唤醒不反复查盘，新版本及显式重试不被旧截止阻挡。</summary>
internal sealed class StagingRetrySchedule
{
    private readonly ConcurrentDictionary<string,DateTimeOffset> _due=new(StringComparer.Ordinal);
    internal void Defer(string id,DateTimeOffset now)=>_due[id]=now+StagingDiskSpace.RecheckDelay;
    internal DateTimeOffset? NextRetryUtc(string id)=>_due.TryGetValue(id,out var next)?next:null;
    internal void Reset(string id)=>_due.TryRemove(id,out _);
    internal bool ShouldWait(RemotePendingChange item,DateTimeOffset now)
    {
        if(!StagingDiskSpace.IsWaiting(item.State,item.Error)){Reset(item.Id);return false;}
        return _due.TryGetValue(item.Id,out var due)&&now<due;
    }
}
