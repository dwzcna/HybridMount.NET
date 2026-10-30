using System.Collections.Concurrent;
using HybridMount.Models.Mounting;
using HybridMount.Common;

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
        public bool RemovalRequested;
        public int RemovalRequests;
        public CloudMountSession? Session;
        public IFileSource? Source;
        public UploadRuntime? Uploads;
        public Action<SourceContentBaseline>? BaselineObserver;
        public Action<string>? WriteObserver;
        public string Status=definition.RemovalPending?"移除未完成":"未连接";
        public string Detail=definition.RemovalPending?"请继续已选择的移除方式；不会自动重放删除操作。":"";
    }
    private sealed class Job(string id,Guid root,string operation,CancellationToken token)
    {
        public readonly Guid Root=root;
        public readonly CancellationTokenSource Cancel=CancellationTokenSource.CreateLinkedTokenSource(token);
        public MountOperationSnapshot Snapshot=new(id,operation,"Queued",0,0,[],null);
        public Task Work=Task.CompletedTask;
    }
    private readonly ExePreviewCache _previews;
    private readonly MountRepository _repository;
    private readonly LoggerService _logger;
    private readonly IFileSourceFactory _factory;
    private Task? _maintenance;
    private readonly ConcurrentDictionary<Guid,Runtime> _roots=new();
    private readonly ConcurrentDictionary<string,Job> _jobs=new();
    private readonly CancellationTokenSource _stop=new();
    private Task? _dispose;
    public event Action? Changed;
    public MountManager(MountRepository repository,LoggerService logger,IFileSourceFactory? factory=null)
    {
        _previews=new ExePreviewCache(Path.Combine(repository.DataDirectory,"PreviewCache"),EnterPreviewAsync,value=>{if(HydrationProcess.DiagnosticsEnabled)logger.Queue("预览诊断","Information",value);});_repository=repository; _logger=logger; _factory=factory??new FileSourceFactory();
        foreach(var definition in repository.LoadDefinitions())
        {
            var root=new Runtime(definition);_roots[definition.Id]=root;
            try { InspectRegistration(root); }
            catch(Exception error) { root.Status="需要恢复";root.Detail=error.Message; }
        }
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
            !_repository.Entries(mount.Id).Any(current=>current.Id==entry.Id&&current.Version==entry.Version&&current.LocalPath==entry.LocalPath&&!current.Deleted&&!current.Protected&&current.RemotePresence!=RemotePresence.Deleted))
        {root.Gate.Release();return null;}
        return new PreviewLease(root.Gate);
    }
    public IReadOnlyList<MountSnapshot> Mounts => _roots.Values.Select(root=>new MountSnapshot(root.Definition,root.Status,root.Detail)).OrderBy(item=>item.Definition.Name).ToArray();
    public IReadOnlyList<MountOperationSnapshot> Operations
    {
        get
        {
            var uploads=_repository.Uploads().ToDictionary(x=>x.Id);var history=_repository.LoadOperations();
            return uploads.Values.Where(x=>x.State is not("Completed" or "Canceled" or "Ignored")).Select(UploadSnapshot)
                .Concat(history.Where(x=>!uploads.TryGetValue(x.Id,out var current)||current.State is "Completed" or "Canceled" or "Ignored")
                .Select(item=>uploads.TryGetValue(item.Id,out var upload)?UploadSnapshot(upload):_jobs.TryGetValue(item.Id,out var job)?job.Snapshot:item)).ToArray();
        }
    }
    private void Notify() { if(Changed is not {} handlers)return; foreach(Action handler in handlers.GetInvocationList())try{handler();}catch(Exception error){_logger.Queue("挂载界面","Warning",error.Message);} }
    private void Status(Runtime root,string status,string detail="") { root.Status=status; root.Detail=detail; Notify(); }
    private Task? _startup;
    public Task StartAsync() { _maintenance??=Task.Run(CacheLoopAsync);return _startup??=Task.Run(InitializeAsync); }
    private async Task InitializeAsync()
    {
        foreach(var root in _roots.Values.Where(root=>root.Definition.AutoConnect && !root.Definition.RemovalPending))
            try { if(_factory is FileSourceFactory && root.Definition.Source is null) { Status(root,"需要迁出","旧版模拟源保留；请选择下载并移除或清空。");continue; } await ConnectAsync(root.Definition.Id,_stop.Token).ConfigureAwait(false); } catch(Exception error) { _logger.Queue("恢复挂载","Error",error.Message); }
    }
    public async Task<MountDefinition> AddAsync(string name,string directory,bool autoConnect,CancellationToken token=default,FileSourceConfiguration? source=null,SourceCredentials? credentials=null,bool autoUpload=true)
    {
        if(_factory is FileSourceFactory)
        {
            if(source is null || source.Kind is FileSourceKind.LegacyMock or FileSourceKind.Smb or FileSourceKind.Nfs)throw new NotSupportedException("请选择已实现的网络协议。");
            FileSourceFactory.Endpoint(source);
            if(source.Kind==FileSourceKind.S3)
            {
                S3FileSource.ValidateOptions(source);
                if(!source.UseDefaultCredentials&&(string.IsNullOrWhiteSpace(credentials?.AccessKey)||string.IsNullOrWhiteSpace(credentials?.SecretKey)))throw new ArgumentException("请填写 S3 访问密钥或选择默认凭据链。");
            }
            if(source.Kind==FileSourceKind.WebDav&&credentials?.UserName.Contains(':')==true)throw new ArgumentException("WebDAV 账号不能包含冒号。");
        }
        if(string.IsNullOrWhiteSpace(name))throw new ArgumentException("请输入挂载名称。");
        var root=MountPathGuard.NormalizeRoot(directory);
        if(MountPathGuard.IsUnder(root,_repository.DataDirectory) || MountPathGuard.IsUnder(_repository.DataDirectory,root) || MountPathGuard.IsUnder(root,AppContext.BaseDirectory) || MountPathGuard.IsUnder(AppContext.BaseDirectory,root))throw new IOException("不能将程序或自身数据目录用作挂载根。");
        if(Directory.EnumerateFileSystemEntries(root).Any())throw new IOException("新挂载必须选择空目录。");
        if(_roots.Values.Any(item=>MountPathGuard.IsUnder(item.Definition.LocalPath,root)||MountPathGuard.IsUnder(root,item.Definition.LocalPath)))throw new IOException("挂载根不能重叠。");
        var definition=new MountDefinition(Guid.NewGuid(),name.Trim(),root,autoConnect,RootIdentity:MountPathGuard.FileIdentity(root),Source:source,RegistrationStage:MountRegistrationStage.NotRegistered,AutoUploadEnabled:autoUpload,UploadInitialReviewCompleted:true);
        var secretStore=new CredentialStore(_repository.DataDirectory);
        if(credentials is not null)secretStore.Save(definition.Id,credentials);
        try { _repository.SaveDefinition(definition); }
        catch { secretStore.Delete(definition.Id);throw; }
        _roots[definition.Id]=new(definition); Notify();
        await ConnectAsync(definition.Id,token).ConfigureAwait(false); return Root(definition.Id).Definition;
    }
    public SourceCredentials GetCredentials(Guid id) { Root(id);return new CredentialStore(_repository.DataDirectory).Load(id); }
    /// <summary>只更换凭据，不能将已有占位悄悄重绑定到另一数据源。</summary>
    public async Task UpdateCredentialsAsync(Guid id,SourceCredentials credentials,CancellationToken token=default,FileSourceConfiguration? settings=null)
    {
        var root=Root(id);RequestUploadStop(root);await StopUploadsAsync(root).ConfigureAwait(false);await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(root.Definition.RemovalPending || root.RemovalRequested || !_roots.ContainsKey(id))throw new IOException("移除过程中不能更换凭据。");
            var effective=settings??root.Definition.Source??throw new IOException("旧版挂载不能更换协议。");
            FileSourceFactory.Endpoint(effective);
            if(effective.Kind==FileSourceKind.WebDav && credentials.UserName.Contains(':'))throw new ArgumentException("WebDAV 账号不能包含冒号。");
            if(effective.Kind==FileSourceKind.S3)
            {
                S3FileSource.ValidateOptions(effective);
                if(!effective.UseDefaultCredentials && (string.IsNullOrWhiteSpace(credentials.AccessKey)||string.IsNullOrWhiteSpace(credentials.SecretKey)))
                    throw new ArgumentException("请填写 S3 访问密钥或选择默认凭据链。");
            }
            if(settings is not null)
            {
                var previous=root.Definition.Source??throw new IOException("旧版挂载不能更换协议。");
                if(previous.Kind!=settings.Kind || (!CanEditRemoteRoot(id) && (previous.Endpoint!=settings.Endpoint||previous.Bucket!=settings.Bucket||previous.Prefix!=settings.Prefix||previous.AllowHttp!=settings.AllowHttp)))
                    throw new IOException("已有挂载不能更换远端根，请新建挂载。");
                FileSourceFactory.Endpoint(settings);
                if(settings.Kind==FileSourceKind.S3)S3FileSource.ValidateOptions(settings);
                root.Definition=root.Definition with{Source=settings};_repository.SaveDefinition(root.Definition);
            }
            new CredentialStore(_repository.DataDirectory).Save(id,credentials);
            await DisconnectCoreAsync(root).ConfigureAwait(false);await ConnectCoreAsync(root,token).ConfigureAwait(false);
        }
        finally { if(root.Session is not null)StartUploads(root);root.Gate.Release(); }
    }
    /// <summary>只有实际无注册、无云占位、无物化索引的从未注册条目可改变远端根。</summary>
    public bool CanEditRemoteRoot(Guid id)
    {
        var root=Root(id);
        if(root.Definition.RemovalPending || root.Definition.RegistrationStage!=MountRegistrationStage.NotRegistered)return false;
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
    private static void CancelConnection(Runtime root,bool removing=false)
    {
        lock(root.ConnectionSync)
        {
            if(removing){root.RemovalRequests++;root.RemovalRequested=true;}
            root.Connecting?.Cancel();
        }
    }
    private Runtime Root(Guid id)=>_roots.TryGetValue(id,out var root)?root:throw new IOException("挂载不存在。");
    public async Task ConnectAsync(Guid id,CancellationToken token=default)
    {
        var root=Root(id); await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try { await ConnectCoreAsync(root,token).ConfigureAwait(false); }
        finally { root.Gate.Release(); }
    }
    private async Task ConnectCoreAsync(Runtime root,CancellationToken token)
    {
        if(root.RemovalRequested || !_roots.TryGetValue(root.Definition.Id,out var current) || !ReferenceEquals(current,root))throw new IOException("挂载正在移除或已经移除。");
        if(root.Session is not null)return;
        if(root.Definition.RemovalPending)throw new IOException("上次移除尚未完成，请继续相同的移除方式。");
        using var connection=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);
        lock(root.ConnectionSync)
        {
            if(root.RemovalRequested)throw new IOException("挂载正在移除。");
            root.Connecting=connection;
        }
        token=connection.Token;
        Status(root,"连接中");
        try
        {
            var registered=InspectRegistration(root);
            if(MountPathGuard.ReadRoot(root.Definition.LocalPath) is null && _repository.Entries(root.Definition.Id).Any(entry=>entry.Materialized))throw new IOException("原同步根注册已丢失；保留数据，不能自动重新注册。");
            root.Source=await _factory.CreateAsync(root.Definition,_repository,false,token).ConfigureAwait(false);ObserveUploadSource(root);
            token.ThrowIfCancellationRequested();
            registered=InspectRegistration(root);
            if(!registered)SetRegistration(root,MountRegistrationStage.Registering);
            await CloudRootRegistration.EnsureAsync(root.Definition,token).ConfigureAwait(false);
            SetRegistration(root,MountRegistrationStage.Registered);
            token.ThrowIfCancellationRequested();
            root.Session=new CloudMountSession(root.Definition,root.Source,_repository,_logger,localContentReady:entry=>_previews.Queue(root.Definition,entry),localChanged:()=>root.Uploads?.Wake()); root.Session.Connect();
            Reconcile(root);ScanLocalUploadsCore(root,token);
            var refreshed=await PopulateAsync(root,"",token).ConfigureAwait(false); Status(root,"已连接",RefreshSummary(refreshed));
            if(refreshed.Count!=0)_repository.SaveOperation(new(Guid.NewGuid().ToString("N"),"refresh",refreshed.Any(item=>item.Status!="Completed")?"PartialFailure":"Completed",refreshed.Count,refreshed.Count,refreshed,null,root.Definition.Id));
            foreach(var cached in _repository.Entries(root.Definition.Id))_previews.Queue(root.Definition,cached);
            if(root.Source is S3FileSource s3)
            {
                try { await s3.RecoverKnownUploadsAsync(token).ConfigureAwait(false); }
                catch(Exception recoveryError)when(recoveryError is not OperationCanceledException)
                { Status(root,"已连接","已知分片恢复失败，读取仍可用；请稍后重试连接。" );_logger.Queue("S3 分片恢复","Warning",recoveryError.Message); }
            }
            StartUploads(root);
        }
        catch(Exception error)
        {
            await StopUploadsAsync(root).ConfigureAwait(false);UnobserveUploadSource(root);
            if(root.Session is not null){await root.Session.DisposeAsync().ConfigureAwait(false);root.Session=null;}
            if(root.Source is not null)await root.Source.DisposeAsync().ConfigureAwait(false);root.Source=null; Status(root,"异常",error.Message);throw;
        }
        finally { lock(root.ConnectionSync)if(ReferenceEquals(root.Connecting,connection))root.Connecting=null; }
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
        await Collect(sourcePath).ConfigureAwait(false);
        var missing=await ConfirmRemoteDeletionsAsync(root,snapshot,token).ConfigureAwait(false);var results=new List<MountItemResult>();
        foreach(var metadata in snapshot.Values.OrderBy(item=>item.Path.Count(c=>c=='/')).ThenBy(item=>item.Path,StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var entries=_repository.Entries(root.Definition.Id);var entry=entries.FirstOrDefault(item=>item.SourcePath.Equals(metadata.Path,StringComparison.OrdinalIgnoreCase));
                if(entry is not null)
                {
                    // 清理后的项重新出现在远端时，只恢复远端存在性，不解除用户本地删除／修改保护。
                    if(entry.RemotePresence==RemotePresence.Deleted&&!entry.Deleted&&LocalMissing(RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath)))
                    {_repository.CompleteRemoteCleanup(root.Definition.Id,entry);entry=entry with{Materialized=false};}
                    _repository.SetRemotePresence(root.Definition.Id,entry,RemotePresence.Present);
                }
                var parent=entries.FirstOrDefault(item=>item.SourcePath.Equals(RelativePaths.Parent(metadata.Path),StringComparison.OrdinalIgnoreCase));
                if(parent?.Deleted==true||parent?.Protected==true||entry?.Deleted==true)continue;
                var local=parent is null?metadata.Path:parent.LocalPath+"/"+Path.GetFileName(metadata.Path);
                entry??=new(Guid.NewGuid().ToString("N"),metadata.Path,local,metadata.Version,metadata.IsDirectory);
                if(entry.IsDirectory!=metadata.IsDirectory)throw new SourceConflictException("远端文件与目录类型变化，保留本地内容，请处理冲突。");
                var full=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);var absent=LocalMissing(full);
                if(absent&&!entry.Materialized)entry=entry with{Version=metadata.Version};
                _repository.SaveEntry(root.Definition.Id,entry);_repository.SetRemotePresence(root.Definition.Id,entry,RemotePresence.Present);
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
        foreach(var path in Walk(root.Definition,root.Definition.LocalPath))
        {
            var relative=Path.GetRelativePath(root.Definition.LocalPath,path).Replace('\\','/');
            var info=CloudFilesNative.Inspect(path);
            if(info is null)
            {
                var local=entries.FirstOrDefault(item=>item.LocalPath.Equals(relative,StringComparison.OrdinalIgnoreCase));
                if(local is not null){seen.Add(local.Id);_repository.SaveEntry(root.Definition.Id,local with{Protected=true,Materialized=true});}continue;
            }
            var marker=MountPathGuard.ReadEntry(info.Value.Identity);
            var entry=marker?.MountId==root.Definition.Id?entries.FirstOrDefault(item=>item.Id==marker.EntryId):null;
            if(entry is null)continue;
            seen.Add(entry.Id);
            _repository.SaveEntry(root.Definition.Id,entry with{LocalPath=relative,Materialized=true,Protected=entry.Protected||entry.LocalPath!=relative||info.Value.Info.ModifiedSize>0||info.Value.Info.InSyncState!=1});
        }
        foreach(var entry in entries.Where(entry=>entry.Materialized&&!entry.Deleted&&entry.RemotePresence!=RemotePresence.Deleted&&!seen.Contains(entry.Id))) _repository.SaveEntry(root.Definition.Id,entry with{Deleted=true,Protected=true});
    }
    public async Task DisconnectAsync(Guid id,CancellationToken token=default)
    {
        var root=Root(id); CancelConnection(root);RequestUploadStop(root);await StopUploadsAsync(root).ConfigureAwait(false);foreach(var job in _jobs.Values.Where(job=>job.Root==id))job.Cancel.Cancel();
        await root.Gate.WaitAsync(token).ConfigureAwait(false);
        try { await DisconnectCoreAsync(root).ConfigureAwait(false); Status(root,"未连接"); }
        finally { root.Gate.Release(); }
    }
    private static async Task DisconnectCoreAsync(Runtime root)
    {
        await StopUploadsAsync(root).ConfigureAwait(false);UnobserveUploadSource(root);
        if(root.Session is not null){await root.Session.DisposeAsync().ConfigureAwait(false);root.Session=null;}
        if(root.Source is not null)await root.Source.DisposeAsync().ConfigureAwait(false);root.Source=null;
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
    /// <summary>单个本产品 EXE 的缓存查询；只读索引和本地身份，不查询远端、不提交任务。</summary>
    public byte[]? GetPreview(string path,int size)
    {
        if(size is <16 or >256)return null;
        var id=FindMount([path]);var root=Root(id);
        if(root.RemovalRequested||root.Definition.RemovalPending)return null;
        var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        var entry=_repository.Entries(id).FirstOrDefault(entry=>entry.Id==marker?.EntryId&&entry.Materialized&&!entry.Deleted);
        if(marker?.MountId!=id||entry is null||info!.Value.Info.ModifiedSize!=0||info.Value.Info.InSyncState!=1)return null;
        return _previews.Get(root.Definition,entry,size);
    }
    public string Queue(Guid id,string operation,string[]? paths=null)
    {
        _stop.Token.ThrowIfCancellationRequested(); var root=Root(id);
        if(operation is not("keepOnDevice" or "freeSpace" or "refresh" or "remove" or "clearAndRemove"))throw new ArgumentException("未知挂载操作。");
        var removing=operation is "remove" or "clearAndRemove";
        var mode=operation=="clearAndRemove"?MountRemovalMode.ClearContents:MountRemovalMode.PreserveFiles;
        if(root.Definition.RemovalPending && (!removing || root.Definition.RemovalMode!=mode))throw new IOException("移除未完成，不能改用其他模式或执行缓存任务。");
        if(removing){CancelConnection(root,removing:true);RequestUploadStop(root);}
        if(removing)foreach(var current in _jobs.Values.Where(job=>job.Root==id && job.Snapshot.State is "Queued" or "Running"))current.Cancel.Cancel();
        var job=new Job(Guid.NewGuid().ToString("N"),id,operation,_stop.Token);
        _jobs[job.Snapshot.Id]=job;Save(job);
        job.Work=Task.Run(async()=>
        {
            var held=false;
            try
            {
                if(removing)await StopUploadsAsync(root).ConfigureAwait(false);
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
                var selected=_repository.Entries(id).Where(entry=>!entry.Deleted&&targets.Any(target=>MountPathGuard.IsUnder(target,RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath)))).ToArray();
                var localOnly=targets.SelectMany(target=>Directory.Exists(target)?Walk(root.Definition,target):new[]{target})
                    .Where(path=>!path.Equals(root.Definition.LocalPath,StringComparison.OrdinalIgnoreCase)&&!selected.Any(entry=>RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath).Equals(path,StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                job.Snapshot=job.Snapshot with{Total=selected.Length+localOnly.Length};Save(job);
                foreach(var local in localOnly)Result(job,local,"Skipped","普通本地文件或未知项，已保留。");
                foreach(var entry in selected)
                {
                    job.Cancel.Token.ThrowIfCancellationRequested(); var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);
                    try
                    {
                        MountPathGuard.ValidateOperation(root.Definition,path);
                        var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
                        if(info is null || entry.RemotePresence==RemotePresence.Deleted || marker?.MountId!=id || marker.EntryId!=entry.Id || (operation=="freeSpace"&&(entry.Protected||info.Value.Info.ModifiedSize>0||info.Value.Info.InSyncState!=1||root.Session.IsActive(entry.Id))))
                        {Result(job,path,"Skipped","本地修改、普通文件、活动项或状态不明，已保留。");continue;}
                        using var priority=operation=="keepOnDevice"?root.Session.PrioritizeDownload(entry.Id):null;
                        using(var handle=CloudFilesNative.Open(path,operation=="freeSpace"))
                        {
                        if(operation=="keepOnDevice")
                        {CloudFilesNative.Check(CloudFilesNative.CfSetPinState(handle,1,0,0));if(!entry.IsDirectory)await CloudFilesNative.HydrateAsync(handle,job.Cancel.Token).ConfigureAwait(false);}
                        else
                        {CloudFilesNative.Check(CloudFilesNative.CfSetPinState(handle,2,0,0));if(!entry.IsDirectory)CloudFilesNative.Check(CloudFilesNative.CfDehydratePlaceholder(handle,0,-1,0,0));}
                        }
                        if(operation=="keepOnDevice")_previews.Queue(root.Definition,entry);
                        Result(job,path,"Completed","操作已完成。");
                    }
                    catch(OperationCanceledException){throw;}
                    catch(Exception error){Result(job,path,"Failed",error.Message);}
                }
                Update(job,job.Snapshot.Results.Any(item=>item.Status=="Failed")?"PartialFailure":"Completed");
            }
            catch(OperationCanceledException){Update(job,"Canceled");}
            catch(Exception error){Update(job,"Failed",error.Message);_logger.Queue("挂载任务","Error",error.Message);}
            finally
            {
                if(held)
                {
                    if(root.Definition.RemovalPending && _roots.ContainsKey(id))Status(root,"移除未完成",root.Definition.RemovalMode==MountRemovalMode.ClearContents?"清空尚未完成；已删内容不会恢复，请重试清空并移除。":"请继续下载并移除，以保留本地文件。");
                    root.Gate.Release();
                }
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
        foreach(var upload in _repository.Uploads(root.Definition.Id))DeleteUploadSnapshot(upload.SnapshotPath);
        new CredentialStore(_repository.DataDirectory).Delete(root.Definition.Id);_repository.DeleteEntries(root.Definition.Id);_repository.DeleteDefinition(root.Definition.Id);
        _roots.TryRemove(root.Definition.Id,out _);Notify();
    }
    /// <summary>清空本地目录；保留 CFAPI 删除确认，不读取或删除源内容。失败／取消保留意图供显式重试。</summary>
    private async Task<bool> ClearAndRemoveAsync(Runtime root,Job job)
    {
        var mount=root.Definition;
        var registered=InspectRegistration(root);
        if(registered)MountPathGuard.ValidateOperation(mount,mount.LocalPath);
        MarkRemoval(root,MountRemovalMode.ClearContents);Status(root,"清空中","删除全部本地内容，保留空根目录及文件源内容。");
        if(registered && root.Session is null)
        {
            // 离线清空不依赖模拟源数据库或内容文件，不播种、不下载。
            root.Session=new CloudMountSession(root.Definition,null,_repository,_logger,blockHydration:true);root.Session.Connect();
        }
        if(root.Session is not null)await root.Session.BeginClearAsync().ConfigureAwait(false);
        job.Cancel.Token.ThrowIfCancellationRequested();
        var failures=MountContentCleaner.Clear(root.Definition,registered,job.Cancel.Token,
            count=>{job.Snapshot=job.Snapshot with{Total=job.Snapshot.Total+count};Save(job);},
            (path,status,message)=>Result(job,path,status,message));
        if(failures>0)return false;
        if(!MountContentCleaner.IsEmpty(root.Definition))throw new IOException("目录出现新内容，保留配置以供重试。");
        job.Cancel.Token.ThrowIfCancellationRequested();MarkUnregistering(root);
        await DisconnectCoreAsync(root).ConfigureAwait(false);
        if(registered)CloudRootRegistration.Unregister(root.Definition);
        if(!MountContentCleaner.IsEmpty(root.Definition))throw new IOException("注销后目录出现新内容，保留清空状态以供重试。");
        Forget(root);return true;
    }
    private async Task RemoveAsync(Runtime root,Job job)
    {
        var mount=root.Definition;
        var registered=InspectRegistration(root);
        MarkRemoval(root,MountRemovalMode.PreserveFiles);
        if(!registered)
        {
            job.Cancel.Token.ThrowIfCancellationRequested();
            await DisconnectCoreAsync(root).ConfigureAwait(false);Forget(root);return;
        }
        MountPathGuard.ValidateOperation(root.Definition,mount.LocalPath);
        if(root.Session is null)
        {
            root.Source=await _factory.CreateAsync(mount,_repository,true,job.Cancel.Token).ConfigureAwait(false);
            root.Session=new CloudMountSession(mount,root.Source,_repository,_logger,localContentReady:entry=>_previews.Queue(root.Definition,entry));root.Session.Connect();
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
            using(var handle=CloudFilesNative.Open(path))await CloudFilesNative.HydrateAsync(handle,job.Cancel.Token).ConfigureAwait(false);
            if(!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length))throw new IOException("文件尚未完整水合，停止移除。");
        }
        foreach(var entry in entries)
        {
            job.Cancel.Token.ThrowIfCancellationRequested();var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);
            var info=CloudFilesNative.Inspect(path);
            if(info is not null)
            {
                var marker=MountPathGuard.ReadEntry(info.Value.Identity);if(marker?.MountId!=mount.Id||marker.EntryId!=entry.Id)throw new IOException("占位归属不匹配，停止解除。");
                using var handle=CloudFilesNative.Open(path,true);CloudFilesNative.Check(CloudFilesNative.CfRevertPlaceholder(handle,0,0));
            }
            Result(job,path,"Completed","已保留为普通本地文件。");
        }
        job.Cancel.Token.ThrowIfCancellationRequested();
        if(MountPathGuard.ContainsCloudPlaceholders(mount.LocalPath))throw new IOException("仍有未解除的云文件，保留配置和注册以供恢复。");
        MarkUnregistering(root);await DisconnectCoreAsync(root).ConfigureAwait(false);
        CloudRootRegistration.Unregister(root.Definition);Forget(root);
    }
    private void Result(Job job,string path,string status,string message)
    {job.Snapshot=job.Snapshot with{Completed=job.Snapshot.Completed+1,Results=job.Snapshot.Results.Append(new(path,status,message)).ToArray()};Save(job);}
    private void Update(Job job,string state,string? error=null){job.Snapshot=job.Snapshot with{State=state,Error=error};Save(job);}
    private void Save(Job job){_repository.SaveOperation(job.Snapshot);Notify();}
    public MountOperationSnapshot? GetOperation(string id)=>_repository.Upload(id) is {} upload?UploadSnapshot(upload):_jobs.TryGetValue(id,out var job)?job.Snapshot:_repository.LoadOperations().FirstOrDefault(item=>item.Id==id);
    /// <summary>仅接受仍在排队或执行的取消请求；完成的任务不能再次取消。</summary>
    public bool Cancel(string id)
    {
        if(_repository.Upload(id) is not null)return CancelUpload(id);
        if(!_jobs.TryGetValue(id,out var job) || job.Snapshot.State is not("Queued" or "Running"))return false;
        try { job.Cancel.Cancel();return true; }catch(ObjectDisposedException){return false;}
    }
    /// <summary>缓存是软限制，0 表示关闭自动回收；固定、修改和活动内容可使实际值超限。</summary>
    public long CacheLimitBytes { get=>_repository.CacheLimitBytes; set { if(value<0)throw new ArgumentOutOfRangeException(nameof(value));_repository.CacheLimitBytes=value; } }
    public long CacheUsageBytes
    {
        get
        {
            long bytes=_previews.UsageBytes;
            var staging=Path.Combine(_repository.DataDirectory,"UploadStaging");
            if(Directory.Exists(staging))foreach(var file in new DirectoryInfo(staging).EnumerateFiles("*",SearchOption.AllDirectories))try{bytes+=file.Length;}catch(IOException){}
            var cache=Path.Combine(_repository.DataDirectory,"RangeCache");
            if(Directory.Exists(cache))foreach(var file in new DirectoryInfo(cache).EnumerateFiles("*",SearchOption.AllDirectories))try{bytes+=file.Length;}catch(IOException){}
            foreach(var root in _roots.Values)
                foreach(var entry in _repository.Entries(root.Definition.Id).Where(entry=>entry.Materialized&&!entry.Deleted&&!entry.IsDirectory))
                    try { bytes+=CloudFilesNative.Inspect(RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath))?.Info.OnDiskSize??0; }catch(IOException){}catch(System.ComponentModel.Win32Exception){}
            return bytes;
        }
    }
    private async Task CacheLoopAsync()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while(await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))try{await EnforceCacheLimitAsync(_stop.Token).ConfigureAwait(false);}catch(Exception error)when(error is not OperationCanceledException){_logger.Queue("缓存回收","Warning",error.Message);} }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    /// <summary>尽力回收整文件临时缓存及未固定的干净占位；不为了达标而解除固定。</summary>
    public async Task<long> EnforceCacheLimitAsync(CancellationToken token=default)
    {
        var limit=CacheLimitBytes;if(limit==0)return 0;
        var needed=CacheUsageBytes-limit;if(needed<=0)return 0;
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
                foreach(var entry in _repository.Entries(root.Definition.Id).Where(entry=>!entry.Deleted&&!entry.Protected&&!entry.IsDirectory&&entry.RemotePresence!=RemotePresence.Deleted).OrderBy(entry=>root.Session.LastRead(entry.Id)))
                {
                    token.ThrowIfCancellationRequested();if(freed>=needed)break;
                    var path=RelativePaths.ToLocal(root.Definition.LocalPath,entry.LocalPath);
                    try
                    {
                        MountPathGuard.ValidateOperation(root.Definition,path);
                        var info=CloudFilesNative.Inspect(path);
                        if(info is null || info.Value.Info.PinState is not(0 or 2) || info.Value.Info.InSyncState!=1 || info.Value.Info.ModifiedSize!=0 || root.Session.IsActive(entry.Id))continue;
                        var marker=MountPathGuard.ReadEntry(info.Value.Identity);if(marker?.MountId!=root.Definition.Id||marker.EntryId!=entry.Id)continue;
                        using var handle=CloudFilesNative.Open(path,true);
                        var current=CloudFilesNative.Inspect(path);
                        if(current?.Info.InSyncState!=1 || current?.Info.ModifiedSize!=0 || current?.Info.PinState is not(0 or 2))continue;
                        CloudFilesNative.Check(CloudFilesNative.CfDehydratePlaceholder(handle,0,-1,1,0));freed+=info.Value.Info.OnDiskSize;
                    }
                    catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException){}
                }
            }
            finally { root.Gate.Release(); }
        }
        return freed;
    }
    public ValueTask DisposeAsync()=>new(_dispose??=StopAsync());
    private async Task StopAsync()
    {
        foreach(var root in _roots.Values)RequestUploadStop(root);
        await _stop.CancelAsync().ConfigureAwait(false);await Task.WhenAll(_roots.Values.Select(StopUploadsAsync)).ConfigureAwait(false);if(_startup is not null)await _startup.ConfigureAwait(false);await Task.WhenAll(_jobs.Values.Select(job=>job.Work)).ConfigureAwait(false);
        if(_maintenance is not null)await _maintenance.ConfigureAwait(false);
        foreach(var root in _roots.Values){await root.Gate.WaitAsync().ConfigureAwait(false);try{await DisconnectCoreAsync(root).ConfigureAwait(false);}finally{root.Gate.Release();}}
        await _previews.DisposeAsync().ConfigureAwait(false);foreach(var job in _jobs.Values)job.Cancel.Dispose();_stop.Dispose();
    }
}
