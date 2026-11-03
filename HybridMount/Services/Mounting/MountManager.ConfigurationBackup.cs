using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly SemaphoreSlim _configurationGate = new(1, 1);

    /// <summary>按根锁捕获一致配置与 DPAPI 凭据，后台加密并原子写入；不探测远端。</summary>
    public Task ExportConfigurationsAsync(IReadOnlyList<Guid> ids, string password, string path, CancellationToken token = default)
    {
        ids = ids.ToArray();
        return OwnAsync(cancel => Task.Run(async () =>
        {
            if (ids.Count is < 1 or > MountBackupCodec.MaximumItems || ids.Distinct().Count() != ids.Count) throw new ArgumentException("请选择要导出的挂载。");
            var items = new List<MountBackupItem>();
            foreach (var id in ids)
            {
                var root = Root(id); await root.Gate.WaitAsync(cancel).ConfigureAwait(false);
                try
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!_roots.ContainsKey(id) || root.RemovalRequested || root.Definition.RemovalPending) throw new IOException("选中的挂载正在移除，请重新选择。");
                    var definition = root.Definition;
                    if (definition.Source is not { Kind: FileSourceKind.WebDav or FileSourceKind.S3 } source) throw new NotSupportedException("仅支持备份 WebDAV 和 S3 挂载。");
                    var credentials = source.Kind == FileSourceKind.S3 && source.UseDefaultCredentials ? new SourceCredentials() : new CredentialStore(_repository.DataDirectory).Load(id);
                    items.Add(new(definition.Name, definition.LocalPath, source, credentials, definition.AutoConnect, definition.ThumbnailsEnabled, definition.RemoteAutoDetectEnabled, definition.RemoteIncrementalSeconds, definition.RemoteFullSeconds,definition.Sqs,definition.Sqs?.CredentialMode==SqsCredentialMode.SeparateKeys?SqsStore().Load(id):null));
                }
                finally { root.Gate.Release(); }
            }
            var bytes = MountBackupCodec.Encode(new(3, DateTimeOffset.UtcNow, items), password, cancel);
            await MountBackupCodec.WriteAtomicAsync(path, bytes, cancel, (temporary, destination) =>
            {
                lock (_lifetime) { ThrowIfStopping(); cancel.ThrowIfCancellationRequested(); File.Move(temporary, destination, true); }
            }).ConfigureAwait(false);
        }, cancel), token);
    }

    /// <summary>只读解密；宿主退出会等待有界读取与密码派生结束，错误不输出秘密内容。</summary>
    public Task<MountConfigurationBackup> ReadConfigurationBackupAsync(string path, string password, CancellationToken token = default)
        => OwnAsync(cancel => Task.Run(() => MountBackupCodec.ReadAsync(path, password, cancel), cancel), token);

    /// <summary>逐项预览目录与连接参数；选中的有效项目参与批内重叠检查，不修改配置。</summary>
    public Task<IReadOnlyList<MountImportResult>> PreviewImportConfigurationsAsync(IReadOnlyList<MountImportItem> items, CancellationToken token = default)
    {
        items = items.ToArray();
        return OwnAsync<IReadOnlyList<MountImportResult>>(cancel => Task.Run(async () =>
        {
            ValidateImportCount(items); var results = new List<MountImportResult>(); var accepted = new List<(string Path, FileSourceConfiguration Source)>();
            await _configurationGate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                for (var index = 0; index < items.Count; index++)
                {
                    cancel.ThrowIfCancellationRequested(); var result = PreviewImportItem(items[index], index, accepted); results.Add(result);
                    if (items[index].Selected && result.State == "Ready") accepted.Add((Path.GetFullPath(items[index].LocalPath), items[index].Configuration.Source));
                }
            }
            finally { _configurationGate.Release(); }
            return (IReadOnlyList<MountImportResult>)results;
        }, cancel), token);
    }

    /// <summary>只新增并保存；不连接、注册、创建文件源或启动上传。取消保留已保存项，后续条目标记取消。</summary>
    public Task<MountImportBatchResult> ImportConfigurationsAsync(IReadOnlyList<MountImportItem> items, CancellationToken token = default, IProgress<MountImportResult>? progress = null)
    {
        items = items.ToArray();
        return OwnAsync(cancel => Task.Run(async () =>
        {
            ValidateImportCount(items); var results = new List<MountImportResult>(); var canceled = false;
            for (var index = 0; index < items.Count; index++)
            {
                MountImportResult result;
                if (!items[index].Selected) result = new(index, "Skipped", "未选择，未导入。");
                else if (cancel.IsCancellationRequested) { canceled = true; result = new(index, "Canceled", "已取消，未保存。"); }
                else
                {
                    var held = false;
                    try
                    {
                        await _configurationGate.WaitAsync(cancel).ConfigureAwait(false); held = true;
                        var preview = PreviewImportItem(items[index], index, []);
                        if (preview.State != "Ready") result = preview with { State = preview.State == "Duplicate" ? "Skipped" : "Failed" };
                        else
                        {
                            var item = items[index].Configuration;
                            var credentials = item.Source.Kind == FileSourceKind.S3 && item.Source.UseDefaultCredentials ? new SourceCredentials() : item.Credentials;
                            var saved = SaveNewMountCore(item.Name, items[index].LocalPath, item.AutoConnect, item.Source, credentials, item.ThumbnailsEnabled, cancel, importing: true, remoteAuto:item.RemoteAutoDetectEnabled, incremental:item.RemoteIncrementalSeconds, full:item.RemoteFullSeconds,sqs:item.Sqs,sqsCredentials:item.SqsCredentials);
                            result = new(index, "Saved", "已保存，尚未连接。", saved.Id);
                        }
                    }
                    catch (OperationCanceledException) when (cancel.IsCancellationRequested || IsStopping) { canceled = true; result = new(index, "Canceled", "已取消，未保存。"); }
                    catch (Exception error) { result = new(index, "Failed", ImportError(error)); }
                    finally { if (held) _configurationGate.Release(); }
                }
                results.Add(result); if (result.State == "Saved") Notify();
                try { progress?.Report(result); } catch (Exception error) { _logger.Queue("配置导入", "Warning", "结果通知失败：" + error.GetType().Name); }
            }
            return new MountImportBatchResult(results, canceled);
        }, cancel), token);
    }

    private MountImportResult PreviewImportItem(MountImportItem request, int index, IReadOnlyList<(string Path, FileSourceConfiguration Source)> accepted)
    {
        try
        {
            var item = request.Configuration ?? throw new ArgumentException("配置条目为空。");
            ValidateNewConnection(item.Source, item.Credentials);ValidateSqsSettings(item.Source,item.Sqs,item.SqsCredentials);
            if(item.RemoteIncrementalSeconds is <10 or >86400||item.RemoteFullSeconds is <30 or >604800)throw new ArgumentException("检测间隔超出允许范围。");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.LocalPath));
            foreach (var existing in _roots.Values.Select(root => root.Definition))
                if (MountPathGuard.IsUnder(existing.LocalPath, full) || MountPathGuard.IsUnder(full, existing.LocalPath))
                    return new(index, full.Equals(existing.LocalPath, StringComparison.OrdinalIgnoreCase) && SameSource(item.Source, existing.Source) ? "Duplicate" : "Conflict",
                        "本地目录已有挂载或与现有挂载重叠；现有配置保持不变，请跳过或重新选择目录。");
            foreach (var previous in accepted)
                if (MountPathGuard.IsUnder(previous.Path, full) || MountPathGuard.IsUnder(full, previous.Path))
                    return new(index, full.Equals(previous.Path, StringComparison.OrdinalIgnoreCase) && SameSource(item.Source, previous.Source) ? "Duplicate" : "Conflict", "与本批已选目录重复或重叠，请重新选择。");
            ValidateNewMountRoot(item.Name, request.LocalPath);
            return new(index, "Ready", "可导入；仅保存，之后手动连接。");
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { return new(index, "Invalid", ImportError(error)); }
    }
    private static bool SameSource(FileSourceConfiguration source, FileSourceConfiguration? other)
        => other is not null && source with { Endpoint = FileSourceFactory.Endpoint(source).AbsoluteUri } == other with { Endpoint = FileSourceFactory.Endpoint(other).AbsoluteUri };
    private static void ValidateImportCount(IReadOnlyList<MountImportItem> items)
    { if (items.Count is < 1 or > MountBackupCodec.MaximumItems || items.Any(item => item is null)) throw new ArgumentException("请选择有效的配置条目。"); }
    private static string ImportError(Exception error) => error switch
    {
        ArgumentException or NotSupportedException or IOException => error.Message,
        System.Security.Cryptography.CryptographicException => "无法使用当前 Windows 用户保存凭据。",
        UnauthorizedAccessException => "没有读取目录或保存配置的权限。",
        _ => "目录验证或配置保存失败，请检查目录、权限及同步根状态。"
    };

    /// <summary>复用新增与导入的连接参数校验，只验证本地参数，不建立协议客户端。</summary>
    private static void ValidateNewConnection(FileSourceConfiguration? source, SourceCredentials? credentials)
    {
        if (source is null || source.Kind is not (FileSourceKind.WebDav or FileSourceKind.S3)) throw new NotSupportedException("请选择已实现的 WebDAV 或 S3 协议。");
        if (source.Endpoint is null || source.Bucket is null || source.Prefix is null || source.Region is null || credentials is null ||
            credentials.UserName is null || credentials.Password is null || credentials.AccessKey is null || credentials.SecretKey is null || credentials.SessionToken is null)
            throw new ArgumentException("连接配置或凭据字段不完整。");
        FileSourceFactory.Endpoint(source);
        if (source.Kind == FileSourceKind.S3)
        {
            S3FileSource.ValidateOptions(source);
            if (!source.UseDefaultCredentials && (string.IsNullOrWhiteSpace(credentials.AccessKey) || string.IsNullOrWhiteSpace(credentials.SecretKey))) throw new ArgumentException("请填写 S3 访问密钥或选择默认凭据链。");
        }
        if (source.Kind == FileSourceKind.WebDav && credentials.UserName.Contains(':')) throw new ArgumentException("WebDAV 账号不能包含冒号。");
    }

    /// <summary>必须在配置串行锁内调用；空目录与实际系统同步根检查不依赖备份中的身份。</summary>
    private string ValidateNewMountRoot(string name, string directory)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请输入挂载名称。");
        var root = MountPathGuard.NormalizeRoot(directory);
        if (MountPathGuard.IsUnder(root, _repository.DataDirectory) || MountPathGuard.IsUnder(_repository.DataDirectory, root) || MountPathGuard.IsUnder(root, AppContext.BaseDirectory) || MountPathGuard.IsUnder(AppContext.BaseDirectory, root)) throw new IOException("不能将程序或自身数据目录用作挂载根。");
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("新挂载必须选择空目录。");
        if (_roots.Values.Any(item => MountPathGuard.IsUnder(item.Definition.LocalPath, root) || MountPathGuard.IsUnder(root, item.Definition.LocalPath))) throw new IOException("挂载根不能重叠。");
        var candidate = new MountDefinition(Guid.NewGuid(), name, root, RootIdentity: MountPathGuard.FileIdentity(root));
        if (CloudRootRegistration.IsOwnedRoot(candidate)) throw new IOException("目录已有同步根，不能通过导入接管。");
        return root;
    }

    /// <summary>配置锁保护根冲突检查；停止锁保护凭据、定义和运行列表提交，失败清除本项凭据。</summary>
    private MountDefinition SaveNewMountCore(string name, string directory, bool autoConnect, FileSourceConfiguration? source, SourceCredentials? credentials, bool thumbnails, CancellationToken token, bool importing = false, bool remoteAuto = true, int incremental = 60, int full = 300, SqsConfiguration? sqs=null, SourceCredentials? sqsCredentials=null)
    {
        if (_factory is FileSourceFactory || importing) ValidateNewConnection(source, credentials ?? new SourceCredentials());
        ValidateSqsSettings(source!,sqs,sqsCredentials);
        var root = ValidateNewMountRoot(name, directory);
        var definition = new MountDefinition(Guid.NewGuid(), name.Trim(), root, autoConnect, RootIdentity: MountPathGuard.FileIdentity(root), Source: source,
            RegistrationStage: MountRegistrationStage.NotRegistered, AutoUploadEnabled: true, UploadInitialReviewCompleted: true, ContinuousUploadMigrated: true, ThumbnailsEnabled: thumbnails, RemoteAutoDetectEnabled: remoteAuto, RemoteIncrementalSeconds: incremental, RemoteFullSeconds: full,Sqs:sqs);
        var store = new CredentialStore(_repository.DataDirectory);
        lock (_lifetime)
        {
            ThrowIfStopping(); token.ThrowIfCancellationRequested();
            try
            {
                if (credentials is not null) store.Save(definition.Id, credentials);
                if(sqs?.CredentialMode==SqsCredentialMode.SeparateKeys&&sqsCredentials is not null)SqsStore().Save(definition.Id,sqsCredentials);
                _repository.SaveDefinition(definition);
            }
            catch
            {
                // 新 ID 仅属于本次保存；逐项清理中途创建的定义和凭据，清理错误另记而不掩盖原故障。
                try { _repository.DeleteDefinition(definition.Id); } catch (Exception error) { _logger.Queue("配置导入", "Warning", "清理未完成定义：" + error.GetType().Name); }
                try { SqsStore().Delete(definition.Id); } catch (Exception error) { _logger.Queue("配置导入", "Warning", "清理队列凭据：" + error.GetType().Name); }
                try { store.Delete(definition.Id); } catch (Exception error) { _logger.Queue("配置导入", "Warning", "清理未完成凭据：" + error.GetType().Name); }
                throw;
            }
            _roots[definition.Id] = new(definition) { SkipStartupConnection = importing };
        }
        return definition;
    }
}
