using System.Collections.Concurrent;
using HybridMount.Models.Mounting;
using HybridMount.Commons;

namespace HybridMount.Services.Mounting;

/// <summary>应用范围的挂载协调器；每个根串行执行变更，文件内容通过 CFAPI 回调独立传输。</summary>
public sealed partial class MountManager : IAsyncDisposable
{
    private sealed class Runtime(MountDefinition definition)
    {
        public MountDefinition Definition=definition;
        public readonly SemaphoreSlim Gate=new(1,1);
        public readonly object ConnectionSync=new();
        public CancellationTokenSource? Connecting;
        public Task ConnectionSignal=Task.CompletedTask;
        public Task ConnectionFinished=Task.CompletedTask;
        public bool RequiresInspection;
        public Task? Inspection;
        public int ConnectionStops;
        public string? ConnectionStopReason;
        public bool RuntimeFailed;
        public Task RuntimeFailureCleanup=Task.CompletedTask;
        public bool RemovalRequested;
        public int RemovalRequests;
        public CloudMountSession? Session;
        public IFileSource? Source;
        public MountQueueBinding? QueueBinding;
        public UploadRuntime? Uploads;
        public readonly ConflictMetadataChanges ConflictMetadata=new();
        public FileExclusionMarkerStore? ExclusionMarkers;
        public string? ExclusionError;
        public PinIntentRuntime? Pins;
        public bool PinIntentsEnabled=true;
        public RemoteSyncRuntime? RemoteSync;
        public RemoteDetectionStatus Detection=new();
        public RemoteChangeSessionState ChangeSession=new();
        public RemoteCapacitySession? Capacity;
        public RemoteCapacitySnapshot? CapacityStatus;
        public string? CapacityScope;
        public int CapacityForce;
        public bool SchedulingEnabled=true,IssueDefinitionReady;
        // 导入在本进程只保存；重启重新加载后恢复配置中的自动连接选择。
        public bool SkipStartupConnection;
        public Action<SourceContentBaseline>? BaselineObserver;
        public Action<string>? WriteObserver;
        public Action? CapacityObserver;
        public string Status=definition.RemovalPending?"移除未完成":"未连接";
        public string Detail=definition.RemovalPending?"请继续已选择的移除方式；不会自动重放删除操作。":"";
    }
    private sealed class Job(string id,Guid root,string operation,CancellationToken token)
    {
        public readonly Guid Root=root;
        public readonly CancellationTokenSource Cancel=CancellationTokenSource.CreateLinkedTokenSource(token);
        public MountOperationSnapshot Snapshot=new(id,operation,"Queued",0,0,[],null,root);
        public readonly List<MountItemResult> Results=[];
        public long LastPublished;public int PublishedCount;
        public Task Work=Task.CompletedTask;
    }
    private readonly ExePreviewCache _previews;
#if HYBRIDMOUNT_HEADLESS_TEST
    // 旧预览仅由隔离回归显式启用；生产能力由原生缩略图注册及当前策略决定。
    internal bool LegacyPreviewsForTests {get;set;}
    internal bool LocalPreviewsForTests {get;set;}
    internal bool RemoteThumbnailsForTests {get;set;}
    private bool CustomPreviewsAvailable=>LocalPreviewsForTests||LegacyPreviewsForTests;
    private bool LegacyRemotePreviewsAllowed=>LegacyPreviewsForTests;
    private bool RemoteThumbnailsAllowed=>RemoteThumbnailsForTests;
#else
    private readonly bool _nativeThumbnails=CloudShellState.ThumbnailCapability().Available;
    private bool CustomPreviewsAvailable=>ShellExtensionPolicy.CustomThumbnailsEnabled&&_nativeThumbnails;
    private bool RemoteThumbnailsAllowed=>CustomPreviewsAvailable;
#endif
    private static string PreviewDirectory(MountRepository repository)=>ThumbnailCachePaths.DirectoryPath;
    private readonly Services.FileTasks.TaskDispatcher _fileDispatcher;
    private readonly MountRepository _repository;
    private readonly ILocalUploadCommitter _uploadCommitter;
    private readonly IAsyncDisposable _remoteEvents;
    private readonly LoggerService _logger;
    private readonly IRecoveryShellRegistration _removalShell=RecoveryShellRegistration.Create();
    internal IRecoveryShellRegistration? RemovalShellForTests {get;set;}
    internal Action<string>? RemovalStageForTests {get;set;}
    private MountRemovalCompletion RemovalCompletion()=>new(Path.Combine(_repository.DataDirectory,"Recovery"),RemovalShellForTests??_removalShell,ThrowIfStopping){StageForTests=RemovalStageForTests};
    private void RecordRemovalFailure(Guid id,Exception failure)
    {
        try{if(RemovalCompletion().Read(id) is {} journal)RemovalCompletion().Failure(journal,failure);}
        catch(Exception error){_logger.QueueException("移除恢复记录","Error","无法保存失败阶段",error);}
    }
    private void RemovalStatus(Runtime root)
    {
        var journal=RemovalCompletion().Read(root.Definition.Id);
        Status(root,"移除未完成",(journal?.Description??"请继续已选择的移除方式；不会自动重放删除操作。")+(journal?.Error is {} error?"；"+error:""));
    }
    private readonly IFileSourceFactory _factory;
    private Task? _maintenance;
    private readonly ConcurrentDictionary<Guid,Runtime> _roots=new();
    private readonly ConcurrentDictionary<string,Job> _jobs=new();
    private readonly CancellationTokenSource _stop=new();
    private Task? _dispose;
    public event Action? Changed;
    public TasksQueueService TasksQueue {get;}
    private readonly bool _ownsTasksQueue;
    public MountManager(MountRepository repository,LoggerService logger,IFileSourceFactory? factory=null,ILocalUploadCommitter? uploadCommitter=null,TasksQueueService? tasksQueue=null)
    {
        TasksQueue=tasksQueue??new();_ownsTasksQueue=tasksQueue is null;_fileDispatcher=new(repository,logger);
        var definitions=repository.LoadDefinitions();
        repository.RecoverFileTaskAttempts();repository.RecoverUploadAttention();repository.RecoverRemovedRemoteOperations(definitions.Select(x=>x.Id).ToHashSet());
        _uploadCommitter=uploadCommitter??new CloudUploadCommitter();
        _previews=new ExePreviewCache(PreviewDirectory(repository),EnterPreviewAsync,value=>logger.Queue("缩略图","Warning",value),RenderPreviewAsync,()=>CustomPreviewsAvailable,id=>_roots.TryGetValue(id,out var root)?root.Definition with{RemovalPending=root.Definition.RemovalPending||root.RemovalRequested}:null,renderOwnsMutex:true);_repository=repository; _logger=logger; _factory=factory??new FileSourceFactory();_remoteEvents=CreateRemoteEvents();
        foreach(var definition in definitions)
        {
            _roots[definition.Id]=new Runtime(definition){RequiresInspection=true,Status="检查中",Detail=""};
        }
        TasksQueue.Attach(this);_displayWorker=Task.Run(UpdateDisplaySnapshotsAsync);RefreshDisplaySnapshot();
    }
    private sealed class PreviewLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate=gate;
        public void Dispose()=>Interlocked.Exchange(ref _gate,null)?.Release();
    }
    /// <summary>图标提取与脱水、刷新、移除共用根锁；排队期间版本已变化则丢弃旧请求。</summary>
    private async Task<IDisposable?> EnterPreviewAsync(MountDefinition mount,MountEntry entry,CancellationToken token)
    {
        if(!_roots.TryGetValue(mount.Id,out var root)||_stop.IsCancellationRequested)return null;
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        if(_stop.IsCancellationRequested||root.RemovalRequested||root.Definition.RemovalPending||!_roots.ContainsKey(mount.Id)||
            !(_repository.EntryById(mount.Id,entry.Id) is {} current&&current.Version==entry.Version&&current.LocalPath==entry.LocalPath&&!current.Deleted&&current.RemotePresence!=RemotePresence.Deleted))
        {root.Gate.Release();return null;}
        return new PreviewLease(root.Gate);
    }
    /// <summary>任务名称及相对路径只用内存定义；不为每行重新查询SQLite。</summary>
    internal IReadOnlyDictionary<Guid,MountDefinition> TaskDefinitions=>_roots.ToDictionary(pair=>pair.Key,pair=>pair.Value.Definition);
    public IReadOnlyList<MountSnapshot> Mounts => _roots.Values.Select(Snapshot).OrderBy(item=>item.Definition.Name).ThenBy(item=>item.Definition.Id).ToArray();
    /// <summary>实际连接状态供页面启用操作；不把后台上传视为全页忙碌。</summary>
    private MountSnapshot Snapshot(Runtime root)
    {
        lock(root.ConnectionSync)return new(root.Definition,root.Status,root.Detail,root.Detection,root.Session is not null,
            root.Connecting is not null,root.ConnectionStops>0,
            _jobs.Values.FirstOrDefault(job=>job.Root==root.Definition.Id&&job.Snapshot.State is "Queued" or "Running")?.Snapshot.Operation,root.CapacityStatus,_repository.Uploads(root.Definition.Id).Count(UploadAttentionRules.NeedsAttention)+RenameIntents(root.Definition.Id).Count(x=>x.NeedsAttention)+(_renameErrors.ContainsKey(root.Definition.Id)?1:0));
    }
    /// <summary>当前上传问题包括没有索引或已清除历史的文件；只读，不自动恢复上传。</summary>
    public IReadOnlyList<MountOperationSnapshot> UploadIssues=>_repository.Uploads().Where(UploadAttentionRules.NeedsAttention).OrderBy(x=>x.Attention!.OccurredUtc).Select(UploadSnapshot).Concat(RenameIntents().Where(x=>x.NeedsAttention).Select(x=>x.Snapshot())).Concat(RenameJournalIssues()).Concat(Operations.Where(row=>row.Operation is "freeSpace" or "keepOnDevice"&&row.State is "Failed" or "PartialFailure")).ToArray();
    public IReadOnlyList<MountOperationSnapshot> Operations=>ReadTaskPresentation().Operations;
    /// <summary>任务、问题、冲突及调度共用一次持久读取；构造与解析在数据库锁外进行。</summary>
    private long _lastTaskReadDiagnostic;
    internal (IReadOnlyList<MountOperationSnapshot> Operations,IReadOnlyDictionary<string,(MountingPriority Priority,DateTimeOffset? Retry)> Schedule) ReadTaskPresentation(bool includeIssues=false)
    {
        var state=_repository.ReadTaskState();var renames=RenameIntents().ToArray();
        var measured=_repository.LastTaskRead;var now=Environment.TickCount64;
        if(measured.Milliseconds>=150&&now-Interlocked.Read(ref _lastTaskReadDiagnostic)>=30000)
        {Interlocked.Exchange(ref _lastTaskReadDiagnostic,now);_logger.Queue("任务快照","Information",$"查询={measured.Queries}；解析行={measured.Rows}；耗时毫秒={measured.Milliseconds:F1}");}
        var history=state.History.ToDictionary(x=>x.Id);var schedule=new Dictionary<string,(MountingPriority,DateTimeOffset?)>();
        foreach(var upload in state.Uploads)
        {
            schedule[upload.Id]=(upload.ExplicitRequest?MountingPriority.User:MountingPriority.Transaction,upload.NextAttemptUtc);
            if(!TaskHistoryRules.IsHistory(upload.State)||history.ContainsKey(upload.Id)||includeIssues&&UploadAttentionRules.NeedsAttention(upload))
                history[upload.Id]=DescribeOperationWait(upload.ToSnapshot(),upload.SourcePath,state,renames) with{SubmissionOrder=history.GetValueOrDefault(upload.Id)?.SubmissionOrder??0};
        }
        foreach(var pending in state.Remote.Where(x=>_roots.ContainsKey(x.MountId)))
        {
            schedule[pending.Id]=(MountingPriority.Transaction,_roots[pending.MountId].RemoteSync?.StagingRetries.NextRetryUtc(pending.Id));
            if(pending.State is not("Completed" or "Superseded")&&!(pending.State=="Canceled"&&pending.MetadataOnly)||history.ContainsKey(pending.Id))
                history[pending.Id]=DescribeOperationWait(RemoteTask(pending),pending.Path,state,renames);
        }
        foreach(var rename in renames)
        {
            schedule[rename.Id]=(rename.RetryRequested||rename.ReadOnlyRequested?MountingPriority.User:MountingPriority.Transaction,null);
            if(!rename.Finished||history.ContainsKey(rename.Id))history[rename.Id]=rename.Snapshot();
        }
        foreach(var problem in RenameJournalIssues())history[problem.Id]=problem;
        foreach(var job in _jobs.Values)
            if(job.Snapshot.State is "Queued" or "Running"||history.ContainsKey(job.Snapshot.Id))history[job.Snapshot.Id]=job.Snapshot with{SubmissionOrder=history.GetValueOrDefault(job.Snapshot.Id)?.SubmissionOrder??0};
        foreach(var task in _fileOperations.Values)
            if(task.Snapshot.State is "Running" or "Canceling" or "Deferred"||history.ContainsKey(task.Snapshot.Id))history[task.Snapshot.Id]=task.Snapshot with{SubmissionOrder=history.GetValueOrDefault(task.Snapshot.Id)?.SubmissionOrder??0};
        return (history.Values.ToArray(),schedule);
    }
    /// <summary>保留旧成功历史入口；生命周期和清理规则由统一方法拥有。</summary>
    public Task<int> ClearCompletedOperationsAsync(CancellationToken token=default)=>ClearOperationHistoryAsync(TaskHistoryClearMode.Completed,token);
    /// <summary>后台清理展示历史，不联网、不删除恢复记录；退出先取消接纳并等待此所有者。</summary>
    public Task<int> ClearOperationHistoryAsync(TaskHistoryClearMode mode,CancellationToken token=default)
        =>OwnAsync(cancel=>Task.Run(()=>
        {
            cancel.ThrowIfCancellationRequested();
            // 只把已存在且不属于活动Job的旧ID作为中断历史，不能将新排队任务按原始状态删除。
            var interrupted=mode==TaskHistoryClearMode.All?_repository.LoadOperations()
                .Where(row=>row.State=="Interrupted"&&(!_jobs.TryGetValue(row.Id,out var job)||job.Snapshot.State is not("Queued" or "Running")))
                .Select(row=>row.Id).ToArray():Array.Empty<string>();
            var count=_repository.ClearOperations(mode,interrupted);Notify();return count;
        },cancel),token);
    private readonly ConcurrentDictionary<Guid,MountDefinition> _previewDefinitions=new();
    private void Notify()
    {
        foreach(var root in _roots.Values)
        {
            var definition=root.Definition with{RemovalPending=root.Definition.RemovalPending||root.RemovalRequested};
            if(!_previewDefinitions.TryGetValue(definition.Id,out var previous)||previous!=definition)
            {_previews.ConfigureMount(definition);_previewDefinitions[definition.Id]=definition;}
        }
        NotifyIssues();RefreshDisplaySnapshot();NotifyChanged();
    }
    /// <summary>仅发布已有内存状态；后台快照回调不能反过来重新请求同一查询。</summary>
    private void NotifyChanged(){if(Changed is not {} handlers)return;foreach(Action handler in handlers.GetInvocationList())try{handler();}catch(Exception error){_logger.QueueException("挂载界面","Warning","操作未完成",error);}}
    private void Status(Runtime root,string status,string detail="") { root.Status=status; root.Detail=detail; Notify(); }
    private Task? _startup;
    public Task StartAsync() { lock(_lifetime){ThrowIfStopping();_maintenance??=Task.Run(CacheLoopAsync);_issuePublishing??=Task.Run(PublishIssueLoopAsync);return _startup??=Task.Run(InitializeAsync);} }
    private async Task InitializeAsync()
    {
        foreach(var root in _roots.Values){if(IsStopping)return;try{await EnsureInspectionAsync(root).ConfigureAwait(false);}catch(OperationCanceledException)when(IsStopping){return;}}
        foreach(var root in _roots.Values.Where(root=>root.Definition.AutoConnect && !root.Definition.RemovalPending && !root.SkipStartupConnection))
            try { if(_factory is FileSourceFactory && root.Definition.Source is null) { Status(root,"需要迁出","旧版模拟源保留；请选择下载并移除或清空。");continue; } await ConnectAsync(root.Definition.Id,_stop.Token).ConfigureAwait(false); } catch(OperationCanceledException)when(IsStopping) { break; } catch(ConnectionStoppedException) { } catch(Exception error) { _logger.QueueException("恢复挂载","Error","操作未完成",error); }
    }
    /// <summary>同根共享一次启动核对；连接、编辑和收尾先等待，调用者不能在持根锁时进入。</summary>
    private Task EnsureInspectionAsync(Runtime root){lock(root.ConnectionSync)return root.RequiresInspection?root.Inspection??=InspectStartupRootAsync(root):Task.CompletedTask;}
    /// <summary>始终离开调用上下文后取得根锁核对注册；退出使用应用令牌，失败保留原配置与恢复入口。</summary>
    private async Task InspectStartupRootAsync(Runtime root)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await root.Gate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            try{InspectRegistration(root);root.Status="未连接";root.Detail="";}
            catch(Exception error){root.Status="需要恢复";root.Detail=error.Message;_logger.QueueException("挂载恢复","Warning","同步根注册核对失败，保留原配置",error,root.Definition.Id.ToString("N"));}
            if(root.Definition.RemovalPending)
                try{RemovalStatus(root);}catch(Exception error){root.Status="移除未完成";root.Detail="恢复记录读取失败："+error.Message;_logger.QueueException("挂载恢复","Warning","移除恢复记录读取失败，未自动重放操作",error,root.Definition.Id.ToString("N"));}
        }
        finally{root.Gate.Release();Notify();}
    }
    public Task<MountDefinition> AddAsync(string name,string directory,bool autoConnect,CancellationToken token=default,FileSourceConfiguration? source=null,SourceCredentials? credentials=null,bool autoUpload=true,IReadOnlyList<FileExclusionRule>? exclusions=null,MountCreationOptions? initial=null)
        =>OwnAsync(cancel=>AddCoreAsync(name,directory,autoConnect,cancel,source,credentials,autoUpload,FileExclusionRules.Validate(exclusions??FileExclusionRules.Defaults()),initial??new()),token);
    private async Task<MountDefinition> AddCoreAsync(string name,string directory,bool autoConnect,CancellationToken token,FileSourceConfiguration? source,SourceCredentials? credentials,bool autoUpload,IReadOnlyList<FileExclusionRule> exclusions,MountCreationOptions initial)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        MountDefinition definition;
        await _configurationGate.WaitAsync(token).ConfigureAwait(false);
        try {
            if(initial.RemoteIncrementalSeconds is <10 or >86400||initial.RemoteFullSeconds is <30 or >604800)throw new ArgumentException("检测间隔超出允许范围。");
            definition = SaveNewMountCore(name,directory,autoConnect,source,credentials,initial.ThumbnailsEnabled,token,remoteAuto:initial.RemoteAutoDetectEnabled,incremental:initial.RemoteIncrementalSeconds,full:initial.RemoteFullSeconds,sqs:initial.Sqs,sqsCredentials:initial.SqsCredentials,capacityAuto:initial.CapacityAutoDetectEnabled,maximumCapacity:initial.MaximumCapacityBytes,exclusions:exclusions);
        }
        finally { _configurationGate.Release(); }
        Notify(); await ConnectOwnedAsync(definition.Id, token).ConfigureAwait(false); return Root(definition.Id).Definition;
    }
    public SourceCredentials GetCredentials(Guid id) { Root(id);return new CredentialStore(_repository.DataDirectory).Load(id); }
    /// <summary>只更换凭据，不能将已有占位悄悄重绑定到另一数据源。</summary>
    public Task UpdateCredentialsAsync(Guid id,SourceCredentials credentials,CancellationToken token=default,FileSourceConfiguration? settings=null)
        =>OwnAsync(cancel=>UpdateCredentialsCoreAsync(id,credentials,cancel,settings),token);
    private async Task UpdateCredentialsCoreAsync(Guid id,SourceCredentials credentials,CancellationToken token,FileSourceConfiguration? settings)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var root=Root(id);await EnsureInspectionAsync(root).ConfigureAwait(false);lock(root.ConnectionSync)root.ConnectionStops++;
        try
        {
            await _fileDispatcher.StopCaptureAsync(root.Definition.Id).ConfigureAwait(false);
            CancelConnection(root);RequestRemoteStop(root);RequestUploadStop(root);RequestCapacityStop(root);RequestPinStop(root);
            await Task.WhenAll(StopRemoteAsync(root),StopUploadsAsync(root),StopCapacityAsync(root),StopPinIntentsAsync(root),root.ConnectionFinished).ConfigureAwait(false);
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
            if(root.Definition.RemovalPending || root.RemovalRequested || !_roots.ContainsKey(id))throw new IOException("移除过程中不能更换凭据。");
            var effective=settings??root.Definition.Source??throw new IOException("旧版挂载不能更换协议。");
            FileSourceFactory.Endpoint(effective);
            FileSourceFactory.ValidateProtocolOptions(effective);FileSourceFactory.ValidateProtocolCredentials(effective,credentials);
            if(settings is not null)
            {
                var previous=root.Definition.Source??throw new IOException("旧版挂载不能更换协议。");
                if(previous.Kind!=settings.Kind || (!CanEditRemoteRoot(id) && (previous.Endpoint!=settings.Endpoint||previous.Bucket!=settings.Bucket||previous.Prefix!=settings.Prefix||previous.AllowHttp!=settings.AllowHttp)))
                    throw new IOException("已有挂载不能更换远端根，请新建挂载。");
                FileSourceFactory.Endpoint(settings);
                FileSourceFactory.ValidateProtocolOptions(settings);
                root.Definition=root.Definition with{Source=settings};_repository.SaveDefinition(root.Definition);
            }
            new CredentialStore(_repository.DataDirectory).Save(id,credentials);
            root.Definition=root.Definition with{CredentialRevision=root.Definition.CredentialRevision+1};_repository.SaveDefinition(root.Definition);
            await DisconnectCoreAsync(root).ConfigureAwait(false);await ConnectCoreAsync(root,token,ownStops:1).ConfigureAwait(false);
        }
            finally{root.Gate.Release();}
        }
        finally
        {
            lock(root.ConnectionSync)root.ConnectionStops--;
            if(root.Session is not null&&!IsStopping){StartUploads(root);StartPinIntents(root);StartRemoteSync(root);}
        }
    }
    /// <summary>只有实际无注册、无云占位、无物化索引的从未注册条目可改变远端根。</summary>
    /// <summary>后台只读核验远端根能否编辑；应用拥有查询，取消或失败不会改配置或注册。</summary>
    public Task<bool> CanEditRemoteRootAsync(Guid id,CancellationToken token=default)
        =>OwnAsync(async cancel=>{await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);cancel.ThrowIfCancellationRequested();return CanEditRemoteRoot(id);},token);
    public bool CanEditRemoteRoot(Guid id)
    {
        var root=Root(id);
        if(root.Status=="检查中" || root.Definition.RemovalPending || root.Definition.RegistrationStage!=MountRegistrationStage.NotRegistered)return false;
        lock(root.ConnectionSync)if(root.RemovalRequested || root.Connecting is not null)return false;
        return !CloudRootRegistration.IsOwnedRoot(root.Definition) && !CloudFilesNative.IsCloudPath(root.Definition.LocalPath) && !MountPathGuard.ContainsCloudPlaceholders(root.Definition.LocalPath) &&
            !_repository.Entries(id).Any(e=>e.Materialized);
    }
    /// <summary>迁移旧配置时同时检查原生、WinRT、占位和索引；丢失的注册不自动接管。</summary>
    private bool InspectRegistration(Runtime root)
    {
        var registered=CloudRootRegistration.IsOwnedRoot(root.Definition);
        if(!registered && (CloudFilesNative.IsCloudPath(root.Definition.LocalPath)||MountPathGuard.ContainsCloudPlaceholders(root.Definition.LocalPath)))
            throw new IOException("同步根注册丢失且残留云占位，保留恢复信息，禁止直接清空。");
        var stage=root.Definition.RegistrationStage;
        if(registered)stage=MountRegistrationStage.Registered;
        else if(stage is MountRegistrationStage.Unknown or MountRegistrationStage.Registering)
        {
            if(_repository.Entries(root.Definition.Id).Any(e=>e.Materialized) && root.Definition.RemovalStage!=MountRemovalStage.Unregistering)
                throw new IOException("注册丢失但存在物化索引，保留配置待恢复。");
            stage=MountRegistrationStage.NotRegistered;
        }
        if(stage!=root.Definition.RegistrationStage)SetRegistration(root,stage);
        return registered;
    }
    private void SetRegistration(Runtime root,MountRegistrationStage stage)
    { root.Definition=root.Definition with{RegistrationStage=stage};_repository.SaveDefinition(root.Definition); }
    private static void CancelConnection(Runtime root,bool removing=false,string reason="Disconnect")
    {
        lock(root.ConnectionSync)
        {
            if(removing){root.RemovalRequests++;root.RemovalRequested=true;root.RuntimeFailed=false;}
            if(root.Connecting is {IsCancellationRequested:false} connection){root.ConnectionStopReason=removing?"Removal":reason;root.ConnectionSignal=connection.CancelAsync();}
        }
    }
    private Runtime Root(Guid id)=>_roots.TryGetValue(id,out var root)?root:throw new IOException("挂载不存在。");
    /// <summary>连接任务登记到应用所有者；退出等待手动连接与启动恢复，不让迟到结果创建新会话。</summary>
    public Task ConnectAsync(Guid id,CancellationToken token=default)=>ConnectAsync(id,token,runtimeFailedOnly:false);
    private Task ConnectAsync(Guid id,CancellationToken token,bool runtimeFailedOnly)=>OwnAsync(cancel=>ConnectOwnedAsync(id,cancel,runtimeFailedOnly),token);
    private async Task ConnectOwnedAsync(Guid id,CancellationToken token,bool runtimeFailedOnly=false)
    {
        var root=Root(id);await EnsureInspectionAsync(root).ConfigureAwait(false); await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(runtimeFailedOnly)lock(root.ConnectionSync)
                if(!root.RuntimeFailed||root.RemovalRequested||root.Definition.RemovalPending||root.Connecting is not null||root.ConnectionStops>0||root.Session is not null)return;
            await ConnectCoreAsync(root,token).ConfigureAwait(false);
        }
        finally { root.Gate.Release(); }
    }
    private async Task ConnectCoreAsync(Runtime root,CancellationToken token,int ownStops=0)
    {
        token.ThrowIfCancellationRequested();
        if(root.RemovalRequested || !_roots.TryGetValue(root.Definition.Id,out var current) || !ReferenceEquals(current,root))throw new IOException("挂载正在移除或已经移除。");
        lock(root.ConnectionSync)if(root.ConnectionStops>ownStops)throw new IOException("挂载正在断开，请完成后重新连接。");
        if(root.Session is not null)return;
        if(root.Connecting is not null)throw new IOException("挂载正在连接，请等待当前连接完成。");
        if(root.Definition.RemovalPending)throw new IOException("上次移除尚未完成，请继续相同的移除方式。");
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);
        lock(root.ConnectionSync)
        {
            if(root.RemovalRequested)throw new IOException("挂载正在移除。");
            token.ThrowIfCancellationRequested();root.ConnectionStopReason=null;root.ConnectionSignal=Task.CompletedTask;root.Connecting=connection;root.ConnectionFinished=finished.Task;
        }
        token=connection.Token;
        root.ChangeSession=new();root.Detection=root.Detection with{Mode="尚未检测",Error=null};
        Status(root,"连接中");
        try
        {
            // 连接所有权和取消状态已经登记；即使后台已预热，也先离开UI线程再做同步本机查询。
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            root.QueueBinding=TasksQueue.StartMount(root.Definition.Id);
            using var queueContext=TasksQueue.Enter(root.QueueBinding,new(Guid.NewGuid().ToString("N"),root.Definition.Id,root.QueueBinding.Generation,MountingPriority.User,DateTimeOffset.UtcNow,"connect"));
            await NativeRuntimeClient.PrepareConnectionAsync().ConfigureAwait(false);token.ThrowIfCancellationRequested();
            var registered=InspectRegistration(root);
            if(MountPathGuard.ReadRoot(root.Definition.LocalPath) is null && _repository.Entries(root.Definition.Id).Any(entry=>entry.Materialized))throw new IOException("原同步根注册已丢失；保留数据，不能自动重新注册。");
            root.Source=await OutsideRootAsync(root,()=>_factory.CreateAsync(root.Definition,_repository,false,token)).ConfigureAwait(false);
            if(root.Source is IMountNetworkSource network)network.BindQueue(root.QueueBinding);ObserveUploadSource(root);
            if(root.Source is IMountSourceRuntime preparing)await OutsideRootAsync(root,()=>preparing.BeforeRegistrationAsync(token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            registered=InspectRegistration(root);
            if(!registered)SetRegistration(root,MountRegistrationStage.Registering);
            await CloudRootRegistration.EnsureAsync(root.Definition,token).ConfigureAwait(false);
            SetRegistration(root,MountRegistrationStage.Registered);
            token.ThrowIfCancellationRequested();
            lock(_lifetime)
            {
                ThrowIfStopping();token.ThrowIfCancellationRequested();
                root.Session=new CloudMountSession(root.Definition,root.Source,_repository,_logger,localContentReady:entry=>{InvalidateCacheUsage(RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath));QueueLocalPreview(root,entry);},localChanged:()=>root.Uploads?.RequestCloseScan()){PinResourcesReleasingForTests=cancel=>PinResourcesReleasingForTests?.Invoke(cancel)??Task.CompletedTask}; ConnectSession(root);
            }
            Reconcile(root);MigrateContinuousUploads(root);ScanLocalUploadsCore(root,token);
            IReadOnlyList<MountItemResult> refreshed;
            if(root.Source is IRemoteChangeSource)
            {
                StartRemoteSync(root,startLoop:false);var runtime=root.RemoteSync!;
                root.Gate.Release();
                try{await DetectRemoteAsync(root,runtime,true,token).ConfigureAwait(false);}
                catch(SourceRescanRequiredException)
                {runtime.RescanSoon=true;root.Detection=new("S3 完整对账",Error:"远端持续变化，初始扫描暂未稳定；连接保留，后台立即继续完整扫描。");}
                finally{await root.Gate.WaitAsync().ConfigureAwait(false);}
                refreshed=RemotePendingFor(root).Where(x=>x.State is not("Completed" or "Superseded")&&!(x.State=="Canceled"&&x.MetadataOnly)).Select(x=>new MountItemResult(x.Path,"Conflict",x.Error??"远端变更待处理。")).ToArray();
            }
            else refreshed=await PopulateAsync(root,"",token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();Status(root,"已连接",RefreshSummary(refreshed));
            if(refreshed.Count!=0)_repository.SaveOperation(new(Guid.NewGuid().ToString("N"),"refresh",refreshed.Any(item=>item.Status!="Completed")?"PartialFailure":"Completed",refreshed.Count,refreshed.Count,refreshed,null,root.Definition.Id));
            _previews.ResumeMount(root.Definition.Id);
#if HYBRIDMOUNT_HEADLESS_TEST
            if (!BrowseDrivenPreviews)
                foreach (var cached in _repository.Entries(root.Definition.Id)) _previews.Queue(root.Definition, cached);
#endif
            SetPreviewObservation(root, root.Definition.ThumbnailsEnabled);
            if(root.Source is IMountSourceRuntime recovery)
            {
                try{await OutsideRootAsync(root,()=>recovery.AfterRegistrationAsync(token)).ConfigureAwait(false);}
                catch(Exception recoveryError)when(recoveryError is not OperationCanceledException)
                {Status(root,"已连接",recovery.RecoveryWarning);_logger.QueueException(recovery.RecoveryLogArea,"Warning","操作未完成",recoveryError);}
            }
            Interlocked.Exchange(ref root.CapacityForce,1);StartCapacity(root);StartUploads(root);StartPinIntents(root);StartRemoteSync(root);
            lock(root.ConnectionSync){token.ThrowIfCancellationRequested();root.RuntimeFailed=false;}Notify();
        }
        catch(Exception error)
        {
            try
            {
                RequestRemoteStop(root);root.Gate.Release();
                try{await StopRemoteAsync(root).ConfigureAwait(false);}finally{await root.Gate.WaitAsync().ConfigureAwait(false);}
                await DisconnectCoreAsync(root).ConfigureAwait(false);
            }
            catch(Exception cleanup){Status(root,"异常","连接停止时清理失败，请查看生命周期阶段。");throw new AggregateException(error,cleanup);}
            if(error is OperationCanceledException&&token.IsCancellationRequested)
            {var reason=IsStopping?"Shutdown":root.ConnectionStopReason??"Caller";ObserveStop("ConnectionCanceled."+reason,null);Status(root,"未连接",IsStopping?"程序正在退出，连接已停止。":"连接已取消。");throw new ConnectionStoppedException(error,token);}
            Status(root,"异常",error.Message);throw;
        }
        finally
        {
            Task signal;lock(root.ConnectionSync){signal=root.ConnectionSignal;if(ReferenceEquals(root.Connecting,connection))root.Connecting=null;}
            try{await signal.ConfigureAwait(false);}finally{finished.TrySetResult();}
        }
    }
    /// <summary>收齐远端快照并确认缺失之后才应用；局部冲突独立记录，不覆盖本地修改。</summary>
    private async Task<IReadOnlyList<MountItemResult>> PopulateAsync(Runtime root,string sourcePath,CancellationToken token)
    {
        var snapshot=new Dictionary<string,SourceEntry>(StringComparer.OrdinalIgnoreCase);
        async Task Collect(string parent)
        {
            await foreach(var metadata in root.Source!.ListAsync(parent,token).ConfigureAwait(false))
            {
                RelativePaths.Normalize(metadata.Path);
                if(metadata.Path.Length==0||RelativePaths.Parent(metadata.Path)!=parent||metadata.Size<0||metadata.ModifiedUtc<DateTimeOffset.FromFileTime(0)||!snapshot.TryAdd(metadata.Path,metadata))
                    throw new IOException("远端枚举包含越界、重复、大小写冲突或无效元数据；未应用快照。");
                if(snapshot.Count>1000000)throw new IOException("快照超过首版百万条安全限制，请缩小远端根。");
                if(metadata.IsDirectory)await Collect(metadata.Path).ConfigureAwait(false);
            }
        }
        var expectedSource=root.Source;var expectedSession=root.Session;
        var missing=await OutsideRootAsync(root,async()=>{await Collect(sourcePath).ConfigureAwait(false);return await ConfirmRemoteDeletionsAsync(root,snapshot,token).ConfigureAwait(false);}).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();if(root.Source!=expectedSource||root.Session!=expectedSession)throw new OperationCanceledException(token);var results=new List<MountItemResult>();
        var populateIndex=_repository.Entries(root.Definition.Id).ToDictionary(x=>x.SourcePath,StringComparer.OrdinalIgnoreCase);
        foreach(var metadata in snapshot.Values.OrderBy(item=>item.Path.Count(c=>c=='/')).ThenBy(item=>item.Path,StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var entry=populateIndex.GetValueOrDefault(metadata.Path);
                if(entry is not null)
                {
                    // 清理后的项重新出现在远端时，只恢复远端存在性，不解除用户本地删除／修改保护。
                    if(entry.RemotePresence==RemotePresence.Deleted&&!entry.Deleted&&LocalMissing(RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath)))
                    {_repository.CompleteRemoteCleanup(root.Definition.Id,entry);entry=entry with{Materialized=false};}
                }
                var parent=populateIndex.GetValueOrDefault(RelativePaths.Parent(metadata.Path));
                if(parent?.Deleted==true||parent?.Protected==true||entry?.Deleted==true)continue;
                var local=parent is null?metadata.Path:parent.LocalPath+"/"+Path.GetFileName(metadata.Path);
                entry??=new(Guid.NewGuid().ToString("N"),metadata.Path,local,metadata.Version,metadata.IsDirectory);
                if(entry.IsDirectory!=metadata.IsDirectory)throw new SourceConflictException("远端文件与目录类型变化，保留本地内容，请处理冲突。");
                var full=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);var absent=LocalMissing(full);
                if(absent&&!entry.Materialized)entry=entry with{Version=metadata.Version};
                _repository.CommitDiscoveryBatch(root.Definition.Id,[entry],[],[]);
                populateIndex[entry.SourcePath]=entry;
                if(absent)root.Session!.CreatePlaceholder(entry,metadata);
                else if(!entry.Materialized)_repository.SaveEntry(root.Definition.Id,entry with{Protected=true,Materialized=true});
                else if(!root.Source!.IsSameVersion(entry.Version,metadata.Version)&&!entry.IsDirectory)
                {
                    if(entry.Protected||!root.Session!.RefreshPlaceholder(entry,metadata))throw new SourceConflictException("远端内容变化，但本地已修改、固定或正在使用；已保留旧内容，请处理后刷新。");
                    _repository.SaveEntry(root.Definition.Id,entry with{Version=metadata.Version});
                }
                else if(entry.IsDirectory&&!root.Source!.IsSameVersion(entry.Version,metadata.Version))_repository.SaveEntry(root.Definition.Id,entry with{Version=metadata.Version});
            }
            catch(SourceConflictException error){results.Add(new(metadata.Path,"Conflict",error.Message));}
        }
        results.AddRange(ApplyRemoteDeletions(root,missing,token));return results;
    }
    /// <summary>只遍历本根内普通目录和本产品云目录，不穿越符号链接、联接或其他提供程序。</summary>
    private IEnumerable<string> Walk(MountDefinition mount,string directory)
    {
        foreach(var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes=File.GetAttributes(path);
            if((attributes&FileAttributes.ReparsePoint)!=0 && (!CloudFilesNative.IsCloudPath(path)||MountPathGuard.ReadRoot(path)?.MountId!=mount.Id))continue;
            yield return path;
            if((attributes&FileAttributes.Directory)!=0)foreach(var child in Walk(mount,path))yield return child;
        }
    }
    private void Reconcile(Runtime root)
    {
        MountPathGuard.ValidateOperation(root.Definition,root.Definition.LocalPath);
        var entries=_repository.Entries(root.Definition.Id); var seen=new HashSet<string>();
        var byLocal=entries.ToDictionary(x=>x.LocalPath,StringComparer.OrdinalIgnoreCase);var byId=entries.ToDictionary(x=>x.Id);
        var updates=new List<MountEntry>();
        foreach(var path in Walk(root.Definition,root.Definition.LocalPath))
        {
            var relative=Path.GetRelativePath(root.Definition.LocalPath,path).Replace('\\','/');
            var info=CloudFilesNative.Inspect(path);
            if(info is null)
            {
                var local=byLocal.GetValueOrDefault(relative);
                if(local is not null){seen.Add(local.Id);var changed=local with{Protected=true,Materialized=true};if(changed!=local)updates.Add(changed);}continue;
            }
            var marker=MountPathGuard.ReadEntry(info.Value.Identity);
            var entry=marker?.MountId==root.Definition.Id?byId.GetValueOrDefault(marker.EntryId):null;
            if(entry is null)continue;
            seen.Add(entry.Id);
            var changedEntry=entry with{LocalPath=relative,Materialized=true,Protected=entry.Protected||entry.LocalPath!=relative||info.Value.Info.ModifiedSize>0||info.Value.Info.InSyncState!=1};if(changedEntry!=entry)updates.Add(changedEntry);
        }
        foreach(var entry in entries.Where(entry=>entry.Materialized&&!entry.Deleted&&entry.RemotePresence!=RemotePresence.Deleted&&!seen.Contains(entry.Id)))updates.Add(entry with{Deleted=true,Protected=true});
        foreach(var batch in updates.Chunk(256))_repository.SaveEntries(batch,root.Definition.Id);
    }
    public Task DisconnectAsync(Guid id,CancellationToken token=default)=>OwnAsync(cancel=>DisconnectOwnedAsync(id,cancel),token);
    private async Task DisconnectOwnedAsync(Guid id,CancellationToken token,bool runtimeFailure=false)
    {
        var root=Root(id);await EnsureInspectionAsync(root).ConfigureAwait(false);lock(root.ConnectionSync){root.ConnectionStops++;if(!runtimeFailure)root.RuntimeFailed=false;}Notify();
        try
        {
            await _fileDispatcher.StopCaptureAsync(id).ConfigureAwait(false);
            CancelConnection(root);await root.ConnectionFinished.WaitAsync(token).ConfigureAwait(false);
            // 先取消任务所有者，再停止共享检测：关联读取可立即结束，不能在任务令牌尚有效的窗口被误记 Failed。
            foreach(var job in _jobs.Values.Where(job=>job.Root==id))job.Cancel.Cancel();
            RequestRemoteStop(root);RequestUploadStop(root);RequestCapacityStop(root);RequestPinStop(root);var previewStop=_previews.CancelMountAsync(id);var reads=root.Session?.RequestStop()??Task.CompletedTask;
            await Task.WhenAll(StopRemoteAsync(root),StopUploadsAsync(root),StopCapacityAsync(root),StopPinIntentsAsync(root),previewStop,reads,root.ConnectionFinished).ConfigureAwait(false);
            await root.Gate.WaitAsync().ConfigureAwait(false);
            try{await DisconnectCoreAsync(root).ConfigureAwait(false);Status(root,"未连接");}
            finally{root.Gate.Release();}
        }
        finally{lock(root.ConnectionSync)root.ConnectionStops--;}
    }
    /// <summary>原生会话故障先封闭所有生产者，再由应用所有者等待收尾；保留同步根和恢复记录。</summary>
    private void ConnectSession(Runtime root)
    {
        var session=root.Session??throw new IOException("缺少原生挂载会话。");
        session.RenameRejected+=message=>{if(!IsStopping&&ReferenceEquals(root.Session,session))NotifyRenameRejected(root.Definition.Id,message);};
        // 独立任务管道可能已推进连续改名；迟到CFAPI通知只请求可靠补扫，不能回写旧路径或错误断开。
        session.NotificationRescanRequired+=()=>
        {
            if(!IsStopping&&!root.RemovalRequested&&ReferenceEquals(root.Session,session))
            {root.Uploads?.RequestFullScan();root.Pins?.RequestFull();}
        };
        session.RuntimeFailed+=error=>
        {
            lock(_lifetime)
            {
                if(IsStopping||!ReferenceEquals(root.Session,session))return;
                lock(root.ConnectionSync){root.RuntimeFailed=root.ConnectionStops==0&&!root.RemovalRequested&&!root.Definition.RemovalPending;CancelConnection(root,reason:"NativeRuntimeFailure");}
                RequestRemoteStop(root);RequestUploadStop(root);RequestCapacityStop(root);RequestPinStop(root);
                try
                {
                    var cleanup=OwnAsync(async cancel=>{await DisconnectOwnedAsync(root.Definition.Id,cancel,runtimeFailure:true).ConfigureAwait(false);bool failed;lock(root.ConnectionSync)failed=root.RuntimeFailed;if(failed)Status(root,"原生后台已断开","已停止相关任务并保留恢复记录；重新连接前先核对未确认结果。");},CancellationToken.None);
                    lock(root.ConnectionSync)root.RuntimeFailureCleanup=cleanup;Notify();
                    _ = cleanup.ContinueWith(task=>{if(task.Exception is {} failure)_logger.QueueException("原生后台","Error","故障连接收尾未完成",failure,root.Definition.Id.ToString("N"));},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                }
                catch(OperationCanceledException)when(IsStopping){}
            }
        };session.PreviewDemandReceived+=paths=>ApplyPreviewDemand(root,session,paths);
        session.DispatchFileTask=(id,entry,operation,execute,token)=>DispatchNativeReadAsync(root,session,id,entry,execute,token);
        RegisterNativeTasks(root,session);
        session.Connect();
    }
    private async Task DisconnectCoreAsync(Runtime root)
    {
        await _fileDispatcher.StopCaptureAsync(root.Definition.Id).ConfigureAwait(false);
        RequestUploadStop(root);RequestPinStop(root);var previewStop=_previews.CancelMountAsync(root.Definition.Id);var reads=root.Session?.RequestStop()??Task.CompletedTask;
        RequestCapacityStop(root);var errors=new List<Exception>();
        try{await Task.WhenAll(previewStop,reads,StopUploadsAsync(root),StopCapacityAsync(root),StopPinIntentsAsync(root)).ConfigureAwait(false);}catch(Exception error){errors.Add(error);}
        await _fileDispatcher.RemoveAsync(root.Definition.Id).ConfigureAwait(false);UnobserveUploadSource(root);var session=root.Session;var source=root.Source;root.Session=null;root.Source=null;
        try{if(session is not null)await session.DisposeAsync().ConfigureAwait(false);}catch(Exception error){errors.Add(error);}
        try{if(source is not null)await source.DisposeAsync().ConfigureAwait(false);}catch(Exception error){errors.Add(error);}
        if(root.QueueBinding is {} binding){try{await TasksQueue.RetireMountAsync(binding).ConfigureAwait(false);}catch(Exception error){errors.Add(error);}root.QueueBinding=null;}
        if(errors.Count!=0)throw new AggregateException("挂载资源清理未全部完成。",errors);
    }
    public Guid FindMount(IEnumerable<string> paths)
    {
        Guid? id=null;
        foreach(var path in paths)
        {
            var marker=MountPathGuard.ReadRoot(path)??throw new IOException("菜单目标不属于本产品挂载。");
            var root=Root(marker.MountId);MountPathGuard.ValidateOperation(root.Definition,path);
            if(id is not null && id!=marker.MountId)throw new IOException("不能混合不同挂载。");id=marker.MountId;
        }
        return id??throw new IOException("未选择文件。");
    }
    /// <summary>单文件缓存只读查询；关闭或不属于本产品时回退，不打开内容、不查询远端。</summary>
    public byte[]? GetPreview(string path,int size)
    {
        if(!CustomPreviewsAvailable||size is <16 or >256)return null;var target=PreviewTarget(path);return target is null?null:_previews.Get(target.Value.Root.Definition,target.Value.Entry,size);
    }
    public string Queue(Guid id,string operation,string[]? paths=null)
    {lock(_lifetime){ThrowIfStopping();return QueueCore(id,operation,paths);}}
    private string QueueCore(Guid id,string operation,string[]? paths)
    {
        _stop.Token.ThrowIfCancellationRequested(); var root=Root(id);
        if(operation is not("keepOnDevice" or "freeSpace" or "refresh" or "remove" or "clearAndRemove"))throw new ArgumentException("未知挂载操作。");
        if(operation=="refresh")RefreshCapacity(root);
        var removing=operation is "remove" or "clearAndRemove";
        var mode=operation=="clearAndRemove"?MountRemovalMode.ClearContents:MountRemovalMode.PreserveFiles;
        if(root.Definition.RemovalPending && (!removing || root.Definition.RemovalMode!=mode))throw new IOException("移除未完成，不能改用其他模式或执行缓存任务。");
        // 预览可能持有正文保护引用；先在根锁外取消并等待，不能进入清空后才等待oplock释放。
        var previewStop=Task.CompletedTask;
        if(removing){CancelConnection(root,removing:true);RequestRemoteStop(root);RequestUploadStop(root);RequestCapacityStop(root);RequestPinStop(root);previewStop=_previews.CancelMountAsync(id);}
        if(removing)foreach(var current in _jobs.Values.Where(job=>job.Root==id && job.Snapshot.State is "Queued" or "Running"))current.Cancel.Cancel();
        var job=new Job(Guid.NewGuid().ToString("N"),id,operation,_stop.Token);
        job.Snapshot=job.Snapshot with{ItemPath=paths is {Length:1}?Path.GetRelativePath(root.Definition.LocalPath,paths[0]):"\\"};
        _jobs[job.Snapshot.Id]=job;Save(job);
        job.Work=Task.Run(async()=>
        {
            using var queueContext=TasksQueue.EnterTask(id,job.Snapshot.Id,MountingPriority.User,operation);
            var held=false;var changedPaths=new List<string>();
            try
            {
                await EnsureInspectionAsync(root).ConfigureAwait(false);
                if(removing)
                {
                    await Task.WhenAll(previewStop,StopRemoteAsync(root),StopUploadsAsync(root),StopCapacityAsync(root),StopPinIntentsAsync(root),root.ConnectionFinished).ConfigureAwait(false);
                    // 删除／注销前先关闭目录句柄并持久交付捕获事件，监听不得跨过根的销毁。
                    await _fileDispatcher.StopCaptureAsync(id).ConfigureAwait(false);
                }
                if(operation=="refresh"&&root.Source is IRemoteChangeSource)
                {
                    Update(job,"Running");
                    await root.Gate.WaitAsync(job.Cancel.Token).ConfigureAwait(false);
                    try{if(root.Session is null)throw new IOException("挂载尚未连接。");Reconcile(root);ScanLocalUploadsCore(root,job.Cancel.Token);}
                    finally{root.Gate.Release();}
                    var before=RemotePendingFor(root).ToDictionary(x=>x.Id,x=>x.State);
                    await DetectRemoteNowAsync(root,true,job.Cancel.Token).ConfigureAwait(false);
                    var applied=RemotePendingFor(root).Where(x=>x.State!="Superseded"&&!(x.State=="Canceled"&&x.MetadataOnly)&&(x.State!="Completed"||!before.TryGetValue(x.Id,out var old)||old!="Completed")).ToArray();
                    job.Snapshot=job.Snapshot with{Total=applied.Length};
                    foreach(var change in applied)Result(job,change.Path,change.State=="Completed"?"Completed":"Conflict",change.Error??(change.Entry is null?"远端删除已清理。":"远端版本已应用。"));
                    Update(job,applied.Any(x=>x.State!="Completed")?"PartialFailure":"Completed");return;
                }
                await root.Gate.WaitAsync(job.Cancel.Token).ConfigureAwait(false);held=true;
                if(!_roots.ContainsKey(id))throw new IOException("挂载已被移除。");
                if(root.Definition.RemovalPending && (!removing || root.Definition.RemovalMode!=mode))throw new IOException("移除模式已固定，请继续原来的移除任务。");
                Update(job,"Running");
                if(operation=="clearAndRemove") { Update(job,await ClearAndRemoveAsync(root,job).ConfigureAwait(false)?"Completed":"PartialFailure");return; }
                if(operation=="remove") { await RemoveAsync(root,job).ConfigureAwait(false);Update(job,"Completed");return; }
                if(root.Session is null)throw new IOException("挂载尚未连接，请在挂载页连接后重试。");
                Reconcile(root);
                if(operation=="refresh")
                {
                    ScanLocalUploadsCore(root,job.Cancel.Token);var results=await PopulateAsync(root,"",job.Cancel.Token).ConfigureAwait(false);
                    job.Snapshot=job.Snapshot with{Total=results.Count};foreach(var result in results)Result(job,result.Path,result.Status,result.Message);
                    Status(root,"已连接",RefreshSummary(results));Update(job,results.Any(item=>item.Status!="Completed")?"PartialFailure":"Completed");return;
                }
                var targets=paths??[root.Definition.LocalPath];
                foreach(var target in targets)MountPathGuard.ValidateOperation(root.Definition,target);
                var targetScopes=targets.Select(target=>Path.GetRelativePath(root.Definition.LocalPath,target).Replace('\\','/')).Select(path=>path=="."?"":path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var selected=_repository.Entries(id).Where(entry=>!entry.Deleted&&TaskStateSnapshot.Ancestors(entry.LocalPath).Any(targetScopes.Contains)).ToArray();
                var selectedPaths=selected.Select(entry=>RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var localOnly=targets.SelectMany(target=>Directory.Exists(target)?Walk(root.Definition,target):new[]{target})
                    .Where(path=>!path.Equals(root.Definition.LocalPath,StringComparison.OrdinalIgnoreCase)&&!selectedPaths.Contains(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                job.Snapshot=job.Snapshot with{Total=selected.Length+localOnly.Length};Save(job);
                foreach(var local in localOnly)Result(job,local,"Skipped",FileExclusionRules.Matches(root.Definition.ExclusionRules,local,Directory.Exists(local))?
                    "文件已排除；固定或释放空间前请先解除对应规则。":"普通本地文件或未知项，已保留。");
                foreach(var entry in selected)
                {
                    job.Cancel.Token.ThrowIfCancellationRequested(); var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);
                    try
                    {
                        MountPathGuard.ValidateOperation(root.Definition,path);
                        if(FileExclusionRules.Matches(root.Definition.ExclusionRules,entry.LocalPath,entry.IsDirectory))
                        {Result(job,path,"Skipped","文件已排除；固定或释放空间前请先解除对应规则。");continue;}
                        var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                        if(info is null||marker?.MountId!=id||marker.EntryId!=entry.Id||operation=="keepOnDevice"&&(_repository.HasPendingRemote(id,entry.SourcePath)||entry.RemotePresence==RemotePresence.Deleted))
                        {Result(job,path,"Skipped","本地修改、普通文件、活动项或状态不明，已保留。");continue;}
                        job.Snapshot=job.Snapshot with{ItemPath=entry.LocalPath,IsDirectory=entry.IsDirectory,BytesTransferred=0,TotalBytes=entry.IsDirectory?0:new FileInfo(path).Length,DurationSeconds=0};Notify();
                        var itemStarted=System.Diagnostics.Stopwatch.GetTimestamp();
                        var session=root.Session;
                        if(operation=="keepOnDevice"){if(root.Pins is {} pins)lock(pins.Sync)pins.Retry.Remove(entry.Id);session.RequestPin(entry);}
                        else
                        {
                            root.Gate.Release();held=false;MountOperationSnapshot? release;
                            try{release=await RequestReleaseAsync(root,entry,job.Cancel.Token).ConfigureAwait(false);}
                            finally{await root.Gate.WaitAsync().ConfigureAwait(false);held=true;}
                            if(release is not null&&release.State!="Completed"){Result(job,path,release.State=="Failed"?"Failed":release.State=="Superseded"?"Skipped":"Waiting",release.Error??"释放暂缓。");continue;}
                        }
                        if(operation=="keepOnDevice"&&!entry.IsDirectory)
                        {
                            root.Gate.Release();held=false;
                            try{await _fileDispatcher.ExecuteAsync(Workflow(root,job.Snapshot.Id+":"+entry.Id,entry.LocalPath,"keepOnDevice",DateTimeOffset.UtcNow.UtcTicks),
                                cancel=>session.HydratePinnedAsync(entry,cancel),cancel=>VerifyPinnedWorkflowAsync(root,session,entry,cancel),job.Cancel.Token).ConfigureAwait(false);}
                            finally{await root.Gate.WaitAsync().ConfigureAwait(false);held=true;}
                            job.Cancel.Token.ThrowIfCancellationRequested();
                            if(!ReferenceEquals(root.Session,session))throw new IOException("下载会话已结束。");
                        }
                        changedPaths.Add(path);
                        // 原生句柄已归还，先完成缓存元数据收尾再发布任务完成；Shell通知仍在根锁外发送。
                        _previews.RebindMetadata(root.Definition,entry);
                        if(operation=="keepOnDevice")QueueLocalPreview(root,entry);
                        if(operation=="keepOnDevice"&&!entry.IsDirectory)job.Snapshot=job.Snapshot with{DurationSeconds=System.Diagnostics.Stopwatch.GetElapsedTime(itemStarted).TotalSeconds};
                        Result(job,path,"Completed","操作已完成。");
                    }
                    catch(OperationCanceledException){throw;}
                    catch(Exception error){Result(job,path,"Failed",error.Message);}
                }
                Update(job,job.Results.Any(item=>item.Status=="Waiting")?"Waiting":job.Results.Any(item=>item.Status=="Failed")?"PartialFailure":"Completed");
                if(operation=="freeSpace")RefreshReleaseParents(root);
            }
            catch(OperationCanceledException)when(job.Cancel.IsCancellationRequested){Update(job,"Canceled");}
            catch(Exception error)
            {
                if(removing)RecordRemovalFailure(id,error);
                Update(job,"Failed",error.Message);_logger.QueueException("挂载任务","Error","操作未完成",error);
            }
            finally
            {
                if(held)
                {
                    try{if(root.Definition.RemovalPending && _roots.ContainsKey(id))RemovalStatus(root);}
                    catch(Exception error){root.Status="移除未完成";root.Detail="恢复记录读取失败："+error.Message;_logger.QueueException("挂载恢复","Warning","移除恢复记录读取失败，未自动重放操作",error,root.Definition.Id.ToString("N"));}
                    finally{root.Gate.Release();}
                }
                foreach(var path in changedPaths)NotifyFileState(path);
                if(removing)lock(root.ConnectionSync)
                { root.RemovalRequests--;root.RemovalRequested=root.RemovalRequests>0 || root.Definition.RemovalPending; }
                Notify();
            }
        });
        return job.Snapshot.Id;
    }
    /// <summary>保存不可切换的移除意图；旧版 Pending 默认按保留文件恢复。</summary>
    private void MarkRemoval(Runtime root,MountRemovalMode mode)
    {
        if(root.Definition.RemovalPending && root.Definition.RemovalMode!=mode)throw new IOException("不能切换已开始的移除模式。");
        root.Definition=root.Definition with{RemovalPending=true,RemovalMode=mode};_repository.SaveDefinition(root.Definition);
    }
    private void MarkUnregistering(Runtime root)
    { root.Definition=root.Definition with{RemovalStage=MountRemovalStage.Unregistering};_repository.SaveDefinition(root.Definition); }
    private void Forget(Runtime root)
    {
        foreach(var upload in _repository.Uploads(root.Definition.Id))DeleteUploadSnapshot(upload.SnapshotPath,forget:true);
        foreach(var pending in RemotePendingFor(root))DeleteRemoteSnapshot(pending.SnapshotPath);
        new CredentialStore(_repository.DataDirectory).Delete(root.Definition.Id);FileSourceFactory.DeleteAdditionalCredentials(root.Definition,_repository.DataDirectory);_repository.DeleteEntries(root.Definition.Id);_repository.DeleteDefinition(root.Definition.Id);
        _roots.TryRemove(root.Definition.Id,out _);Notify();
    }
    /// <summary>先保存精确备份与根身份；只有未完成内容处理的日志才允许清空，原生注销后只继续注册收尾。</summary>
    private MountRemovalJournal PrepareRemoval(Runtime root,MountRemovalMode mode)
    {
        var mount=root.Definition;var candidate=new OrphanedMountCandidate(mount.Id,mount.LocalPath,mount.RootIdentity??"",CloudRootRegistration.Id(mount),false);
        MountRemovalCompletion.VerifyLocal(candidate);var native=CloudFilesNative.RootIdentity(mount.LocalPath,true);
        if(native is not null)MountRemovalCompletion.ValidateNative(candidate);
        else if(MountPathGuard.ContainsCloudPlaceholders(mount.LocalPath))throw new IOException("注册已丢失且残留云占位，保留现场。");
        var completion=RemovalCompletion();var journal=completion.Begin(candidate with{Registered=native is not null},mode);MarkRemoval(root,mode);
        if(!journal.ContentCompleted&&mount.RemovalPending&&mount.RemovalStage==MountRemovalStage.Unregistering)journal=completion.ContentComplete(journal);
        return journal;
    }
    private async Task CompleteRemovalAsync(Runtime root,MountRemovalJournal journal,CancellationToken token)
    {
        MarkUnregistering(root);await DisconnectCoreAsync(root).ConfigureAwait(false);
        RemovalCompletion().Finish(journal,token);await _previews.RemoveMountAsync(root.Definition).ConfigureAwait(false);Forget(root);
    }
    /// <summary>清空本地目录，不读取或删除远端；失败／取消保留已删结果，内容完成以后不重复清空。</summary>
    private async Task<bool> ClearAndRemoveAsync(Runtime root,Job job)
    {
        var journal=PrepareRemoval(root,MountRemovalMode.ClearContents);
        if(journal.ContentCompleted){await CompleteRemovalAsync(root,journal,job.Cancel.Token).ConfigureAwait(false);return true;}
        var registered=journal.Root.Registered;Status(root,"清空中");
        if(registered&&root.Session is null){root.Session=new CloudMountSession(root.Definition,null,_repository,_logger,blockHydration:true);ConnectSession(root);}
        if(root.Session is not null)await root.Session.BeginClearAsync().ConfigureAwait(false);
        job.Cancel.Token.ThrowIfCancellationRequested();
        var failures=MountContentCleaner.Clear(root.Definition,registered,job.Cancel.Token,
            count=>{job.Snapshot=job.Snapshot with{Total=job.Snapshot.Total+count};Save(job);},(path,status,message)=>Result(job,path,status,message));
        if(failures>0){RemovalCompletion().Failure(journal,new IOException(job.Results.LastOrDefault(x=>x.Status=="Failed")?.Message??"内容清空未完成。"));return false;}
        journal=RemovalCompletion().ContentComplete(journal);job.Cancel.Token.ThrowIfCancellationRequested();
        await CompleteRemovalAsync(root,journal,job.Cancel.Token).ConfigureAwait(false);return true;
    }
    private async Task RemoveAsync(Runtime root,Job job)
    {
        var mount=root.Definition;
        var journal=PrepareRemoval(root,MountRemovalMode.PreserveFiles);
        if(journal.ContentCompleted){await CompleteRemovalAsync(root,journal,job.Cancel.Token).ConfigureAwait(false);return;}
        var registered=journal.Root.Registered;
        if(!registered)
        {
            journal=RemovalCompletion().ContentComplete(journal);
            await CompleteRemovalAsync(root,journal,job.Cancel.Token).ConfigureAwait(false);return;
        }
        MountPathGuard.ValidateOperation(root.Definition,mount.LocalPath);
        if(root.Session is null)
        {
            root.QueueBinding??=TasksQueue.StartMount(mount.Id);
            using var queueContext=TasksQueue.Enter(root.QueueBinding,new(job.Snapshot.Id,mount.Id,root.QueueBinding.Generation,MountingPriority.User,DateTimeOffset.UtcNow,"remove"));
            root.Source=await OutsideRootAsync(root,()=>_factory.CreateAsync(mount,_repository,true,job.Cancel.Token)).ConfigureAwait(false);
            if(root.Source is IMountNetworkSource network)network.BindQueue(root.QueueBinding);
            root.Session=new CloudMountSession(mount,root.Source,_repository,_logger,localContentReady:entry=>QueueLocalPreview(root,entry));ConnectSession(root);
        }
        Status(root,"移除中","完整下载后保留为普通本地目录。");Reconcile(root);
        var entries=_repository.Entries(mount.Id).Where(entry=>!entry.Deleted&&!(entry.RemotePresence==RemotePresence.Deleted&&LocalMissing(RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath)))).OrderByDescending(entry=>entry.LocalPath.Length).ToArray();
        job.Snapshot=job.Snapshot with{Total=entries.Length};Save(job);
        // 所有剩余文件完整下载成功后，才开始解除任何占位。
        foreach(var entry in entries.Where(entry=>!entry.IsDirectory))
        {
            job.Cancel.Token.ThrowIfCancellationRequested();var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);
            if(CloudFilesNative.Inspect(path) is null)continue;
            if(entry.RemotePresence==RemotePresence.Deleted)
            {if(!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length))throw new IOException("远端已删除且本地尚不完整，保留配置与现有内容。");continue;}
            using(var priority=root.Session!.PrioritizeDownload(entry.Id))
            await root.Session!.Runtime.InvokeAsync("hydrateFile",root.Session.EntryArgs(entry),token:job.Cancel.Token).ConfigureAwait(false);
            if(!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length))throw new IOException("文件尚未完整水合，停止移除。");
        }
        foreach(var entry in entries)
        {
            job.Cancel.Token.ThrowIfCancellationRequested();var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);
            var info=CloudFilesNative.Inspect(path);
            if(info is not null)
            {
                var marker=MountPathGuard.ReadEntry(info.Value.Identity);if(marker?.MountId!=mount.Id||marker.EntryId!=entry.Id)throw new IOException("占位归属不匹配，停止解除。");
                root.Session!.Runtime.Invoke("revertLocal",root.Session.EntryArgs(entry));
            }
            Result(job,path,"Completed","已保留为普通本地文件。");
        }
        job.Cancel.Token.ThrowIfCancellationRequested();
        if(MountPathGuard.ContainsCloudPlaceholders(mount.LocalPath))throw new IOException("仍有未解除的云文件，保留配置和注册以供恢复。");
        journal=RemovalCompletion().ContentComplete(journal);
        await CompleteRemovalAsync(root,journal,job.Cancel.Token).ConfigureAwait(false);
    }
    /// <summary>批量结果只累积一次；250毫秒或256项发布，终态强制刷新全部结果。</summary>
    private void Result(Job job,string path,string status,string message)
    {
        job.Results.Add(new(path,status,message));
        job.Snapshot=job.Snapshot with{Completed=job.Snapshot.Completed+1,BytesTransferred=job.Snapshot.Operation=="keepOnDevice"&&job.Snapshot.IsDirectory is false&&status=="Completed"?job.Snapshot.TotalBytes:job.Snapshot.BytesTransferred};
        var now=Environment.TickCount64;
        if(job.Results.Count-job.PublishedCount>=256||now-job.LastPublished>=250)Save(job);
    }
    /// <summary>实例级持久化屏障；仅隔离回归使用，不通过界面或IPC配置。</summary>
    internal Action<MountOperationSnapshot>? OperationSavingForTests {get;set;}
    /// <summary>终态先落库再发布；已完成结果不因取消、错误或退出而丢失。</summary>
    private void Update(Job job,string state,string? error=null)
    {
        var next=job.Snapshot with{State=state,Error=error,Results=job.Results.ToArray()};
        OperationSavingForTests?.Invoke(next);_repository.SaveOperation(next);job.Snapshot=next;Notify();
    }
    private void Save(Job job)
    {
        job.Snapshot=job.Snapshot with{Results=job.Results.ToArray()};job.LastPublished=Environment.TickCount64;job.PublishedCount=job.Results.Count;
        _repository.SaveOperation(job.Snapshot);Notify();
    }
    public MountOperationSnapshot? GetOperation(string id)=>_fileOperations.GetValueOrDefault(id)?.Snapshot??RenameIntent(id)?.Snapshot()??(_repository.Upload(id) is {} upload?UploadSnapshot(upload):_repository.FindRemotePending(id) is {} remote?DescribeOperationWait(RemoteTask(remote),remote.Path):_jobs.TryGetValue(id,out var job)?job.Snapshot:_repository.FindOperation(id));
    /// <summary>仅接受仍在排队或执行的取消请求；完成的任务不能再次取消。</summary>
    public bool Cancel(string id)
    {
        if(CancelFileOperation(id))return true;
        if(CancelExcludedRename(id) is {} renameCancel)return renameCancel;
        if(_repository.Upload(id) is not null)return CancelUpload(id);
        if(CancelRemoteChange(id))return true;
        if(!_jobs.TryGetValue(id,out var job) || job.Snapshot.State is not("Queued" or "Running"))return false;
        try { job.Cancel.Cancel();return true; }catch(ObjectDisposedException){return false;}
    }
    /// <summary>缓存是软限制，0 表示关闭自动回收；固定、修改和活动内容可使实际值超限。</summary>
    public long CacheLimitBytes { get=>_repository.CacheLimitBytes; set { if(value<0)throw new ArgumentOutOfRangeException(nameof(value));_repository.CacheLimitBytes=value; } }
    /// <summary>返回最近一次成功后台统计；需要实际核对的调用者使用GetCacheUsageAsync。</summary>
    public long CacheUsageBytes=>Interlocked.Read(ref _cacheUsageBytes);
    private async Task CacheLoopAsync()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while(await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))try{await OwnAsync(cancel=>EnforceCacheLimitCoreAsync(cancel,false),_stop.Token).ConfigureAwait(false);}catch(Exception error)when(error is not OperationCanceledException){_logger.QueueException("缓存回收","Warning","操作未完成",error);} }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    /// <summary>尽力回收整文件临时缓存及未固定的干净占位；不为了达标而解除固定。</summary>
    public Task<long> EnforceCacheLimitAsync(CancellationToken token=default)=>OwnAsync(cancel=>EnforceCacheLimitCoreAsync(cancel,true),token);
    private async Task<long> EnforceCacheLimitCoreAsync(CancellationToken token,bool full)
    {
        // UNPINNED是既有释放意图，和按容量自动回收分开；即便关闭软限额也要安全完成。
        var requested=await CompletePendingUnpinsAsync(token).ConfigureAwait(false);
        var limit=CacheLimitBytes;if(limit==0)return requested;
        var needed=await ReadCacheUsageAsync(full,token).ConfigureAwait(false)-limit;if(needed<=0)return requested;
        long freed=_previews.Trim(needed);
        if(freed<needed)freed+=WholeFileCache.Trim(Path.Combine(_repository.DataDirectory,"RangeCache"),needed-freed);
        foreach(var root in _roots.Values)
        {
            if(freed>=needed)break;
            // 扫描和上传提交也使用根锁；排队后重新核对，避免一次短扫描导致本轮缓存回收被跳过。
            await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(root.Session is null || root.Definition.RemovalPending)continue;
                Reconcile(root);
                foreach(var entry in _repository.Entries(root.Definition.Id).Where(entry=>!entry.Deleted&&!entry.Protected&&!entry.IsDirectory&&entry.RemotePresence!=RemotePresence.Deleted&&!_repository.HasPendingRemote(root.Definition.Id,entry.SourcePath)).OrderBy(entry=>root.Session.LastRead(entry.Id)))
                {
                    token.ThrowIfCancellationRequested();if(freed>=needed)break;
                    if(FileExclusionRules.Matches(root.Definition.ExclusionRules,entry.LocalPath))continue;
                    var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);
                    try
                    {
                        MountPathGuard.ValidateOperation(root.Definition,path);
                        var info=CloudFilesNative.Inspect(path);
                        if(info is null || info.Value.Info.PinState is not(0 or 2) || info.Value.Info.InSyncState!=1 || info.Value.Info.ModifiedSize!=0 || root.Session.IsActive(entry.Id))continue;
                        var marker=MountPathGuard.ReadEntry(info.Value.Identity);if(marker?.MountId!=root.Definition.Id||marker.EntryId!=entry.Id)continue;
                        await PreserveBeforeDehydrateAsync(root,entry,token).ConfigureAwait(false);
                        var released=root.Session.Runtime.Invoke("trimFile",root.Session.EntryArgs(entry));if(released.GetProperty("changed").GetBoolean()){freed+=NativeRuntimeProtocol.Read<long>(released.GetProperty("freed"));_previews.RebindMetadata(root.Definition,entry);}
                    }
                    catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException){}
                }
            }
            finally { root.Gate.Release(); }
        }
        return requested+freed;
    }
}
