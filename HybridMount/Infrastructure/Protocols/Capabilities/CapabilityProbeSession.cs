using HybridMount.Models.Mounting.Capabilities;
using HybridMount.Helpers.Paths;
using HybridMount.Infrastructure.Files;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Models.Mounting;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HybridMount.Infrastructure.Protocols.Capabilities;

/// <summary>协议适配器的显式写测入口；实现沿用实际传输，返回前排空所有写入，清理使用独立令牌。</summary>
internal interface ICapabilityProbeSource
{
    Task DetectCapabilitiesAsync(CapabilityProbeSession session, CancellationToken token);
    Task CleanupCapabilitiesAsync(CapabilityProbeSession session, CancellationToken token);
}

/// <summary>后台线程拥有的一次检测会话；每次写前落盘，单项失败不停止无依赖用例，取消禁止启动新用例。</summary>
internal sealed class CapabilityProbeSession
{
    private readonly string _file;
    private readonly Action<CapabilityProbeRun>? _publish;
    private readonly Action<string>? _log;
    private CapabilityProbeRun _run;
    internal CapabilityProbeRun Run => _run;
    internal string Prefix => "hybridmount-probe-" + _run.RunId.ToString("N") + "-";

    internal CapabilityProbeSession(string directory, FileSourceConfiguration source, SourceCredentials credentials,
        Action<CapabilityProbeRun>? publish = null, Action<string>? log = null, CapabilityProbeRun? resume = null)
    {
        Directory.CreateDirectory(directory);
        _run = resume ?? new(Guid.NewGuid(), source.Kind, Scope(source, credentials), Target(source), DateTimeOffset.UtcNow, "Preflight", [], []);
        if (_run.Scope != Scope(source, credentials) || _run.Target != Target(source) || _run.Kind != source.Kind)
            throw new IOException("检测记录与当前服务器、账号或目录不匹配，未执行清理。");
        _file = Path.Combine(directory, _run.RunId.ToString("N") + ".json");
        _publish = publish;
        _log = log;
        Save();
    }

    /// <summary>范围摘要不含密码；配置根不参与账号域，跨源实例只排除相同账号和服务器的精确拥有路径。</summary>
    internal static string Scope(FileSourceConfiguration source, SourceCredentials credentials)
    {
        var endpoint = FileSourceFactory.Endpoint(source).AbsoluteUri;
        var identity = source.Kind == FileSourceKind.S3 ? (source.UseDefaultCredentials ? "default" : credentials.AccessKey) : credentials.UserName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{source.Kind}\0{endpoint}\0{source.Bucket}\0{identity}")));
    }

    internal static string Target(FileSourceConfiguration source) => source.Kind == FileSourceKind.S3
        ? "s3://" + source.Bucket + "/" + source.Prefix.Trim('/')
        : FileSourceFactory.Endpoint(source).AbsoluteUri + (source.Kind == FileSourceKind.Synology ? source.Prefix.Trim('/') : "");

    internal void Stage(string stage)
    {
        _run = _run with { Stage = stage };
        Log("stage " + stage + " cases=" + _run.Cases.Length + " pending=" + _run.Resources.Count(item => item.Stage is not ("Cleaned" or "Planned")));
        Save();
    }

    /// <summary>在发送创建、复制或初始化请求前调用；仅规划不授予远端删除权。</summary>
    internal string Plan(string suffix, string remotePath, string kind = "File", params byte[][] contents)
    {
        var path = Prefix + suffix;
        if (_run.Resources.Length >= 128 || suffix.Length > 128 || suffix.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new IOException("检测资源数量或名称超限。");
        if (_run.Resources.Any(item => item.Path == path)) return path;
        _run = _run with { Resources = [.. _run.Resources, new(path, remotePath, kind, "Planned", contents.Select(Hash).Distinct().ToArray())] };
        Log("planned " + kind + " " + remotePath);
        Save();
        return path;
    }

    /// <summary>只输出固定阶段和有限协议码，不保留任意异常正文或URL。</summary>
    internal static string FailureReason(Exception error)
    {
        if (error is CapabilityProbeCleanupException cleanup) return cleanup.Reason;
        if (error is SourceConflictException) return "IdentityChanged";
        if (error is OperationCanceledException) return "CleanupTimeout";
        var diagnostic = (error as SourceConnectionException ?? error.InnerException as SourceConnectionException)?.Diagnostic;
        var status = diagnostic?.HttpStatus ?? (error as SourceRequestRejectedException)?.HttpStatus;
        return "CleanupUnconfirmed" + (status is {} http ? " HTTP " + http : "") + (diagnostic?.ServiceCode is {} code ? " " + code : "");
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>精确回执和阶段必须先持久化；只有 Owned 资源参与活动挂载排除，Foreign 永不被认领。</summary>
    internal void Update(string path, Func<CapabilityProbeResource, CapabilityProbeResource> update)
    {
        if (!_run.Resources.Any(item => item.Path == path)) throw new IOException("检测资源没有持久意图。");
        _run = _run with { Resources = _run.Resources.Select(item => item.Path == path ? update(item) : item).ToArray() };
        var changed = Resource(path);
        Log("resource " + changed.Kind + " " + changed.RemotePath + " " + changed.Stage + " " + changed.Reason);
        Save();
    }

    internal CapabilityProbeResource Resource(string path) => _run.Resources.Single(item => item.Path == path);

    /// <summary>每个用例最多三十秒；权限、超时、冲突保留为未知，明确接口拒绝和语义不成立由适配器单独记录。</summary>
    internal async Task CaseAsync(string id, string scope, Func<CancellationToken, Task> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var result = new CapabilityProbeCase(id, ProtocolCapabilityStatus.Supported, scope, "", 0);
        Log("begin " + id + " " + scope);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await action(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Log("cancel " + id); throw; }
        catch (CapabilityProbeSkipException error) { result = result with { Status = ProtocolCapabilityStatus.Unknown, Reason = error.Message, Skipped = true }; }
        catch (CapabilityProbeUnsupportedException error) { result = result with { Status = ProtocolCapabilityStatus.Unsupported, Reason = error.Message }; }
        catch (Exception error) when (error is IOException or OperationCanceledException or NotSupportedException or ArgumentException)
        {
            var diagnostic = (error as SourceConnectionException)?.Diagnostic;
            var httpStatus = diagnostic?.HttpStatus ?? (error as SourceRequestRejectedException)?.HttpStatus;
            var permission = httpStatus is 401 or 403 || diagnostic?.ServiceCode == "AccessDenied" || error is SourcePermissionException { Pending: false };
            result = result with { Status = httpStatus is 405 or 501 ? ProtocolCapabilityStatus.Unsupported : ProtocolCapabilityStatus.Unknown,
                Reason = (permission ? "Permission" : error is OperationCanceledException ? "Timeout" : error is SourceOutcomeUnknownException ? "OutcomeUnknown" : "Uncertain") + (httpStatus is {} code ? " HTTP " + code : "") + (diagnostic?.ServiceCode is {} serviceCode ? " " + serviceCode : ""),
                PermissionDenied = permission };
        }
        result = result with { Milliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds };
        _run = _run with { Cases = [.. _run.Cases.Where(item => item.Id != id), result] };
        Log($"end {id} {result.Status} {result.Reason} {result.Milliseconds}ms");
        Save();
    }

    internal void Skip(string id, string reason)
    {
        if (_run.Cases.Any(item => item.Id == id)) return;
        _run = _run with { Cases = [.. _run.Cases, new(id, ProtocolCapabilityStatus.Unknown, "", reason, 0, true)] };
        Log("skip " + id + " " + reason);
        Save();
    }

    internal void CompleteMissing(ProtocolCapabilitySnapshot? evidence, bool sqs)
    {
        foreach (var descriptor in ProtocolCapabilityCatalog.For(_run.Kind, sqs))
        {
            if (_run.Cases.Any(item => item.Id == descriptor.Id)) continue;
            var observed = evidence is null ? null : ProtocolCapabilitySummary.Read(evidence, descriptor.Id);
            if (observed is { Evidence.Count: > 0 })
                _run = _run with { Cases = [.. _run.Cases, new(descriptor.Id, observed.Status, observed.Evidence.Last().Scope, observed.Reason.ToString(), 0)] };
            else Skip(descriptor.Id, _run.Stage.StartsWith("Canceled", StringComparison.Ordinal) ? "Canceled" : "NotObserved");
        }
        Save();
    }

    internal ProtocolCapabilitySnapshot Snapshot(ProtocolCapabilitySnapshot? original)
    {
        var values = (original?.Evidence ?? []).ToList();
        foreach (var step in _run.Cases.Where(item => !item.Skipped))
        {
            values.RemoveAll(item => item.Id == step.Id && item.Scope == step.Scope);
            values.Add(new(step.Id, step.Status, step.Scope, DateTimeOffset.UtcNow,
                step.PermissionDenied ? ProtocolCapabilityReason.Permission : step.Status == ProtocolCapabilityStatus.Unknown ? ProtocolCapabilityReason.Uncertain : ProtocolCapabilityReason.None));
        }
        return new(_run.Kind, _run.RunId, DateTimeOffset.UtcNow, values.AsReadOnly());
    }

    private void Log(string value) => _log?.Invoke($"probe:{_run.RunId:N} {value}");
    private void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_run);
        if (bytes.Length > 2 * 1024 * 1024) throw new IOException("检测清理记录超限。");
        var temporary = _file + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, _file, true);
        CapabilityProbeOwnership.Publish(_run);
        _publish?.Invoke(_run);
    }

    /// <summary>只读加载有界清理记录；损坏或未知记录不得转为远端清理授权。</summary>
    internal static IReadOnlyList<CapabilityProbeRun> Read(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        var result = new List<CapabilityProbeRun>();
        var files = Directory.EnumerateFiles(directory, "*.json").Take(1025).ToArray();
        if (files.Length > 1024) throw new IOException("检测记录数量超限，未省略或清理任何遗留资源。");
        foreach (var file in files)
        {
            if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new IOException("检测记录超限，未加载清理授权。");
            var value = JsonSerializer.Deserialize<CapabilityProbeRun>(File.ReadAllBytes(file)) ?? throw new IOException("检测记录无效。");
            if (Path.GetFileNameWithoutExtension(file) != value.RunId.ToString("N") || !Enum.IsDefined(value.Kind) ||
                value.Scope is null || value.Scope.Length != 64 || !value.Scope.All(char.IsAsciiHexDigit) ||
                value.Resources is null || value.Cases is null || value.Resources.Length > 128 || value.Cases.Length > 128)
                throw new IOException("检测记录身份或数量无效，未加载清理授权。");
            if (value.Cases.Any(item => item is null || string.IsNullOrEmpty(item.Id) || item.Id.Length > 128 || item.Scope is null || item.Scope.Length > 4096 || item.Reason is null || item.Reason.Length > 4096))
                throw new IOException("检测结果无效，未加载清理授权。");
            foreach (var item in value.Resources)
            {
                if (item is null || item.Path is null) throw new IOException("检测资源路径缺失，未加载清理授权。");
                string normalized;
                try { normalized = RelativePaths.Normalize(item.Path); }
                catch (Exception error) when (error is ArgumentException or IOException) { throw new IOException("检测路径无效，未加载清理授权。"); }
                static bool Tokens(string[]? values) => values is null || values.Length <= 128 && values.All(text => text is { Length: > 0 and <= 4096 } && !text.Any(char.IsControl));
                if (item.Path != normalized || !item.Path.StartsWith("hybridmount-probe-" + value.RunId.ToString("N") + "-", StringComparison.Ordinal) ||
                    item.Hashes is null || item.Hashes.Length > 8 || item.Hashes.Any(hash => hash is null || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) ||
                    string.IsNullOrEmpty(item.RemotePath) || item.RemotePath.Length > 8192 || item.RemotePath.Any(char.IsControl) ||
                    !Tokens(item.VersionIds) || !Tokens(item.UploadIds) || !Tokens(item.DeleteMarkerIds))
                    throw new IOException("检测资源身份无效，未加载清理授权。");
            }
            result.Add(value);
            CapabilityProbeOwnership.Publish(value);
        }
        return result;
    }
}

/// <summary>清理的固定失败原因，界面可解释且日志不记录任意服务端正文。</summary>
internal sealed class CapabilityProbeCleanupException(string reason) : IOException(reason)
{
    internal string Reason { get; } = reason;
}

/// <summary>单项缺少前提，不是服务器不支持。</summary>
internal sealed class CapabilityProbeSkipException(string reason) : IOException(reason);
/// <summary>适配器已经核实错误条件被忽略或接口明确不支持。</summary>
internal sealed class CapabilityProbeUnsupportedException(string reason) : IOException(reason);

/// <summary>进程内精确拥有项索引；不查询网络，不按文件名前缀屏蔽用户数据。</summary>
internal static class CapabilityProbeOwnership
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CapabilityProbeRun> Runs = new();
    internal static void Publish(CapabilityProbeRun run) => Runs[run.RunId] = run;
    internal static bool Contains(string scope, string remotePath, string? version = null) => version is not null && Runs.Values.Any(run => run.Scope == scope &&
        run.Resources.Any(item => item.RemotePath == remotePath && item.Stage == "Owned" && item.ETag is not null && item.ETag == version));
}
