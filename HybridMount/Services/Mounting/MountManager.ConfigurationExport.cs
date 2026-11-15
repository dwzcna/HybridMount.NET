using HybridMount.Infrastructure.Configuration;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Models.Configuration;
using HybridMount.Models.Mounting;
using System.Security.Cryptography;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    /// <summary>仅内存拥有密码；持久任务只有目标、阶段及结果，工作收尾解除秘密引用。</summary>
    private sealed class ConfigurationExportRequest(Guid[] ids, string password, string path)
    {
        internal Guid[] Ids { get; } = ids;
        private string? _password = password;
        internal string Password => _password ?? throw new ObjectDisposedException(nameof(ConfigurationExportRequest));
        internal string Path { get; } = path;
        internal bool Committed { get; set; }
        internal void Clear() => _password = null;
    }
    private readonly HashSet<string> _configurationExports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>位置有效后事务接纳稳定导出任务；不认证、不连接，应用所有权独立于表单。</summary>
    internal Task<AcceptedApplicationTask> AcceptConfigurationExportAsync(IReadOnlyList<Guid> ids, string password, string path, CancellationToken token = default)
    {
        var selected = ids.ToArray();
        MountBackupCodec.ValidatePath(path);
        var fullPath = Path.GetFullPath(path);
        if (selected.Length is < 1 or > MountBackupCodec.MaximumItems || selected.Distinct().Count() != selected.Length)
            throw new ArgumentException(HybridMount.Models.Localization.ResourceMessage.Create("SelectTheMountsToExport").Render());
        if (password.Length is > 0 and < 8) throw new ArgumentException(HybridMount.Models.Localization.ResourceMessage.Create("BackupPasswordMinimum").Render());
        return OwnAsync(cancel =>
        {
            lock (_lifetime)
            {
                ThrowIfStopping(); cancel.ThrowIfCancellationRequested(); EnsureNoRemovalBatch();
                foreach (var id in selected)
                {
                    var root = Root(id);
                    if (root.RemovalRequested || root.Definition.RemovalPending) throw new IOException(HybridMount.Models.Localization.ResourceMessage.Create("MountsBeingRemovedCannotBeExportedYet").Render());
                }
                if (_configurationExports.Count >= 16 || !_configurationExports.Add(fullPath)) throw new IOException(HybridMount.Models.Localization.ResourceMessage.Create("BackupExportTargetBusy").Render());
                var job = new Job(Guid.NewGuid().ToString("N"), Guid.Empty, "exportConfiguration", _stop.Token);
                var request = new ConfigurationExportRequest(selected, password, fullPath);
                job.Snapshot = job.Snapshot with { MountId = null, MountName = "HybridMount.NET", ItemPath = fullPath, Total = 1, BackupProtected = password.Length > 0 };
                try
                {
                    _repository.AcceptApplicationTask(job.Snapshot, OperationStages("exportConfiguration"));
                    _jobs[job.Snapshot.Id] = job;
                    job.Work = OwnAsync(appToken => RunConfigurationExportAsync(job, request, appToken), _stop.Token);
                    Notify(); return Task.FromResult(new AcceptedApplicationTask(job.Snapshot.Id));
                }
                catch { _configurationExports.Remove(fullPath); request.Clear(); job.Cancel.Dispose(); throw; }
            }
        }, token);
    }

#if HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>只在隔离回归的真实阶段阻塞，不进入产品构建。</summary>
    internal Func<string, string, CancellationToken, Task>? ConfigurationExportCheckpointForTests { get; set; }
    internal Action? ConfigurationExportBeforeCommitForTests { get; set; }
#endif
    private async Task RunConfigurationExportAsync(Job job, ConfigurationExportRequest request, CancellationToken appToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(appToken, job.Cancel.Token);
        try
        {
            job.Snapshot = job.Snapshot with { State = "Running" }; Save(job);
            await ExportConfigurationsCoreAsync(request, cancel.Token, job.Snapshot.Id).ConfigureAwait(false);
            if (!request.Committed) throw new IOException(HybridMount.Models.Localization.ResourceMessage.Create("BackupExportCommitIncomplete").Render());
            job.Snapshot = job.Snapshot with { State = "Completed", Completed = 1, Results = [new(request.Path, "Completed", "")] };
            EndTrace(job.Snapshot.Id, "Completed"); Save(job);
        }
        catch (Exception error)
        {
            var stage = _repository.OperationRuns(job.Snapshot.Id).FirstOrDefault()?.Steps.FirstOrDefault(item => item.State == "Running")?.Name;
            var state = error is OperationCanceledException && cancel.IsCancellationRequested ? "Canceled" : "Failed";
            job.Snapshot = job.Snapshot with { State = state, Error = error.Message, FailureStage = stage, FailureHResult = error.HResult };
            EndTrace(job.Snapshot.Id, state, error.Message); Save(job);
            ReportDiagnosticException("配置导出", "后台配置导出未完成", error);
        }
        finally
        {
            request.Clear();
            lock (_lifetime) _configurationExports.Remove(request.Path);
            job.Cancel.Dispose();
        }
    }

    /// <summary>后台按根锁捕获完整配置；真实编码、写入和原子提交完成后才报告成功，取消保留原文件。</summary>
    private async Task ExportConfigurationsCoreAsync(ConfigurationExportRequest request, CancellationToken token, string? taskId = null)
    {
        async Task Enter(string stage)
        {
            if (taskId is null) return;
            Stage(taskId, stage);
#if HYBRIDMOUNT_HEADLESS_TEST
            if (ConfigurationExportCheckpointForTests is { } checkpoint) await checkpoint(taskId, stage, token).ConfigureAwait(false);
#endif
            await Task.CompletedTask.ConfigureAwait(false);
        }
        MountBackupCodec.ValidatePath(request.Path);
        if (request.Ids.Length is < 1 or > MountBackupCodec.MaximumItems || request.Ids.Distinct().Count() != request.Ids.Length) throw new ArgumentException(HybridMount.Models.Localization.ResourceMessage.Create("SelectTheMountsToExport").Render());
        await Enter("OperationStageReadConfiguration").ConfigureAwait(false);
        var items = new List<MountBackupItem>();
        foreach (var id in request.Ids)
        {
            var root = Root(id); await root.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                if (!_roots.ContainsKey(id) || root.RemovalRequested || root.Definition.RemovalPending) throw new IOException(HybridMount.Models.Localization.ResourceMessage.Create("MountsBeingRemovedCannotBeExportedYet").Render());
                var definition = root.Definition;
                if (definition.Source is not { Kind: FileSourceKind.WebDav or FileSourceKind.S3 or FileSourceKind.Synology } source) throw new NotSupportedException(HybridMount.Models.Localization.ResourceMessage.Create("BackupSupportedProtocolsOnly").Render());
                items.Add(new(definition.Name, definition.LocalPath, source, FileSourceFactory.BackupCredentials(definition, _repository.DataDirectory), definition.AutoConnect,
                    definition.ThumbnailsEnabled, definition.RemoteAutoDetectEnabled, definition.RemoteIncrementalSeconds, definition.RemoteFullSeconds, definition.Sqs,
                    FileSourceFactory.BackupAdditionalCredentials(definition, _repository.DataDirectory), definition.CapacityAutoDetectEnabled, definition.MaximumCapacityBytes, definition.ExclusionRules));
            }
            finally { root.Gate.Release(); }
        }
        await Enter("OperationStageEncodeBackup").ConfigureAwait(false);
        var bytes = MountBackupCodec.Encode(new(MountBackupCodec.CurrentVersion, DateTimeOffset.UtcNow, items), request.Password, token);
        items.Clear();
        try
        {
            await Enter("OperationStageWriteBackup").ConfigureAwait(false);
            await MountBackupCodec.WriteAtomicAsync(request.Path, bytes, token, (temporary, destination) =>
            {
                lock (_lifetime)
                {
                    ThrowIfStopping(); token.ThrowIfCancellationRequested();
                    if (taskId is not null) Stage(taskId, "OperationStageCommitBackup");
#if HYBRIDMOUNT_HEADLESS_TEST
                    if (taskId is not null) ConfigurationExportBeforeCommitForTests?.Invoke();
#endif
                    token.ThrowIfCancellationRequested(); File.Move(temporary, destination, true); request.Committed = true;
                }
            }).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>没有内存所有者的导出只显示中断，绝不从持久任务重建密码或自动重放。</summary>
    private MountOperationSnapshot? RestoredOperation(string taskId)
    {
        var task = _repository.FindOperation(taskId);
        return task is { Operation: "exportConfiguration", State: "Queued" or "Running" or "Canceling" }
            ? task with { State = "Interrupted", Error = HybridMount.Models.Localization.ResourceMessage.Create("BackupExportInterrupted").Render() } : task;
    }
    /// <summary>只读取本机导出任务的用户目标，不借挂载身份解析，不访问远端。</summary>
    internal string? ConfigurationExportLocation(string taskId)
    {
        var task = GetOperation(taskId);
        return task is { Operation: "exportConfiguration", ItemPath: { } path } && Path.IsPathFullyQualified(path) ? path : null;
    }
}
