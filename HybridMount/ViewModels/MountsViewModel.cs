using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Services.Mounting;
namespace HybridMount.ViewModels;

/// <summary>挂载页唯一状态源，按挂载拥有草稿、选择及操作防重入；释放不停止应用后台工作。</summary>
public sealed class MountsViewModel : ObservableObject,IDisposable
{
    private readonly MountManager _manager;
    private readonly IUiDispatcher _dispatcher;
    private readonly LoggerService? _logger;
    private readonly HashSet<Guid> _busyMounts=[],_cancelingConnections=[];
    private readonly Dictionary<Guid,DetectionDraft> _drafts=[];
    private readonly Dictionary<Guid,bool> _thumbnailDrafts=[];
    private readonly Dictionary<Guid,CapacityDraft> _capacityDrafts=[];
    private int _pending;
    private bool _disposed,_dialogBusy;
    private Guid? _selected;
    private string _status="本地：实心绿勾；仅远端：云；待脱水：空心绿勾，表示允许回收。自动上传仅提交新增和修改；本地删除不删除远端。";
    public sealed record CapacityDraft(bool Automatic,string Text,string Unit);
    public sealed record DetectionDraft(bool Enabled,double Incremental,double Full);
    public string Status {get=>_status;set{if(!_disposed)SetProperty(ref _status,value);}}
    public Guid? SelectedMountId {get=>_selected;set{if(!_disposed&&SetProperty(ref _selected,value)){OnPropertyChanged(nameof(Selected));StateChanged?.Invoke();}}}
    public MountSnapshot? Selected=>Mounts.FirstOrDefault(x=>x.Definition.Id==_selected);
    public MountSnapshot RequireSelected()=>Selected??throw new InvalidOperationException("请先选择一个挂载。");
    public bool DialogBusy=>_dialogBusy;
    public bool IsStopping=>_disposed||_manager.IsStopping;
    public bool IsBusy(Guid id)=>_busyMounts.Contains(id);
    public bool IsCanceling(Guid id)=>_cancelingConnections.Contains(id);
    public MountActionState Actions=>MountActionState.From(Selected,_selected is {} id&&IsBusy(id),IsStopping);
    public event Action? Refreshing;
    public event Action? Refreshed;
    public event Action? StateChanged;
    public ObservableCollection<MountSnapshot> Mounts {get;}=[];
    public MountsViewModel(MountManager manager,IUiDispatcher dispatcher,LoggerService? logger=null)
    {_manager=manager;_dispatcher=dispatcher;_logger=logger;_manager.Changed+=Changed;Refresh();}
    private void Changed()
    {
        if(_disposed||Interlocked.Exchange(ref _pending,1)!=0)return;
        if(!_dispatcher.TryEnqueue(()=>{Interlocked.Exchange(ref _pending,0);if(!_disposed)Refresh();}))Interlocked.Exchange(ref _pending,0);
    }
    public void RefreshNow()=>Refresh();
    private void Refresh()
    {
        if(_disposed)return;
        try{var snapshots=_manager.Mounts;Refreshing?.Invoke();SnapshotCollections.Reconcile(Mounts,snapshots,x=>x.Definition.Id);OnPropertyChanged(nameof(Selected));Refreshed?.Invoke();}
        catch(Exception error){Status=error.Message;}
    }
    public DetectionDraft Draft(MountDefinition definition)=>_drafts.GetValueOrDefault(definition.Id)??new(definition.RemoteAutoDetectEnabled,definition.RemoteIncrementalSeconds,definition.RemoteFullSeconds);
    public void UpdateDraft(Guid id,bool enabled,double incremental,double full){if(!IsStopping)_drafts[id]=new(enabled,incremental,full);}
    public bool ThumbnailDraft(MountDefinition definition)=>_thumbnailDrafts.GetValueOrDefault(definition.Id,definition.ThumbnailsEnabled);
    /// <summary>只串行化本挂载；捕获的 ID 不随页面选择切换，迟到结果不能回写其他挂载。</summary>
    public async Task RunMountAsync(Guid id,Func<Task> work)
    {
        if(IsStopping||!_busyMounts.Add(id))return;StateChanged?.Invoke();
        try{await work();}
        catch(OperationCanceledException)when(_manager.IsStopping) { }
        catch(MountManager.ConnectionStoppedException){Status="连接已停止。";}
        catch(Exception error){Report(error);}
        finally{_busyMounts.Remove(id);if(!_disposed){Refresh();StateChanged?.Invoke();}}
    }
    public Task RunSelectedAsync(Func<MountSnapshot,Task> work)=>Selected is {} selected?RunMountAsync(selected.Definition.Id,()=>work(selected)):Task.CompletedTask;
    /// <summary>所有对话框和选择器共享页面忙碌状态，实际控件由 Page 展示。</summary>
    public async Task RunDialogAsync(Func<Task> work)
    {
        if(IsStopping||_dialogBusy)return;_dialogBusy=true;StateChanged?.Invoke();
        try{await work();}
        catch(OperationCanceledException)when(_manager.IsStopping) { }
        catch(MountManager.ConnectionStoppedException){Status="连接已停止。";}
        catch(Exception error){Report(error);}
        finally{_dialogBusy=false;if(!_disposed){Refresh();StateChanged?.Invoke();}}
    }
    private void Report(Exception error){if(_disposed)return;Status=error.Message;_logger?.Queue("挂载","Error",error.GetType().Name);}
    public Task SaveSelectedDetectionAsync()
    {
        if(Selected is not {} selected||IsBusy(selected.Definition.Id))return Task.CompletedTask;
        var id=selected.Definition.Id;var draft=Draft(selected.Definition);
        return RunMountAsync(id,async()=>
        {
            try
            {
                if(!double.IsFinite(draft.Incremental)||!double.IsFinite(draft.Full)||draft.Incremental!=Math.Truncate(draft.Incremental)||draft.Full!=Math.Truncate(draft.Full))throw new ArgumentException("检测间隔必须为有效的整数秒。");
                await _manager.SetRemoteDetectionAsync(id,draft.Enabled,checked((int)draft.Incremental),checked((int)draft.Full));Status="检测设置已保存。";
            }
            finally{_drafts.Remove(id);}
        });
    }
    public Task SetSelectedThumbnailAsync(bool enabled)
    {
        if(Selected is not {} selected||IsBusy(selected.Definition.Id))return Task.CompletedTask;
        var id=selected.Definition.Id;_thumbnailDrafts[id]=enabled;
        return RunMountAsync(id,async()=>{try{await _manager.SetThumbnailsEnabledAsync(id,enabled);Status=enabled?"缩略图已开启，无法高效读取时显示普通图标。":"缩略图已关闭，后台预览已停止。";}finally{_thumbnailDrafts.Remove(id);}});
    }
    /// <summary>字节值以十进制 GB 无损呈现；草稿按挂载隔离，列表刷新和选择切换不写回。</summary>
    public CapacityDraft CapacitySettingsDraft(MountDefinition definition)=>_capacityDrafts.GetValueOrDefault(definition.Id)??new(definition.CapacityAutoDetectEnabled,
        definition.MaximumCapacityBytes is {} bytes?((decimal)bytes/1_000_000_000m).ToString(System.Globalization.CultureInfo.CurrentCulture):"","GB");
    public void UpdateCapacityDraft(Guid id,bool automatic,string text,string unit){if(!IsStopping)_capacityDrafts[id]=new(automatic,text,unit);}
    public Task SaveSelectedCapacityAsync()
    {
        if(Selected is not {} selected||IsBusy(selected.Definition.Id))return Task.CompletedTask;
        var id=selected.Definition.Id;var draft=CapacitySettingsDraft(selected.Definition);
        return RunMountAsync(id,async()=>
        {
            try{var bytes=CapacityRules.ParseMaximum(draft.Text,draft.Unit);await _manager.SetCapacityAsync(id,draft.Automatic,bytes);Status="容量设置已保存；上传前将按当前设置检查。";}
            finally{_capacityDrafts.Remove(id);}
        });
    }
    public Task RecheckSelectedCapacityAsync()=>RunSelectedAsync(async mount=>
    {
        await _manager.RefreshCapacityAsync(mount.Definition.Id,true);
        Status=_manager.Mounts.FirstOrDefault(x=>x.Definition.Id==mount.Definition.Id)?.Capacity?.Error??"容量核实完成；等待任务将重新检查可用空间。";
    });
    public async Task ReadCapacityAsync(Guid? id)
    {
        if(id is null||IsStopping)return;
        try{await _manager.RefreshCapacityAsync(id.Value);}
        catch(OperationCanceledException)when(_manager.IsStopping) { }
        catch(IOException) { }
        catch(Exception error){if(!_disposed)_logger?.Queue("配额查询","Warning",error.GetType().Name);}
    }
    public Task ConnectAsync()=>RunSelectedAsync(mount=>_manager.ConnectAsync(mount.Definition.Id));
    public Task DisconnectAsync()=>RunSelectedAsync(mount=>_manager.DisconnectAsync(mount.Definition.Id));
    public async Task CancelConnectionAsync()
    {
        if(IsStopping||Selected is not {} selected||!_cancelingConnections.Add(selected.Definition.Id))return;
        var id=selected.Definition.Id;StateChanged?.Invoke();
        try{await _manager.DisconnectAsync(id);Status="连接已取消。";}
        catch(OperationCanceledException)when(_manager.IsStopping) { }
        catch(Exception error){Report(error);}
        finally{_cancelingConnections.Remove(id);if(!_disposed){Refresh();StateChanged?.Invoke();}}
    }
    public Task ScanChangesAsync()=>RunSelectedAsync(async mount=>{await _manager.ScanUploadChangesAsync(mount.Definition.Id);Status="本地扫描完成；新增和修改持续上传，冲突可在任务页查看。";});
    public void QueueSelected(string operation)
    {
        if(IsStopping||Selected is not {} selected||IsBusy(selected.Definition.Id))return;
        try{_manager.Queue(selected.Definition.Id,operation);Status="操作已提交，可在任务页查看进度和逐项结果。";}
        catch(Exception error){Report(error);}
    }
    /// <summary>确认对话框返回捕获的挂载身份；应用服务重验移除阶段及原模式。</summary>
    public void QueueRemoval(Guid id,bool clear)
    {
        if(IsStopping)return;
        try{_manager.Queue(id,clear?"clearAndRemove":"remove");Status="移除任务已提交，可在任务页查看进度和逐项结果。";}
        catch(Exception error){Report(error);}
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_manager.Changed-=Changed;}
}
