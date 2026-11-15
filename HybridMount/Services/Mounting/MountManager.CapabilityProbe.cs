using HybridMount.Models.Mounting.Synology;

namespace HybridMount.Services.Mounting;

/// <summary>显式检测由应用拥有；不创建挂载配置，退出等待在途写入和独立清理，日志只含运行ID和公开结果。</summary>
public sealed partial class MountManager
{
    private string CapabilityProbeDirectory => Path.Combine(_repository.DataDirectory, "CapabilityProbes");

    internal Task<IReadOnlyList<CapabilityProbeRun>> ReadCapabilityProbesAsync(FileSourceConfiguration source, SourceCredentials credentials, CancellationToken token)
        => OwnAsync(_ => Task.FromResult<IReadOnlyList<CapabilityProbeRun>>(CapabilityProbeSession.Read(CapabilityProbeDirectory)
            .Where(run => run.Scope == CapabilityProbeSession.Scope(source, credentials) && run.Target == CapabilityProbeSession.Target(source)).OrderByDescending(run => run.StartedUtc).ToArray()), token);

    /// <summary>当前不可变草稿直接检测，不要求本地路径或已保存配置；恢复参数只允许重试清理，绝不重跑写测。</summary>
    internal Task<(CapabilityProbeRun Run, ProtocolCapabilitySnapshot Snapshot)> DetectCapabilitiesAsync(
        FileSourceConfiguration source, SourceCredentials credentials, SqsConfiguration? sqs, SourceCredentials? queueCredentials,
        string? oneTimePassword, IProgress<CapabilityProbeRun>? progress, CancellationToken token, Guid? cleanupRun = null)
        => OwnAsync(async cancel =>
        {
            FileSourceFactory.ValidateProtocolOptions(source);
            FileSourceFactory.ValidateProtocolCredentials(source, credentials);
            if (sqs?.Enabled == true)
            {
                _ = SqsQueueClient.Validate(source, sqs);
                var keys = sqs.CredentialMode == SqsCredentialMode.SeparateKeys ? queueCredentials : credentials;
                var defaults = sqs.CredentialMode == SqsCredentialMode.DefaultCredentials || sqs.CredentialMode == SqsCredentialMode.ReuseS3 && source.UseDefaultCredentials;
                if (!defaults && (string.IsNullOrWhiteSpace(keys?.AccessKey) || string.IsNullOrWhiteSpace(keys.SecretKey)))
                    throw new ArgumentException("请填写当前草稿的SQS访问密钥，或选择有效的凭据来源。");
            }
            var old = cleanupRun is { } id ? CapabilityProbeSession.Read(CapabilityProbeDirectory).SingleOrDefault(run => run.RunId == id)
                ?? throw new IOException("检测清理记录不存在。") : null;
            var session = new CapabilityProbeSession(CapabilityProbeDirectory, source, credentials,
                value => { progress?.Report(value); SaveCapabilityProbeOperation(value); },
                text => _logger.Queue("能力检测", "Information", text), old);
            var binding = TasksQueue.StartMount(session.Run.RunId);
            using var requestStop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            var detectionToken = requestStop.Token;
            using var cancelOwner = TasksQueue.RegisterCancellation("probe:" + session.Run.RunId.ToString("N"), () =>
            {
                if (!requestStop.IsCancellationRequested) _ = requestStop.CancelAsync();
                return true;
            });
            IFileSource? adapter = null;
            var canceled = false;
            Exception? fatal = null;
            IDisposable? context = null, workflow = null;
            try
            {
                context = TasksQueue.Enter(binding, new MountingTask("probe:" + session.Run.RunId.ToString("N"), binding.MountId, binding.Generation, MountingPriority.User, DateTimeOffset.UtcNow, "capabilityProbe") { FlowId = Guid.NewGuid(), Channel = CloudOperationChannel.Upload });
                workflow = await TasksQueue.AcquireCurrentWorkflowAsync(detectionToken).ConfigureAwait(false);
                var work = Path.Combine(CapabilityProbeDirectory, session.Run.RunId.ToString("N"));
                adapter = (_factory as FileSourceFactory ?? new FileSourceFactory()).CreateCapabilityProbeSource(source, credentials, work, oneTimePassword);
                if (adapter is IMountNetworkSource network) network.BindQueue(binding);
                if (adapter is not ICapabilityProbeSource probe) throw new NotSupportedException("此协议尚未实现主动检测。");
                if (old is null)
                {
                    session.Stage("Testing");
                    try
                    {
                        await probe.DetectCapabilitiesAsync(session, detectionToken).ConfigureAwait(false);
                        if (sqs?.Enabled == true)
                        {
                            await session.CaseAsync("sqs.attributes", "@queue", async request =>
                            {
                                var keys = sqs.CredentialMode == SqsCredentialMode.SeparateKeys ? queueCredentials ?? new SourceCredentials() : credentials;
                                await using var client = new SqsQueueClient(source, sqs, keys);
                                using var slot = await MountingHttpScheduler.AcquireAsync(binding, MountingPriority.User, request).ConfigureAwait(false);
                                var attributes = await client.AttributesAsync(request).ConfigureAwait(false);
                                SqsQueueClient.VerifyAttributes(sqs, attributes);
                            }, detectionToken).ConfigureAwait(false);
                            session.Skip("sqs.receive", "RealMessagesNotConsumed");
                            session.Skip("sqs.ack", "RealMessagesNotConsumed");
                        }
                    }
                    catch (OperationCanceledException) when (detectionToken.IsCancellationRequested) { canceled = true; }
                    catch (Exception error)
                    {
                        fatal = error;
                        _logger.Queue("能力检测", "Warning", $"probe:{session.Run.RunId:N} detection {error.GetType().Name}");
                    }
                }
                // 适配器按顺序返回后没有新写入；检测取消和退出均不能跳过收尾。
                try { session.Stage("Cleaning"); }
                catch (Exception error) { fatal ??= error; }
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await probe.CleanupCapabilitiesAsync(session, cleanup.Token).ConfigureAwait(false); }
                catch (Exception error)
                { _logger.Queue("能力检测", "Warning", $"probe:{session.Run.RunId:N} cleanup {error.GetType().Name}"); }
                if (fatal is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fatal).Throw();
                var pending = session.Run.Resources.Any(item => item.Stage is not ("Cleaned" or "Planned"));
                session.Stage(canceled || detectionToken.IsCancellationRequested ? pending ? "CanceledCleanupPending" : "Canceled" : pending ? "CleanupPending" : "Completed");
                session.CompleteMissing(adapter.ProtocolCapabilities, sqs?.Enabled == true);
                return (session.Run, session.Snapshot(adapter.ProtocolCapabilities));
            }
            finally
            {
                try { if (adapter is not null) await adapter.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    workflow?.Dispose(); context?.Dispose();
                    await TasksQueue.RetireMountAsync(binding).ConfigureAwait(false);
                }
            }
        }, token);

    /// <summary>历史只追加公开阶段和精确遗留路径；任务页关闭不影响检测或清理所有权。</summary>
    private void SaveCapabilityProbeOperation(CapabilityProbeRun run)
    {
        var unfinished = run.Resources.Where(item => item.Stage is not ("Cleaned" or "Planned")).ToArray();
        var results = run.Cases.Select(item => new MountItemResult(item.Id, item.Skipped ? "Skipped" : item.Status.ToString(), item.Reason))
            .Concat(unfinished.Select(item => new MountItemResult(item.RemotePath, item.Stage, CapabilityProbePresentation.ResourceDetails(item)))).ToArray();
        var state = run.Stage switch { "Completed" => "Completed", "Canceled" => "Canceled", "CleanupPending" or "CanceledCleanupPending" => "PartialFailure", _ => "Running" };
        _repository.SaveOperation(new("probe:" + run.RunId.ToString("N"), "capabilityProbe", state, run.Cases.Length,
            ProtocolCapabilityCatalog.For(run.Kind).Count, results, run.Stage is "CleanupPending" or "CanceledCleanupPending" ? global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("CapabilityProbePendingHistory", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)! : null,
            RemotePath: run.Target));
        Notify();
    }
}
