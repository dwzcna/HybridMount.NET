using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.Windows;

public sealed partial class CloudMountSession
{
    private readonly SharedAsyncWork<string,bool> _pinnedHydration=new();
    private readonly ConcurrentDictionary<string,byte> _cancelingPins=new();
    /// <summary>资源真正归还后仅唤醒固定维护，不进入上传扫描；停止时由消费者解除订阅。</summary>
    internal event Action<string>? PinWorkCompleted;
    internal Func<CancellationToken,Task>? PinResourcesReleasingForTests {get;set;}
    private void NotifyPinWorkCompleted(string entryId)
    {
        if(_stop.IsCancellationRequested||PinWorkCompleted is null)return;
        var entry=_repository.EntryById(_mount.Id,entryId);
        if(entry is not null)NotifyPinWorkCompleted(entry);
    }
    /// <summary>同一次关闭的固定及内容通知复用精确查询结果；订阅者仅在每挂载消费者执行。</summary>
    private void NotifyPinWorkCompleted(MountEntry entry)
    {
        if(_stop.IsCancellationRequested||PinWorkCompleted is not {} handlers)return;
        foreach(Action<string> handler in handlers.GetInvocationList())
            try{handler(entry.LocalPath);}
            catch(Exception error){_logger.Queue("固定意图","Warning",error.GetType().Name);}
    }
    private readonly ConcurrentDictionary<string,SemaphoreSlim> _pinGates=new();
    /// <summary>只查属性和本地范围；调用者持根锁且已排除上传及远端屏障，不读取正文或改变固定意图。</summary>
    internal (bool Pinned,bool NeedsDownload) ReadPinIntent(MountEntry entry)
    {
        if(_cancelingPins.ContainsKey(entry.Id))return(false,false);
        var path=RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(_mount,path);
        var snapshot=Runtime.InspectAsync(path,ranges:true).GetAwaiter().GetResult();var info=snapshot.Placeholder;var marker=info is null?null:MountPathGuard.ReadEntry(info.Identity);
        if(marker?.MountId!=_mount.Id||marker.EntryId!=entry.Id||info!.PinState!=1||info.InSyncState!=1||info.ModifiedSize!=0)return(false,false);
        return(true,(snapshot.State&0x10)!=0||!snapshot.Full);
    }
    /// <summary>只接纳同条目未含摘要的冷版本被本会话首读绑定后的升级；两个已验证版本仍须严格相等。</summary>
    internal bool SamePinVersion(MountEntry expected,MountEntry current)
        =>expected.Id==current.Id&&expected.SourcePath==current.SourcePath&&expected.LocalPath==current.LocalPath&&expected.IsDirectory==current.IsDirectory&&
            (expected.Version==current.Version||_source is Models.Mounting.Synology.SynologyFileSource source&&!expected.Version.Contains(";md5:",StringComparison.Ordinal)&&current.Version.Contains(";md5:",StringComparison.Ordinal)&&source.IsSameVersionHint(expected.Version,current.Version));
    /// <summary>显式固定只由原生属性操作修改已核验条目。</summary>
    internal void RequestPin(MountEntry entry)
    {if(_cancelingPins.ContainsKey(entry.Id))throw new IOException("固定下载正在取消，请等待收尾后重新固定。");Runtime.Invoke("requestPin",EntryArgs(entry));}
    /// <summary>仅撤销固定拥有的水合和FETCH；等待句柄释放后清除单文件固定，不触发脱水或取消外部读取。</summary>
    internal async Task CancelPinnedAsync(MountEntry entry,string fileId,CancellationToken token)
    {
        if(!_cancelingPins.TryAdd(entry.Id,0))throw new IOException("固定取消已在处理。");
        try
        {
            var owned=_fetches.Values.Where(fetch=>fetch.EntryId==entry.Id&&fetch.Owned).ToArray();
            foreach(var fetch in owned)try{await fetch.Cancel.CancelAsync().ConfigureAwait(false);}catch(ObjectDisposedException){}
            await _pinnedHydration.CancelAndDrainAsync(entry.Id).ConfigureAwait(false);
            await Task.WhenAll(owned.Select(fetch=>fetch.Work)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();var current=_repository.EntryById(_mount.Id,entry.Id);
            if(_clearing||current is null||!SamePinVersion(entry,current))throw new IOException("取消固定前文件身份或内容版本已变化。");
            await Runtime.InvokeAsync("cancelPin",EntryArgs(current,new{fileId}),token:token).ConfigureAwait(false);
        }
        finally{_cancelingPins.TryRemove(entry.Id,out _);NotifyPinWorkCompleted(entry.Id);}
    }
    /// <summary>原生固定与显式下载共享同条目的水合；每位调用者独立取消，工作自己拥有句柄和停止令牌，不重新设置Pin或InSync。</summary>
    internal Task HydratePinnedAsync(MountEntry entry,CancellationToken token)=>_pinnedHydration.RunAsync(entry.Id,async shared=>
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(shared,_stop.Token);
        var cancel=stop.Token;var gate=_pinGates.GetOrAdd(entry.Id,_=>new(1,1));await gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            cancel.ThrowIfCancellationRequested();if(_cancelingPins.ContainsKey(entry.Id))throw new OperationCanceledException(cancel);using var priority=PrioritizeDownload(entry.Id);
            var result=await Runtime.InvokeAsync("hydratePinned",EntryArgs(entry),async(stage,_,checkpointToken)=>
            {
                checkpointToken.ThrowIfCancellationRequested();
                if(stage=="pinnedGuarded")
                {
                    var current=_repository.EntryById(_mount.Id,entry.Id);
                    if(_clearing||current is null||current.Deleted||current.Protected||!SamePinVersion(entry,current)||current.LocalPath!=entry.LocalPath||current.RemotePresence==RemotePresence.Deleted||_repository.HasPendingRemote(_mount.Id,entry.SourcePath)||_repository.Uploads(_mount.Id).Any(x=>x.LocalPath==entry.LocalPath&&x.State!="Completed"))throw new IOException("固定下载遇到本地修改、未决工作或身份变化，已保留内容。");
                    entry=current;
                }
                else if(stage=="pinReleasing"&&PinResourcesReleasingForTests is {} checkpoint)await checkpoint(checkpointToken).ConfigureAwait(false);
                return null;
            },cancel).ConfigureAwait(false);
            if(_repository.EntryById(_mount.Id,entry.Id) is {} latest&&SamePinVersion(entry,latest))entry=latest;
            var stamp=NativeRuntimeArguments.Stamp(result);if(stamp.Usn!=0)RecordLocalWrite(entry,stamp);_localContentReady?.Invoke(entry);return true;
        }
        finally{gate.Release();NotifyPinWorkCompleted(entry.Id);}
    },token);
}
