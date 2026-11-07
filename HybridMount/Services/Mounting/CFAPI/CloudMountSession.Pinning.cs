using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class CloudMountSession
{
    private readonly SharedAsyncWork<string,bool> _pinnedHydration=new();
    /// <summary>资源真正归还后仅唤醒固定维护，不进入上传扫描；停止时由消费者解除订阅。</summary>
    internal event Action? PinWorkCompleted;
    internal Func<Task>? PinResourcesReleasingForTests {get;set;}
    private void NotifyPinWorkCompleted()
    {if(PinWorkCompleted is {} handlers)foreach(Action handler in handlers.GetInvocationList())try{handler();}catch(Exception error){_logger.Queue("固定意图","Warning",error.GetType().Name);}}
    private readonly ConcurrentDictionary<string,SemaphoreSlim> _pinGates=new();
    /// <summary>只查属性和本地范围；调用者持根锁且已排除上传及远端屏障，不读取正文或改变固定意图。</summary>
    internal (bool Pinned,bool NeedsDownload) ReadPinIntent(MountEntry entry)
    {
        var path=RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(_mount,path);
        using var attributes=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0);
        if(attributes.IsInvalid)throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        var info=CloudFilesNative.Inspect(attributes);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        if(marker?.MountId!=_mount.Id||marker.EntryId!=entry.Id||info!.Value.Info.PinState!=1||info.Value.Info.InSyncState!=1||info.Value.Info.ModifiedSize!=0)return(false,false);
        if(!CloudFilesNative.GetFileInformationByHandleEx(attributes,9,out var tag,8))throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        var length=LocalFileState.Read(attributes).Length;
        return(true,(CloudFilesNative.CfGetPlaceholderStateFromAttributeTag(tag.Attributes,tag.Tag)&0x10)!=0||!CloudFilesNative.HasFullContent(path,length));
    }
    /// <summary>显式固定只修改已核验身份的属性；不持根锁打开正文或等待下载。</summary>
    internal void RequestPin(MountEntry entry)
    {
        var path=RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(_mount,path);
        using var attributes=CloudFilesNative.CreateFile(path,0x180,7,0,3,0x02200000,0);
        if(attributes.IsInvalid)throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        var info=CloudFilesNative.Inspect(attributes);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        if(marker?.MountId!=_mount.Id||marker.EntryId!=entry.Id)throw new IOException("固定目标身份已变化。");
        CloudFilesNative.Check(CloudFilesNative.SetPinIntent(attributes,1,0,0));
    }
    /// <summary>原生固定与显式下载共享同条目的水合；每位调用者独立取消，工作自己拥有句柄和停止令牌，不重新设置Pin或InSync。</summary>
    internal Task HydratePinnedAsync(MountEntry entry,CancellationToken token)=>_pinnedHydration.RunAsync(entry.Id,async shared=>
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(shared,_stop.Token);
        var cancel=stop.Token;var gate=_pinGates.GetOrAdd(entry.Id,_=>new(1,1));await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            cancel.ThrowIfCancellationRequested();var path=RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(_mount,path);
            using var priority=PrioritizeDownload(entry.Id);using var handle=CloudFilesNative.OpenRead(path);
            if(!CloudFilesNative.CfReferenceProtectedHandle(handle))throw new IOException("固定下载无法取得文件保护，稍后重试。");
            try
            {
                using var borrowed=new SafeFileHandle(CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle),false);
                var before=LocalFileState.Read(borrowed);var info=CloudFilesNative.Inspect(borrowed);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                var current=_repository.Entries(_mount.Id).FirstOrDefault(x=>x.Id==entry.Id);
                if(_clearing||current is null||current.Deleted||current.Protected||current.Version!=entry.Version||current.LocalPath!=entry.LocalPath||current.RemotePresence==RemotePresence.Deleted||
                    _repository.HasPendingRemote(_mount.Id,entry.SourcePath)||_repository.Uploads(_mount.Id).Any(x=>x.LocalPath==entry.LocalPath&&x.State!="Completed")||
                    marker?.MountId!=_mount.Id||marker.EntryId!=entry.Id||info!.Value.Info.PinState!=1||info.Value.Info.InSyncState!=1||info.Value.Info.ModifiedSize!=0)
                    throw new IOException("固定下载遇到本地修改、未决工作或身份变化，已保留内容。");
                await CloudFilesNative.HydrateAsync(handle,cancel).ConfigureAwait(false);cancel.ThrowIfCancellationRequested();
                var after=LocalFileState.Read(borrowed);var final=CloudFilesNative.Inspect(borrowed);
                if(final is null||final.Value.Info.PinState!=1||final.Value.Info.InSyncState!=1||final.Value.Info.ModifiedSize!=0||!final.Value.Identity.SequenceEqual(info.Value.Identity)||
                    before.Identity!=after.Identity||before.Length!=after.Length||before.LastWriteTime!=after.LastWriteTime||MountPathGuard.FileIdentity(path)!=after.Identity||!CloudFilesNative.HasFullContent(path,after.Length))
                    throw new IOException("固定下载完成后的文件状态已变化，未标记为完成。");
                // 保护句柄内已证明正文/身份未变，只吸收本次水合的可靠属性戳，不能按路径或时间窗过滤编辑。
                if(after.Usn!=0)RecordLocalWrite(entry,after);
                _localContentReady?.Invoke(entry);return true;
            }
            finally
            {
                try{if(PinResourcesReleasingForTests is {} checkpoint)await checkpoint().ConfigureAwait(false);}
                finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
            }
        }
        finally{gate.Release();NotifyPinWorkCompleted();}
    },token);
}
