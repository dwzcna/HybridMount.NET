using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class CloudMountSession
{
    private readonly SharedAsyncWork<string,bool> _pinnedHydration=new();
    /// <summary>资源真正归还后仅唤醒固定维护，不进入上传扫描；停止时由消费者解除订阅。</summary>
    internal event Action? PinWorkCompleted;
    internal Func<CancellationToken,Task>? PinResourcesReleasingForTests {get;set;}
    private void NotifyPinWorkCompleted()
    {if(PinWorkCompleted is {} handlers)foreach(Action handler in handlers.GetInvocationList())try{handler();}catch(Exception error){_logger.Queue("固定意图","Warning",error.GetType().Name);}}
    private readonly ConcurrentDictionary<string,SemaphoreSlim> _pinGates=new();
    /// <summary>只查属性和本地范围；调用者持根锁且已排除上传及远端屏障，不读取正文或改变固定意图。</summary>
    internal (bool Pinned,bool NeedsDownload) ReadPinIntent(MountEntry entry)
    {
        var path=RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(_mount,path);
        var snapshot=Runtime.InspectAsync(path,ranges:true).GetAwaiter().GetResult();var info=snapshot.Placeholder;var marker=info is null?null:MountPathGuard.ReadEntry(info.Identity);
        if(marker?.MountId!=_mount.Id||marker.EntryId!=entry.Id||info!.PinState!=1||info.InSyncState!=1||info.ModifiedSize!=0)return(false,false);
        return(true,(snapshot.State&0x10)!=0||!snapshot.Full);
    }
    /// <summary>显式固定只由原生属性操作修改已核验条目。</summary>
    internal void RequestPin(MountEntry entry)=>Runtime.Invoke("requestPin",EntryArgs(entry));
    /// <summary>原生固定与显式下载共享同条目的水合；每位调用者独立取消，工作自己拥有句柄和停止令牌，不重新设置Pin或InSync。</summary>
    internal Task HydratePinnedAsync(MountEntry entry,CancellationToken token)=>_pinnedHydration.RunAsync(entry.Id,async shared=>
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(shared,_stop.Token);
        var cancel=stop.Token;var gate=_pinGates.GetOrAdd(entry.Id,_=>new(1,1));await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            cancel.ThrowIfCancellationRequested();using var priority=PrioritizeDownload(entry.Id);
            var result=await Runtime.InvokeAsync("hydratePinned",EntryArgs(entry),async(stage,_,checkpointToken)=>
            {
                checkpointToken.ThrowIfCancellationRequested();
                if(stage=="pinnedGuarded")
                {
                    var current=_repository.Entries(_mount.Id).FirstOrDefault(x=>x.Id==entry.Id);
                    if(_clearing||current is null||current.Deleted||current.Protected||current.Version!=entry.Version||current.LocalPath!=entry.LocalPath||current.RemotePresence==RemotePresence.Deleted||_repository.HasPendingRemote(_mount.Id,entry.SourcePath)||_repository.Uploads(_mount.Id).Any(x=>x.LocalPath==entry.LocalPath&&x.State!="Completed"))throw new IOException("固定下载遇到本地修改、未决工作或身份变化，已保留内容。");
                }
                else if(stage=="pinReleasing"&&PinResourcesReleasingForTests is {} checkpoint)await checkpoint(checkpointToken).ConfigureAwait(false);
                return null;
            },cancel).ConfigureAwait(false);
            var stamp=NativeRuntimeArguments.Stamp(result);if(stamp.Usn!=0)RecordLocalWrite(entry,stamp);_localContentReady?.Invoke(entry);return true;
        }
        finally{gate.Release();NotifyPinWorkCompleted();}
    },token);
}
