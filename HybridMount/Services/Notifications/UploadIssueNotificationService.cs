using HybridMount.Models.Mounting;
namespace HybridMount.Services.Notifications;

/// <summary>按挂载合并新发生的问题，只生成应用内提示。启动已有问题只计数，不重新提示；关闭提示不解除问题。</summary>
public sealed class UploadIssueNotificationService : IAsyncDisposable
{
    private readonly MountManager _manager;
    private readonly object _gate=new();
    private readonly HashSet<string> _seen=[];
    private readonly HashSet<Guid> _pending=[];
    private readonly Dictionary<Guid,string> _renameWarnings=[];
    private readonly CancellationTokenSource _stop=new();
    private Task _work=Task.CompletedTask;
    private bool _scheduled,_disposed;
    private string _noticeFingerprint="";
    public event Action? Changed;
    public UploadIssueNotificationService(MountManager manager)
    {
        _manager=manager;foreach(var item in manager.UploadIssues)_seen.Add(Key(item));manager.UploadIssuesChanged+=OnIssuesChanged;manager.RenameRejected+=OnRenameRejected;
    }
    private static string Key(MountOperationSnapshot item)=>item.Id+":"+item.Attention!.NotificationKey;
    public IReadOnlyList<UploadIssueNotice> Pending
    {
        get
        {
            Guid[] pending;KeyValuePair<Guid,string>[] warnings;lock(_gate){pending=_pending.ToArray();warnings=_renameWarnings.ToArray();}
            var mounts=_manager.Mounts.ToDictionary(x=>x.Definition.Id,x=>x.Definition.Name);
            var result=_manager.UploadIssues.Where(x=>x.MountId is {} id&&pending.Contains(id)).GroupBy(x=>x.MountId!.Value)
                .ToDictionary(x=>x.Key,x=>new UploadIssueNotice(x.Key,mounts.GetValueOrDefault(x.Key,"挂载"),x.Count()));
            foreach(var warning in warnings)if(mounts.TryGetValue(warning.Key,out var name))result[warning.Key]=result.TryGetValue(warning.Key,out var notice)?notice with{Message=warning.Value}:new(warning.Key,name,0,warning.Value);
            return result.Values.ToArray();
        }
    }
    /// <summary>只解除合并提示；seen保持本会话去重，问题仍留在上传记录和Explorer。</summary>
    public void Dismiss(Guid mount){lock(_gate){if(_disposed)return;_pending.Remove(mount);_renameWarnings.Remove(mount);_noticeFingerprint="";}Changed?.Invoke();}
    private void OnRenameRejected(Guid mount,string message)
    {
        lock(_gate)
        {
            if(_disposed||_renameWarnings.GetValueOrDefault(mount)==message)return;
            _renameWarnings[mount]=message;
            if(_scheduled)return;_scheduled=true;_work=Task.Run(PublishAsync);
        }
    }
    private void OnIssuesChanged()
    {
        var issues=_manager.UploadIssues;
        lock(_gate)
        {
            if(_disposed)return;
            foreach(var item in issues)if(_seen.Add(Key(item))&&item.MountId is {} mount)_pending.Add(mount);
            _pending.RemoveWhere(id=>!issues.Any(x=>x.MountId==id));
            var fingerprint=string.Join(";",_pending.Order().Select(id=>id+":"+issues.Count(x=>x.MountId==id)));
            if(fingerprint==_noticeFingerprint)return;_noticeFingerprint=fingerprint;
            if(_scheduled)return;_scheduled=true;_work=Task.Run(PublishAsync);
        }
    }
    private async Task PublishAsync()
    {
        try{await Task.Delay(600,_stop.Token).ConfigureAwait(false);lock(_gate){_scheduled=false;if(_disposed)return;}Changed?.Invoke();}
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    public async ValueTask DisposeAsync()
    {
        Task work;lock(_gate){if(_disposed)return;_disposed=true;_manager.UploadIssuesChanged-=OnIssuesChanged;_manager.RenameRejected-=OnRenameRejected;_stop.Cancel();work=_work;}
        await work.ConfigureAwait(false);_stop.Dispose();
    }
}
/// <summary>应用内按挂载合并后的只读提示，不含凭据或响应原文。</summary>
public sealed record UploadIssueNotice(Guid MountId,string MountName,int Count,string? Message=null);
