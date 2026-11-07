using System.ComponentModel;
using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

public sealed partial class CloudMountSession
{
    /// <summary>CFAPI工作线程上的已确认文件写入通知；订阅者只记录内存戳，不执行网络、持久化或抛出异常。</summary>
    internal event Action<string,LocalFileStamp>? LocalWriteCompleted;
    /// <summary>仅活动会话的普通文件发布自写入戳，停止或清理阶段不新增过滤证明。</summary>
    internal void RecordLocalWrite(MountEntry entry,LocalFileStamp stamp)
    {
        if(!entry.IsDirectory&&!_clearing&&!_stop.IsCancellationRequested)LocalWriteCompleted?.Invoke(entry.LocalPath,stamp);
    }
    /// <summary>保护句柄仍有效时记录元数据更新后的戳；记录失败保留正常扫描行为。</summary>
    private void RecordProtectedWrite(MountEntry entry,SafeFileHandle handle)
    {
        if(LocalWriteCompleted is null||entry.IsDirectory)return;
        try{RecordLocalWrite(entry,LocalFileState.Read(handle));}
        catch(Exception error)when(error is IOException or Win32Exception or UnauthorizedAccessException){}
    }
    /// <summary>只读属性核对本挂载、条目身份及干净状态；可靠USN、长度和时间均匹配才提供候选戳。</summary>
    private LocalFileStamp? HydrationStamp(MountEntry entry,SourceEntry metadata)
    {
        if(LocalWriteCompleted is null||entry.IsDirectory||entry.Protected)return null;
        try
        {
            using var handle=LocalWriteStampCache.OpenRead(RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath));
            var info=CloudFilesNative.Inspect(handle);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            if(marker?.MountId!=_mount.Id||marker.EntryId!=entry.Id||info!.Value.Info.InSyncState!=1||info.Value.Info.ModifiedSize!=0)return null;
            var stamp=LocalFileState.Read(handle);
            return stamp.Usn!=0&&!stamp.IsDirectory&&stamp.Length==metadata.Size&&stamp.LastWriteTime==metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc()?stamp:null;
        }
        catch(Exception error)when(error is IOException or Win32Exception or UnauthorizedAccessException or COMException or ArgumentException){return null;}
    }
    /// <summary>水合前后完整戳相同才记录；用户恢复写入后的任何差异保留为普通扫描事件。</summary>
    private void RecordHydrationWrite(MountEntry entry,SourceEntry metadata,LocalFileStamp? before)
    {
        // CfExecute 结束时用户可能已恢复写入，只接受入口与出口完全不变的干净戳。
        if(before is not null&&HydrationStamp(entry,metadata) is {} after&&before==after)RecordLocalWrite(entry,after);
    }
    /// <summary>只记录CFAPI创建回执USN仍匹配的文件，不掩盖创建后的外部编辑。</summary>
    private void RecordCreatedWrite(MountEntry entry,SourceEntry metadata,long usn)
    {
        if(usn!=0&&HydrationStamp(entry,metadata) is {} stamp&&stamp.Usn==usn)RecordLocalWrite(entry,stamp);
    }
}
