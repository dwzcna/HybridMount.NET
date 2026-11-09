using System.Collections.Concurrent;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.Windows;

public sealed partial class CloudMountSession
{
    private sealed record StagedContent(string Path,SourceEntry Metadata);
    private readonly ConcurrentDictionary<string,StagedContent> _remoteSnapshots=new();
    private readonly HashSet<string> _metadataUpdating=[];
    /// <summary>原生版本切换前原子拒绝迟到的旧版回调；已有读取继续使本轮延后，不取消其他调用者。</summary>
    private bool BeginMetadataUpdate(string id)
    {
        lock(_fetchGate){if(_clearing||_stop.IsCancellationRequested||IsActive(id)||!_metadataUpdating.Add(id))return false;}
        try{Runtime.Invoke("blockEntry",SessionArgs(new{entryId=id,blocked=true}));lock(_fetchGate){if(!_clearing&&!_stop.IsCancellationRequested&&!IsActive(id))return true;}EndMetadataUpdate(id);return false;}
        catch{lock(_fetchGate)_metadataUpdating.Remove(id);throw;}
    }
    private void EndMetadataUpdate(string id){try{if(Runtime.IsAvailable)Runtime.Invoke("blockEntry",SessionArgs(new{entryId=id,blocked=false}));}finally{lock(_fetchGate)_metadataUpdating.Remove(id);}}
    /// <summary>保护引用取得之后切换水合来源；首次切换必须没有旧版本回调；已持久切换后的恢复允许新版本水合等待者领取同一快照。</summary>
    internal void InstallRemoteSnapshot(MountEntry entry,RemotePendingChange change)
    {
        lock(_fetchGate)
        {
            if(_clearing||_stop.IsCancellationRequested||change.State!="Hydrating"&&(IsActive(entry.Id)||_fetches.Values.Any(x=>x.EntryId==entry.Id)))throw new IOException("文件仍在使用，远端更新延后。");
            _remoteSnapshots[entry.Id]=new(change.SnapshotPath!,change.Entry!);
        }
    }
    internal void RemoveRemoteSnapshot(string id)=>_remoteSnapshots.TryRemove(id,out _);
}
