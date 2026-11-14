using System.Collections.ObjectModel;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly object _displayGate=new();
    private readonly SemaphoreSlim _displayWake=new(0,1);
    private readonly Task _displayWorker;
    private long _displayRevision;
    private IReadOnlyList<MountSnapshot> _displayMounts=Array.Empty<MountSnapshot>();
    private IReadOnlyDictionary<Guid,int> _displayIssues=new ReadOnlyDictionary<Guid,int>(new Dictionary<Guid,int>());
    private string? _displayError;
    /// <summary>界面只读不可变内存快照；不持仓库锁、不查询SQLite、不调用CFAPI。</summary>
    internal IReadOnlyList<MountSnapshot> DisplayMounts=>Volatile.Read(ref _displayMounts);
    internal string? DisplaySnapshotError=>Volatile.Read(ref _displayError);
    /// <summary>纯内存字段只用于展示，权限与操作仍由业务执行前重新核验。</summary>
    private MountSnapshot MemorySnapshot(Runtime root,int issues)=>new(root.Definition,root.Status,root.Detail,root.Detection,root.Session is not null,
        root.Connecting is not null,root.ConnectionStops>0,_jobs.Values.FirstOrDefault(job=>job.Root==root.Definition.Id&&job.Snapshot.State is "Queued" or "Running")?.Snapshot.Operation,root.CapacityStatus,issues,ReadProtocolCapabilities(root));
    private IReadOnlyList<MountSnapshot> CaptureDisplay(IReadOnlyDictionary<Guid,int> counts)=>Array.AsReadOnly(_roots.Values.Select(root=>MemorySnapshot(root,counts.GetValueOrDefault(root.Definition.Id))).OrderBy(row=>row.Definition.Name).ThenBy(row=>row.Definition.Id).ToArray());
    /// <summary>进度通知仅发布轻量状态并合并一次后台查询；代次屏障禁止旧查询覆盖新定义或退出状态。</summary>
    internal void RefreshDisplaySnapshot()
    {
        if(IsStopping)return;long revision;IReadOnlyDictionary<Guid,int> counts;
        lock(_displayGate){revision=++_displayRevision;counts=_displayIssues;}
        var snapshot=CaptureDisplay(counts);
        lock(_displayGate)
        {
            if(IsStopping)return;if(revision==_displayRevision)Volatile.Write(ref _displayMounts,snapshot);
            if(_displayWake.CurrentCount==0)_displayWake.Release();
        }
    }
    /// <summary>专用后台拥有者每轮只读一次上传及改名集合；50ms合并变化，失败保留旧快照与可诊断错误。</summary>
    private async Task UpdateDisplaySnapshotsAsync()
    {
        try
        {
            while(true)
            {
                await _displayWake.WaitAsync(_stop.Token).ConfigureAwait(false);await Task.Delay(50,_stop.Token).ConfigureAwait(false);
                long revision;lock(_displayGate)revision=_displayRevision;
                try
                {
                    var counts=_repository.UploadAttentionCounts();
                    foreach(var group in RenameIntents().Where(item=>item.NeedsAttention).GroupBy(item=>item.MountId))counts[group.Key]=counts.GetValueOrDefault(group.Key)+group.Count();
                    foreach(var id in _renameErrors.Keys)counts[id]=counts.GetValueOrDefault(id)+1;
                    _stop.Token.ThrowIfCancellationRequested();var immutable=new ReadOnlyDictionary<Guid,int>(counts);var snapshot=CaptureDisplay(immutable);
                    bool changed;
                    lock(_displayGate)
                    {
                        if(IsStopping||revision!=_displayRevision)continue;
                        changed=_displayError is not null||!_displayMounts.SequenceEqual(snapshot);
                        _displayIssues=immutable;Volatile.Write(ref _displayMounts,snapshot);Volatile.Write(ref _displayError,null);
                    }
                    if(changed)NotifyChanged();
                }
                catch(OperationCanceledException)when(_stop.IsCancellationRequested){throw;}
                catch(Exception error)
                {
                    var message=$"挂载统计读取失败（{error.GetType().Name}，0x{error.HResult:X8}），保留上次显示。";
                    lock(_displayGate){if(IsStopping||revision!=_displayRevision)continue;Volatile.Write(ref _displayError,message);}
                    _logger.QueueException("挂载显示","Warning","后台快照读取失败",error);NotifyChanged();
                }
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
}
