using System.ComponentModel;
using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Mounting;

public sealed partial class CloudMountSession
{
    internal event Action<string,LocalFileStamp>? LocalWriteCompleted;
    internal void RecordLocalWrite(MountEntry entry,LocalFileStamp stamp)
    {
        if(!entry.IsDirectory&&!_clearing&&!_stop.IsCancellationRequested)LocalWriteCompleted?.Invoke(entry.LocalPath,stamp);
    }
    private void RecordProtectedWrite(MountEntry entry,SafeFileHandle handle)
    {
        if(LocalWriteCompleted is null||entry.IsDirectory)return;
        try{RecordLocalWrite(entry,LocalFileState.Read(handle));}
        catch(Exception error)when(error is IOException or Win32Exception or UnauthorizedAccessException){}
    }
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
    private void RecordHydrationWrite(MountEntry entry,SourceEntry metadata,LocalFileStamp? before)
    {
        // CfExecute 结束时用户可能已恢复写入，只接受入口与出口完全不变的干净戳。
        if(before is not null&&HydrationStamp(entry,metadata) is {} after&&before==after)RecordLocalWrite(entry,after);
    }
    private void RecordCreatedWrite(MountEntry entry,SourceEntry metadata,long usn)
    {
        if(usn!=0&&HydrationStamp(entry,metadata) is {} stamp&&stamp.Usn==usn)RecordLocalWrite(entry,stamp);
    }
}
