using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HybridMount.Services.Mounting;
namespace HybridMount.ViewModels;

/// <summary>挂载页唯一状态源，按挂载拥有草稿、选择及操作防重入；释放不停止应用后台工作。</summary>
public sealed partial class MountsViewModel : ObservableObject,IDisposable
{
    private readonly MountManager _manager;
    private readonly IUiDispatcher _dispatcher;
    private readonly LoggerService? _logger;
    public ShellThumbnailCapability ThumbnailCapability {get;}
    public bool CanConfigureThumbnails=>ThumbnailCapability.Available;
    private readonly HashSet<Guid> _busyMounts=[],_cancelingConnections=[];
    private readonly Dictionary<Guid,DetectionDraft> _drafts=[];
    private readonly Dictionary<Guid,bool> _thumbnailDrafts=[];
    private readonly Dictionary<Guid,CapacityDraft> _capacityDrafts=[];
    private int _pending;
    private bool _disposed,_dialogBusy,_runtimeReconnecting;
    private Guid? _selected;
    /// <summary>本页可关闭提示；后台刷新不重新打开已关闭消息。</summary>
    public PageStatusMessage StatusMessage {get;}=new();
    public sealed record CapacityDraft(bool Automatic,string Text,string Unit);
    public sealed record DetectionDraft(bool Enabled,double Incremental,double Full);
    /// <summary>服务原文只作为原文展示，避免与自有提示模板同文时被翻译。</summary>
    public void ReportError(string text){if(!_disposed&&StatusMessage.Show(global::HybridMount.Services.Localization.ResourceMessage.Raw(text),PageMessageSeverity.Error))OnPropertyChanged(nameof(Status));}
    public string Status {get=>StatusMessage.Text;set{if(!_disposed&&StatusMessage.Show(value))OnPropertyChanged();}}
    public Guid? SelectedMountId {get=>_selected;set{if(!_disposed&&SetProperty(ref _selected,value)){OnPropertyChanged(nameof(Selected));NotifyStateChanged();}}}
    public MountSnapshot? Selected=>Mounts.FirstOrDefault(x=>x.Definition.Id==_selected);
    public MountSnapshot RequireSelected()=>Selected??throw new InvalidOperationException(global::HybridMount.Services.Localization.UiText.Text("Text_69E37BF742A4"));
    public bool DialogBusy=>_dialogBusy||_runtimeReconnecting;
    public bool HasRuntimeFailedMounts=>_manager.HasRuntimeFailedMounts;
    public bool CanReconnectRuntimeFailed=>!IsStopping&&!DialogBusy&&_busyMounts.Count==0&&_cancelingConnections.Count==0&&HasRuntimeFailedMounts;
    public IAsyncRelayCommand ReconnectRuntimeFailedCommand {get;}
    public bool IsStopping=>_disposed||_manager.IsStopping;
    public bool IsBusy(Guid id)=>_busyMounts.Contains(id);
    public bool IsCanceling(Guid id)=>_cancelingConnections.Contains(id);
    public MountActionState Actions=>MountActionState.From(Selected,_selected is {} id&&IsBusy(id),IsStopping);
    public event Action? Refreshing;
    public event Action? Refreshed;
    public event Action? StateChanged;
    public ObservableCollection<OrphanedMountCandidate> RecoveryRoots {get;}=[];
    public OrphanedMountCandidate? SelectedRecoveryRoot {get;set;}
    /// <summary>遗留列表与选择属于挂载页状态；应用服务只返回本地身份快照。</summary>
    public async Task DiscoverRecoveryRootsAsync(CancellationToken token)
    {
        var roots=await _manager.DiscoverOrphanedMountsAsync(token);if(IsStopping)return;
        SnapshotCollections.Reconcile(RecoveryRoots,roots,x=>x.LocalPath);SelectedRecoveryRoot=null;
    }
    public async Task AddRecoveryRootAsync(string path,CancellationToken token)
    {
        var root=await _manager.InspectOrphanedMountAsync(path,token);if(IsStopping)return;
        var previous=RecoveryRoots.FirstOrDefault(x=>x.LocalPath.Equals(root.LocalPath,StringComparison.OrdinalIgnoreCase));
        if(previous is {CanRemove:true}&&previous.RootIdentity==root.RootIdentity&&previous.SyncRootId==root.SyncRootId&&previous.OrphanedDirectory==root.OrphanedDirectory&&previous.ShellOnly==root.ShellOnly)
        {SelectedRecoveryRoot=previous;return;}
        if(previous is not null)RecoveryRoots.Remove(previous);
        RecoveryRoots.Add(root);SelectedRecoveryRoot=root;
    }
    public async Task<OrphanedMountResult> ClearRecoveryRootAsync(OrphanedMountCandidate root,CancellationToken token,Action<string>? progress=null)
    {
        var result=await _manager.ClearOrphanedMountAsync(root,token,progress);if(IsStopping)return result;
        Status=result.Completed?global::HybridMount.Services.Localization.UiText.Text("Text_8391DA809DD9",(root.ShellOnly?global::HybridMount.Services.Localization.UiText.Text("Text_096B758B03DC"):global::HybridMount.Services.Localization.UiText.Text("Text_8C440D44770E")),root.LocalPath):global::HybridMount.Services.Localization.UiText.Text("Text_8B421796FD67",string.Join("；",result.Errors));
        if(result.Completed){RecoveryRoots.Remove(root);SelectedRecoveryRoot=null;}return result;
    }
    /// <summary>固定多选交给应用；页面释放后不再更新消息或候选，成功项才从列表移除。</summary>
    public async Task<OrphanedMountBatchResult> ClearRecoveryRootsAsync(IReadOnlyList<OrphanedMountCandidate> roots,CancellationToken token,Action<OrphanedMountBatchProgress>? progress=null)
    {
        var result=await _manager.ClearOrphanedMountsAsync(roots,token,progress);
        if(IsStopping)return result;
        foreach(var item in result.Items.Where(x=>x.State==OrphanedMountItemState.Completed))
            if(RecoveryRoots.FirstOrDefault(x=>x.LocalPath.Equals(item.Root.LocalPath,StringComparison.OrdinalIgnoreCase)&&x.RootIdentity==item.Root.RootIdentity) is {} existing)RecoveryRoots.Remove(existing);
        Status=result.Summary;return result;
    }
    public ObservableCollection<MountSnapshot> Mounts {get;}=[];
    public ObservableCollection<MountListItem> Items {get;}=[];
    public MountsViewModel(MountManager manager,IUiDispatcher dispatcher,LoggerService? logger=null,ShellThumbnailCapability? thumbnailCapability=null)
    {ThumbnailCapability=thumbnailCapability??CloudShellState.ThumbnailCapability();_manager=manager;_dispatcher=dispatcher;_logger=logger;ReconnectRuntimeFailedCommand=new AsyncRelayCommand(ReconnectRuntimeFailedAsync,()=>CanReconnectRuntimeFailed);_manager.Changed+=Changed;Refresh();}
    private void Changed()
    {
        if(_disposed||Interlocked.Exchange(ref _pending,1)!=0)return;
        if(!_dispatcher.TryEnqueue(()=>{Interlocked.Exchange(ref _pending,0);if(!_disposed)Refresh();}))Interlocked.Exchange(ref _pending,0);
    }
    public void RefreshNow(){if(_disposed)return;_manager.RefreshDisplaySnapshot();Refresh();}
    private void Refresh()
    {
        if(_disposed)return;
        try{var snapshots=_manager.DisplayMounts;if(_manager.DisplaySnapshotError is {} error&&StatusMessage.Update(error,PageMessageSeverity.Error))OnPropertyChanged(nameof(Status));Refreshing?.Invoke();SnapshotCollections.Reconcile(Mounts,snapshots,x=>x.Definition.Id);RefreshItems(snapshots);OnPropertyChanged(nameof(Selected));OnPropertyChanged(nameof(HasRuntimeFailedMounts));NotifyReconnectState();Refreshed?.Invoke();}
        catch(Exception error){if(StatusMessage.Update(global::HybridMount.Services.Localization.ResourceMessage.Raw(error.Message),PageMessageSeverity.Error))OnPropertyChanged(nameof(Status));_manager.ReportDiagnosticException("挂载页面","挂载快照读取失败",error);}
    }
    private void RefreshItems(IReadOnlyList<MountSnapshot> snapshots)
    {
        var previous=Items.ToDictionary(x=>x.Id);var rows=snapshots.Select(snapshot=>previous.GetValueOrDefault(snapshot.Definition.Id)??new MountListItem(snapshot)).ToArray();
        foreach(var row in rows)row.Update(snapshots.First(x=>x.Definition.Id==row.Id),row.Id==_selected,IsBusy(row.Id),IsStopping,DialogBusy,IsCanceling(row.Id),CanConfigureThumbnails);
        SnapshotCollections.Reconcile(Items,rows,x=>x.Id);
    }
    private void NotifyStateChanged()
    {
        if(!_disposed)NotifyReconnectState();
        foreach(var row in Items)row.Update(row.Snapshot,row.Id==_selected,IsBusy(row.Id),IsStopping,DialogBusy,IsCanceling(row.Id),CanConfigureThumbnails);
        if(!_disposed)StateChanged?.Invoke();
    }
    private void NotifyReconnectState(){OnPropertyChanged(nameof(CanReconnectRuntimeFailed));ReconnectRuntimeFailedCommand.NotifyCanExecuteChanged();}
    public async Task ReconnectRuntimeFailedAsync()
    {
        if(!CanReconnectRuntimeFailed)return;_runtimeReconnecting=true;NotifyStateChanged();
        try{await _manager.ReconnectRuntimeFailedAsync();}
        catch(OperationCanceledException)when(_manager.IsStopping) { }
        catch(Exception error){Report(error);}
        finally{_runtimeReconnecting=false;if(!_disposed){Refresh();NotifyStateChanged();}}
    }
    public MountSnapshot? FindMount(Guid id)=>Mounts.FirstOrDefault(x=>x.Definition.Id==id);
    public Task RunForMountAsync(Guid id,Func<MountSnapshot,Task> work)=>FindMount(id) is {} mount?RunMountAsync(id,()=>work(mount)):Task.CompletedTask;
    /// <summary>配置表单只读取应用已保存的凭据与根编辑权限；不发起连接。</summary>
    public SourceCredentials ReadCredentials(Guid id)=>_manager.GetCredentials(id);
    public Task<bool> CanEditRemoteRootAsync(Guid id)=>IsStopping?Task.FromResult(false):_manager.CanEditRemoteRootAsync(id);
    /// <summary>只读探测继续由应用登记、取消和等待；页面关闭使用自己的等待令牌。</summary>
    public Task<ConnectionTestResult> TestConnectionAsync(FileSourceConfiguration settings,SourceCredentials credentials,CancellationToken token)
        =>_manager.TestConnectionAsync(settings,credentials,token);
    /// <summary>只读群晖探测保留应用所有权；表单取消不影响其他挂载。</summary>
    internal Task<Models.Mounting.Synology.SynologyProbeResult> TestSynologyConnectionAsync(FileSourceConfiguration settings,SourceCredentials credentials,string? oneTimePassword,CancellationToken token,
        Models.Mounting.Synology.SynologySessionTicket? previous=null)
        =>_manager.TestSynologyConnectionAsync(settings,credentials,oneTimePassword,token,previous);
    /// <summary>新建或保存连接，应用拥有网络工作；持久化成功但连接失败时返回可重试的配置，校验失败仍抛出。</summary>
    public async Task<MountConnectionSaveResult> SaveConnectionAsync(Guid? id,string name,string path,bool autoConnect,FileSourceConfiguration settings,SourceCredentials credentials,IReadOnlyList<FileExclusionRule>? exclusions=null,MountCreationOptions? initial=null)
    {
        if(IsStopping)throw new OperationCanceledException(global::HybridMount.Services.Localization.UiText.Text("Text_25EAE2E8931B"),new CancellationToken(true));
        var busyId=id??Guid.Empty;
        if(!_busyMounts.Add(busyId))throw new InvalidOperationException(global::HybridMount.Services.Localization.UiText.Text("Text_826BE380E194"));
        NotifyStateChanged();var before=_manager.TaskDefinitions.Keys.ToHashSet();
        try
        {
            MountDefinition saved;
            if(id is {} existing){await _manager.UpdateCredentialsAsync(existing,credentials,settings:settings);saved=_manager.TaskDefinitions[existing];}
            else saved=await _manager.AddAsync(name,path,autoConnect,source:settings,credentials:credentials,exclusions:exclusions,initial:initial);
            if(id is null)_exclusionDrafts.Remove((Guid.Empty,settings.Kind));
            return new(saved,global::HybridMount.Services.Localization.UiText.Text("Text_E987FAE4AEA4",saved.Name));
        }
        catch(OperationCanceledException)when(_manager.IsStopping){throw;}
        catch(Exception error)
        {
            var saved=_manager.TaskDefinitions.Values.FirstOrDefault(x=>id is {} existing?x.Id==existing:!before.Contains(x.Id)&&x.LocalPath.Equals(path,StringComparison.OrdinalIgnoreCase));
            if(saved is null||error is ArgumentException or NotSupportedException)throw;
            if(id is null)_exclusionDrafts.Remove((Guid.Empty,settings.Kind));
            Report(error);return new(saved,global::HybridMount.Services.Localization.UiText.Text("Text_83F7CCCF2032",error.Message),PageMessageSeverity.Error);
        }
        finally{_busyMounts.Remove(busyId);if(!_disposed){Refresh();NotifyStateChanged();}}
    }
    public DetectionDraft Draft(MountDefinition definition)=>_drafts.GetValueOrDefault(definition.Id)??new(definition.RemoteAutoDetectEnabled,definition.RemoteIncrementalSeconds,definition.RemoteFullSeconds);
    public void UpdateDraft(Guid id,bool enabled,double incremental,double full){if(!IsStopping)_drafts[id]=new(enabled,incremental,full);}
    public bool ThumbnailDraft(MountDefinition definition)=>_thumbnailDrafts.GetValueOrDefault(definition.Id,definition.ThumbnailsEnabled);
    /// <summary>只串行化本挂载；捕获的 ID 不随页面选择切换，迟到结果不能回写其他挂载。</summary>
    public async Task RunMountAsync(Guid id,Func<Task> work)
    {
        if(IsStopping||_runtimeReconnecting||!_busyMounts.Add(id))return;NotifyStateChanged();
        try{await work();}
        catch(OperationCanceledException)when(_manager.IsStopping) { }
        catch(MountManager.ConnectionStoppedException){Status=global::HybridMount.Services.Localization.UiText.Text("Text_36C0DC4550F0");}
        catch(Exception error){Report(error);}
        finally{_busyMounts.Remove(id);if(!_disposed){Refresh();NotifyStateChanged();}}
    }
    public Task RunSelectedAsync(Func<MountSnapshot,Task> work)=>Selected is {} selected?RunMountAsync(selected.Definition.Id,()=>work(selected)):Task.CompletedTask;
    /// <summary>所有对话框和选择器共享页面忙碌状态，实际控件由 Page 展示。</summary>
    public async Task RunDialogAsync(Func<Task> work)
    {
        if(IsStopping||DialogBusy)return;_dialogBusy=true;NotifyStateChanged();
        try{await work();}
        catch(OperationCanceledException)when(_manager.IsStopping) { }
        catch(MountManager.ConnectionStoppedException){Status=global::HybridMount.Services.Localization.UiText.Text("Text_36C0DC4550F0");}
        catch(Exception error){Report(error);}
        finally{_dialogBusy=false;if(!_disposed){Refresh();NotifyStateChanged();}}
    }
    public void PublishStatus(string text,PageMessageSeverity severity=PageMessageSeverity.Information){if(!_disposed&&StatusMessage.Show(text,severity))OnPropertyChanged(nameof(Status));}
    private void Report(Exception error){if(_disposed)return;PublishStatus(error.Message,PageMessageSeverity.Error);_manager.ReportDiagnosticException("挂载","挂载页面操作未完成",error);}
    public Task SaveSelectedDetectionAsync()=>SelectedMountId is {} id?SaveDetectionAsync(id):Task.CompletedTask;
    public Task SaveDetectionAsync(Guid mountId)
    {
        if(FindMount(mountId) is not {} selected||IsBusy(mountId))return Task.CompletedTask;
        var id=selected.Definition.Id;var draft=Draft(selected.Definition);
        return RunMountAsync(id,async()=>
        {
            try
            {
                if(!double.IsFinite(draft.Incremental)||!double.IsFinite(draft.Full)||draft.Incremental!=Math.Truncate(draft.Incremental)||draft.Full!=Math.Truncate(draft.Full))throw new ArgumentException(global::HybridMount.Services.Localization.UiText.Text("Text_BCC223CCC272"));
                await _manager.SetRemoteDetectionAsync(id,draft.Enabled,checked((int)draft.Incremental),checked((int)draft.Full));PublishStatus(global::HybridMount.Services.Localization.UiText.Text("Text_FF6DF7520EEE"),PageMessageSeverity.Success);
            }
            finally{_drafts.Remove(id);}
        });
    }
    /// <summary>快捷开关只保存开关值，保留本挂载尚未提交的间隔草稿；失败时真实状态不变。</summary>
    public Task SetRemoteDetectionEnabledAsync(Guid id,bool enabled)=>RunForMountAsync(id,async mount=>
    {
        var definition=mount.Definition;
        await _manager.SetRemoteDetectionAsync(id,enabled,definition.RemoteIncrementalSeconds,definition.RemoteFullSeconds);
        if(_drafts.TryGetValue(id,out var draft))_drafts[id]=draft with{Enabled=enabled};
        PublishStatus(enabled?global::HybridMount.Services.Localization.UiText.Text("Text_D8BEB94D8189"):global::HybridMount.Services.Localization.UiText.Text("Text_F075BC371E5A"),PageMessageSeverity.Success);
    });
    /// <summary>启动开关即时保存；RunMount统一报告故障并刷新正式快照，表单用正式值回滚。</summary>
    public Task SetAutoConnectAsync(Guid id,bool enabled)=>RunForMountAsync(id,async _=>{await _manager.SetAutoConnectAsync(id,enabled);PublishStatus(global::HybridMount.Services.Localization.UiText.Text("Text_28B3B98EB194"),PageMessageSeverity.Success);});
    /// <summary>仅切换容量来源，保留已保存上限和未提交的文本草稿，不把其他字段一起保存。</summary>
    public Task SetCapacityAutomaticAsync(Guid id,bool enabled)=>RunForMountAsync(id,async mount=>
    {
        await _manager.SetCapacityAsync(id,enabled,mount.Definition.MaximumCapacityBytes);
        if(_capacityDrafts.TryGetValue(id,out var draft))_capacityDrafts[id]=draft with{Automatic=enabled};
        PublishStatus(global::HybridMount.Services.Localization.UiText.Text("Text_462C57FFDF1A"),PageMessageSeverity.Success);
    });
    /// <summary>读取指定行当前完整诊断；不联网、不保存，也不依赖当前页面选择。</summary>
    /// <summary>完整诊断由应用后台只读生成，旧简要入口保持兼容。</summary>
    public Task<string> DiagnosticAsync(Guid id)=>_manager.ReadDiagnosticsAsync(id);
    public string Diagnostic(Guid id)=>FindMount(id)?.Detail is {Length:>0} detail?detail:global::HybridMount.Services.Localization.UiText.Text("Text_B00893BB99C2");
    public Task SetSelectedThumbnailAsync(bool enabled)=>SelectedMountId is {} id?SetThumbnailAsync(id,enabled):Task.CompletedTask;
    public Task SetThumbnailAsync(Guid mountId,bool enabled)
    {
        if(FindMount(mountId) is not {} selected||IsBusy(mountId))return Task.CompletedTask;
        var id=selected.Definition.Id;_thumbnailDrafts[id]=enabled;
        return RunMountAsync(id,async()=>{try{await _manager.SetThumbnailsEnabledAsync(id,enabled);PublishStatus(enabled?global::HybridMount.Services.Localization.UiText.Text("Text_8A4C0201DBCE"):global::HybridMount.Services.Localization.UiText.Text("Text_B019211CE0BB"),PageMessageSeverity.Success);}finally{_thumbnailDrafts.Remove(id);}});
    }
    /// <summary>SQS 折叠区只读取当前用户的独立凭据，不解析默认凭据链。</summary>
    public SourceCredentials ReadSqsCredentials(Guid id)=>_manager.GetSqsCredentials(id);
    public Task<ConnectionTestResult> TestSqsConnectionAsync(Guid id,SqsConfiguration options,SourceCredentials credentials,CancellationToken token)
        =>_manager.TestSqsConnectionAsync(id,options,credentials,token);
    /// <summary>只保存SQS启用值，队列参数及凭据仍由独立保存按钮提交；无有效已保存队列时启用失败并回滚。</summary>
    public Task SetSqsEnabledAsync(Guid id,bool enabled)=>RunForMountAsync(id,async mount=>
    {await _manager.SetSqsConfigurationAsync(id,(mount.Definition.Sqs??new()) with{Enabled=enabled});PublishStatus(global::HybridMount.Services.Localization.UiText.Text("Text_70543FF66115"),PageMessageSeverity.Success);});
    /// <summary>按固定挂载身份保存队列设置，失败保留原配置，应用拥有订阅停止和重启。</summary>
    public Task SaveSqsConfigurationAsync(Guid id,SqsConfiguration options,SourceCredentials credentials)=>RunForMountAsync(id,async _=>
    {await _manager.SetSqsConfigurationAsync(id,options,credentials);PublishStatus(global::HybridMount.Services.Localization.UiText.Text("Text_A145A59AE166"),PageMessageSeverity.Success);});
    /// <summary>字节值以十进制 GB 无损呈现；草稿按挂载隔离，列表刷新和选择切换不写回。</summary>
    public CapacityDraft CapacitySettingsDraft(MountDefinition definition)=>_capacityDrafts.GetValueOrDefault(definition.Id)??new(definition.CapacityAutoDetectEnabled,
        definition.MaximumCapacityBytes is {} bytes?((decimal)bytes/1_000_000_000m).ToString(System.Globalization.CultureInfo.CurrentCulture):"","GB");
    public void UpdateCapacityDraft(Guid id,bool automatic,string text,string unit){if(!IsStopping)_capacityDrafts[id]=new(automatic,text,unit);}
    public Task SaveSelectedCapacityAsync()=>SelectedMountId is {} id?SaveCapacityAsync(id):Task.CompletedTask;
    public Task SaveCapacityAsync(Guid mountId)
    {
        if(FindMount(mountId) is not {} selected||IsBusy(mountId))return Task.CompletedTask;
        var id=selected.Definition.Id;var draft=CapacitySettingsDraft(selected.Definition);
        return RunMountAsync(id,async()=>
        {
            try{var bytes=CapacityRules.ParseMaximum(draft.Text,draft.Unit);await _manager.SetCapacityAsync(id,draft.Automatic,bytes);PublishStatus(global::HybridMount.Services.Localization.UiText.Text("Text_2825482F8878"),PageMessageSeverity.Success);}
            finally{_capacityDrafts.Remove(id);}
        });
    }
    public Task RecheckSelectedCapacityAsync()=>SelectedMountId is {} id?RecheckCapacityAsync(id):Task.CompletedTask;
    public Task RecheckCapacityAsync(Guid id)=>RunForMountAsync(id,async mount=>
    {
        await _manager.RefreshCapacityAsync(mount.Definition.Id,true);
        var error=_manager.DisplayMounts.FirstOrDefault(x=>x.Definition.Id==mount.Definition.Id)?.Capacity?.Error;PublishStatus(error??global::HybridMount.Services.Localization.UiText.Text("Text_616726CC744E"),error is null?PageMessageSeverity.Success:PageMessageSeverity.Warning);
    });
    public async Task ReadCapacityAsync(Guid? id)
    {
        if(id is null||IsStopping)return;
        try{await _manager.RefreshCapacityAsync(id.Value);}
        catch(OperationCanceledException)when(_manager.IsStopping) { }
        catch(IOException error){_manager.ReportDiagnosticException("配额查询","容量查询未完成",error);}
        catch(Exception error){if(!_disposed)_logger?.Queue("配额查询","Warning",error.GetType().Name);}
    }
    public Task ConnectAsync()=>RunSelectedAsync(mount=>_manager.ConnectAsync(mount.Definition.Id));
    public Task DisconnectAsync()=>RunSelectedAsync(mount=>_manager.DisconnectAsync(mount.Definition.Id));
    public Task CancelConnectionAsync()=>SelectedMountId is {} id?CancelConnectionAsync(id):Task.CompletedTask;
    public async Task CancelConnectionAsync(Guid mountId)
    {
        if(IsStopping||FindMount(mountId) is not {} selected||!_cancelingConnections.Add(mountId))return;
        var id=selected.Definition.Id;NotifyStateChanged();
        try{await _manager.DisconnectAsync(id);Status=global::HybridMount.Services.Localization.UiText.Text("Text_B364AFEE9249");}
        catch(OperationCanceledException)when(_manager.IsStopping) { }
        catch(Exception error){Report(error);}
        finally{_cancelingConnections.Remove(id);if(!_disposed){Refresh();NotifyStateChanged();}}
    }
    public Task ScanChangesAsync()=>SelectedMountId is {} id?ScanChangesAsync(id):Task.CompletedTask;
    public Task ScanChangesAsync(Guid id)=>RunForMountAsync(id,async mount=>{await _manager.ScanUploadChangesAsync(mount.Definition.Id);Status=global::HybridMount.Services.Localization.UiText.Text("Text_42B7F8CC659C");});
    public void QueueSelected(string operation){if(SelectedMountId is {} id)QueueMount(id,operation);}
    public void QueueMount(Guid mountId,string operation)
    {
        if(IsStopping||FindMount(mountId) is not {} selected||IsBusy(mountId))return;
        try{_manager.Queue(selected.Definition.Id,operation);Status=global::HybridMount.Services.Localization.UiText.Text("Text_52E0B9B9F464");}
        catch(Exception error){Report(error);}
    }
    /// <summary>行按钮捕获挂载ID，连接中的取消不受连接任务自身忙碌标志阻挡。</summary>
    public Task ChangeConnectionAsync(Guid id)
    {
        if(FindMount(id) is not {} mount)return Task.CompletedTask;
        if(mount.IsConnecting)return CancelConnectionAsync(id);
        return RunMountAsync(id,()=>mount.IsConnected?_manager.DisconnectAsync(id):_manager.ConnectAsync(id));
    }
    /// <summary>确认对话框返回捕获的挂载身份；应用服务重验移除阶段及原模式。</summary>
    public void QueueRemoval(Guid id,bool clear)
    {
        if(IsStopping)return;
        try{_manager.Queue(id,clear?"clearAndRemove":"remove");Status=global::HybridMount.Services.Localization.UiText.Text("Text_273A955713B8");}
        catch(Exception error){Report(error);}
    }
    public void Dispose(){if(_disposed)return;_disposed=true;StatusMessage.Dispose();_manager.Changed-=Changed;_exclusionDrafts.Clear();}
}

/// <summary>配置保存的实际结果；连接失败不冒充未保存，也不自动重试写入。</summary>
public sealed record MountConnectionSaveResult(MountDefinition Mount,string Message,PageMessageSeverity Severity=PageMessageSeverity.Success);
