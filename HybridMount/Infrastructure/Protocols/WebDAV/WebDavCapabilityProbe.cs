using HybridMount.Infrastructure.Protocols.Capabilities;
using HybridMount.Models.Mounting;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace HybridMount.Infrastructure.Protocols.WebDAV;

public sealed partial class WebDavFileSource : ICapabilityProbeSource
{
    private string _capabilityProbeScope = "";

    /// <summary>显式检测只修改本次持久登记的随机对象；普通测试连接和能力展开不调用此入口。</summary>
    async Task ICapabilityProbeSource.DetectCapabilitiesAsync(CapabilityProbeSession session, CancellationToken token)
    {
        var rootReady = false;
        await session.CaseAsync("dav.propfind", "", async cancel =>
        {
            if (!(await GetMetadataAsync("", cancel).ConfigureAwait(false)).IsDirectory) throw new IOException("配置根不是目录。");
            rootReady = true;
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.options", "", async cancel =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, _endpoint);
            using var response = await SendAsync(request, cancel).ConfigureAwait(false);
            ProbeSuccess(response);
            response.Dispose();
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.quota", "", async cancel =>
        {
            if ((await GetRemoteCapacityAsync(cancel).ConfigureAwait(false)).TotalBytes is null)
                throw new CapabilityProbeSkipException("QuotaMissing");
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.privileges", "", async cancel =>
        {
            var access = await QueryAccessAsync("", true, cancel).ConfigureAwait(false);
            if (access.Reason != "Verified") throw new CapabilityProbeSkipException(access.Reason);
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.report", "", async cancel =>
        {
            if (!await SupportsCollectionSyncAsync("", cancel).ConfigureAwait(false)) throw new CapabilityProbeSkipException("SyncTokenMissing");
            _ = await GetCollectionChangesAsync("", null, cancel).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        if (!rootReady) return;

        var original = RandomNumberGenerator.GetBytes(32);
        var changed = RandomNumberGenerator.GetBytes(33);
        string Plan(string suffix, string kind = "File") => session.Plan(suffix, Address(session.Prefix + suffix, kind == "Directory").AbsoluteUri, kind, original, changed);
        var file = Plan("file");
        var destination = Plan("destination");
        var moved = Plan("moved");
        var directory = Plan("directory", "Directory");
        await session.CaseAsync("dav.put", file, cancel => ProbeCreateAsync(session, file, original, cancel), token).ConfigureAwait(false);

        void RequireFile() { if (session.Resource(file).Stage != "Owned") throw new CapabilityProbeSkipException("CreateRequired"); }
        await session.CaseAsync("dav.head", file, async cancel =>
        {
            RequireFile();
            using var request = new HttpRequestMessage(HttpMethod.Head, Address(file));
            using var response = await SendAsync(request, cancel).ConfigureAwait(false);
            ProbeSuccess(response);
            response.Dispose();
            if (response.Content.Headers.ContentLength != original.Length) throw new IOException("HEAD长度不一致。");
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.get", file, async cancel =>
        {
            RequireFile();
            await ProbeVerifyBytesAsync(file, original, cancel).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.range", file, async cancel =>
        {
            RequireFile();
            var entry = await GetMetadataAsync(file, cancel).ConfigureAwait(false);
            if (!Strong(Version(entry).ETag)) throw new CapabilityProbeSkipException("StrongVersionRequired");
            var bytes = new byte[4];
            if (await ReadRemoteRangeAsync(entry, 2, bytes, cancel).ConfigureAwait(false) != 4 || !bytes.AsSpan().SequenceEqual(original.AsSpan(2, 4)))
                throw new IOException("范围正文不一致。");
        }, token).ConfigureAwait(false);
        foreach (var create in new[] { true, false })
        {
            await session.CaseAsync(create ? "dav.create" : "dav.update", file, async cancel =>
            {
                RequireFile();
                var before = await ReadContentHashAsync(file, null, cancel).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Put, Address(file)) { Content = new ByteArrayContent(changed) };
                if (create) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                else request.Headers.IfMatch.ParseAdd("\"never-match-" + session.Run.RunId.ToString("N") + "\"");
                using var response = await SendAsync(request, cancel, true).ConfigureAwait(false);
                response.Dispose();
                if (response.StatusCode != HttpStatusCode.PreconditionFailed)
                {
                    ProbeSuccess(response);
            response.Dispose();
                    session.Update(file, item => item with { ETag = null });
                    await ProbeVerifyBytesAsync(file, changed, cancel).ConfigureAwait(false);
                    throw new CapabilityProbeUnsupportedException("ConditionIgnored");
                }
                if (await ReadContentHashAsync(file, null, cancel).ConfigureAwait(false) != before) throw new IOException("条件拒绝后正文变化。");
                if (!create)
                {
                    var entry = await GetMetadataAsync(file, cancel).ConfigureAwait(false);
                    if (!Strong(Version(entry).ETag)) throw new CapabilityProbeSkipException("StrongVersionRequired");
                    using (var update = new HttpRequestMessage(HttpMethod.Put, Address(file)) { Content = new ByteArrayContent(changed) })
                    {
                        update.Headers.IfMatch.ParseAdd(NormalizeTag(Version(entry).ETag)!);
                        using var updated = await SendAsync(update, cancel, true).ConfigureAwait(false);
                        ProbeSuccess(updated);
                    }
                    await ProbeVerifyBytesAsync(file, changed, cancel).ConfigureAwait(false);
                    entry = await GetMetadataAsync(file, cancel).ConfigureAwait(false);
                    using (var restore = new HttpRequestMessage(HttpMethod.Put, Address(file)) { Content = new ByteArrayContent(original) })
                    {
                        restore.Headers.IfMatch.ParseAdd(NormalizeTag(Version(entry).ETag)!);
                        using var restored = await SendAsync(restore, cancel, true).ConfigureAwait(false);
                        ProbeSuccess(restored);
                    }
                    await ProbeVerifyBytesAsync(file, original, cancel).ConfigureAwait(false);
                    entry = await GetMetadataAsync(file, cancel).ConfigureAwait(false);
                    session.Update(file, item => item with { ETag = NormalizeTag(Version(entry).ETag) });
                }
            }, token).ConfigureAwait(false);
        }
        await session.CaseAsync("dav.mkcol", directory, async cancel =>
        {
            if (await TryGetMetadataAsync(directory, cancel).ConfigureAwait(false) is not null)
            {
                session.Update(directory, item => item with { Stage = "Foreign", Reason = "AlreadyExists" });
                throw new CapabilityProbeSkipException("AlreadyExists");
            }
            session.Update(directory, item => item with { Stage = "Sending" });
            using var request = new HttpRequestMessage(new("MKCOL"), Address(directory, true));
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
            using var response = await SendAsync(request, cancel, true).ConfigureAwait(false);
            ProbeSuccess(response);
            response.Dispose();
            var entry = await GetMetadataAsync(directory, cancel).ConfigureAwait(false);
            if (!entry.IsDirectory) throw new IOException("目录创建后类型不符。");
            session.Update(directory, item => item with { Stage = "Owned", ETag = NormalizeTag(Version(entry).ETag) });
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.conditionalDelete", file, async cancel =>
        {
            RequireFile();
            using var request = new HttpRequestMessage(HttpMethod.Delete, Address(file));
            request.Headers.IfMatch.ParseAdd("\"never-match-" + session.Run.RunId.ToString("N") + "\"");
            using var response = await SendAsync(request, cancel, true).ConfigureAwait(false);
            response.Dispose();
                if (response.StatusCode != HttpStatusCode.PreconditionFailed)
            {
                ProbeSuccess(response);
            response.Dispose();
                if (await TryGetMetadataAsync(file, cancel).ConfigureAwait(false) is null)
                {
                    session.Update(file, item => item with { Stage = "Cleaned" });
                    throw new CapabilityProbeUnsupportedException("ConditionIgnored");
                }
                throw new IOException("删除结果未确认。");
            }
            if (await TryGetMetadataAsync(file, cancel).ConfigureAwait(false) is null) throw new IOException("拒绝后对象意外消失。");
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.conditionalMove", file, async cancel =>
        {
            RequireFile();
            await ProbeCreateAsync(session, destination, changed, cancel).ConfigureAwait(false);
            using var request = new HttpRequestMessage(new("MOVE"), Address(file));
            request.Headers.Add("Destination", Address(destination).AbsoluteUri);
            request.Headers.Add("Overwrite", "F");
            using var response = await SendAsync(request, cancel, true).ConfigureAwait(false);
            response.Dispose();
                if (response.StatusCode != HttpStatusCode.PreconditionFailed)
            {
                ProbeSuccess(response);
            response.Dispose();
                throw new CapabilityProbeUnsupportedException("ConditionIgnored");
            }
            await ProbeVerifyBytesAsync(destination, changed, cancel).ConfigureAwait(false);
            // 源条件另行验证，不能只凭 Overwrite:F 推定 If-Match 可靠。
            using var conditional = new HttpRequestMessage(new("MOVE"), Address(file));
            conditional.Headers.Add("Destination", Address(moved).AbsoluteUri);
            conditional.Headers.Add("Overwrite", "F");
            conditional.Headers.IfMatch.ParseAdd("\"never-match-" + session.Run.RunId.ToString("N") + "\"");
            session.Update(moved, item => item with { Stage = "Sending" });
            using var rejected = await SendAsync(conditional, cancel, true).ConfigureAwait(false);
            rejected.Dispose();
            if (rejected.StatusCode != HttpStatusCode.PreconditionFailed)
            {
                ProbeSuccess(rejected);
                session.Update(moved, item => item with { Stage = "Owned", ETag = null });
                throw new CapabilityProbeUnsupportedException("ConditionIgnored");
            }
            if (await TryGetMetadataAsync(file, cancel).ConfigureAwait(false) is null || await TryGetMetadataAsync(moved, cancel).ConfigureAwait(false) is not null)
                throw new IOException("移动条件拒绝后资源不一致。");
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.lock", file, async cancel =>
        {
            RequireFile();
            // 既有锁验证会创建另外的资源，因此此处只在已拥有文件上执行同一底层实现。
            DavLockLease acquired;
            try { acquired = await AcquireLockAsync(file, cancel, true).ConfigureAwait(false); }
            catch (NotSupportedException) { throw new CapabilityProbeUnsupportedException("LockUnsupported"); }
            await using var lease = acquired;
            session.Update(file, item => item with { LockExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(300) });
            var exclusive = false;
            try { await using var duplicate = await AcquireLockAsync(file, cancel, true).ConfigureAwait(false); }
            catch (SourceConflictException) { exclusive = true; }
            if (!exclusive) throw new CapabilityProbeUnsupportedException("LockNotExclusive");
            using var noToken = new HttpRequestMessage(HttpMethod.Put, Address(file)) { Content = new ByteArrayContent(changed) };
            using var denied = await SendAsync(noToken, cancel, true).ConfigureAwait(false);
            if ((int)denied.StatusCode is not (412 or 423))
            {
                ProbeSuccess(denied);
                throw new CapabilityProbeUnsupportedException("LockNotEnforced");
            }
            denied.Dispose();
            await lease.RefreshAsync(cancel).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.unlock", file, async cancel =>
        {
            RequireFile();
            if (!ProtocolCapabilities.Evidence.Any(item => item.Id == "dav.unlock" && item.Status == ProtocolCapabilityStatus.Supported))
                throw new CapabilityProbeSkipException("LockRequired");
            var entry = await GetMetadataAsync(file, cancel).ConfigureAwait(false);
            if (!Strong(Version(entry).ETag)) throw new CapabilityProbeSkipException("StrongVersionRequired");
            using var request = new HttpRequestMessage(HttpMethod.Put, Address(file)) { Content = new ByteArrayContent(original) };
            request.Headers.IfMatch.ParseAdd(NormalizeTag(Version(entry).ETag)!);
            using var response = await SendAsync(request, cancel, true).ConfigureAwait(false);
            ProbeSuccess(response);
            response.Dispose();
            session.Update(file, item => item with { ETag = null, LockExpiresUtc = null });
            await ProbeVerifyBytesAsync(file, original, cancel).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.move", file, async cancel =>
        {
            RequireFile();
            if (await TryGetMetadataAsync(moved, cancel).ConfigureAwait(false) is not null) throw new CapabilityProbeSkipException("DestinationExists");
            var entry = await GetMetadataAsync(file, cancel).ConfigureAwait(false);
            if (!Strong(Version(entry).ETag)) throw new CapabilityProbeSkipException("StrongVersionRequired");
            session.Update(moved, item => item with { Stage = "Sending" });
            using var request = new HttpRequestMessage(new("MOVE"), Address(file));
            request.Headers.Add("Destination", Address(moved).AbsoluteUri);
            request.Headers.Add("Overwrite", "F");
            request.Headers.IfMatch.ParseAdd(NormalizeTag(Version(entry).ETag)!);
            using var response = await SendAsync(request, cancel, true).ConfigureAwait(false);
            ProbeSuccess(response);
            response.Dispose();
            session.Update(moved, item => item with { Stage = "Owned" });
            session.Update(file, item => item with { Stage = "Cleaned" });
            await ProbeVerifyBytesAsync(moved, original, cancel).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        await session.CaseAsync("dav.delete", moved, cancel => ProbeCleanupResourceAsync(session, moved, cancel), token).ConfigureAwait(false);
    }

    private static void ProbeSuccess(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is 405 or 501) throw new CapabilityProbeUnsupportedException("MethodUnsupported");
        Success(response);
    }

    private async Task ProbeCreateAsync(CapabilityProbeSession session, string path, byte[] bytes, CancellationToken token)
    {
        if (await TryGetMetadataAsync(path, token).ConfigureAwait(false) is not null)
        {
            session.Update(path, item => item with { Stage = "Foreign", Reason = "AlreadyExists" });
            throw new CapabilityProbeSkipException("AlreadyExists");
        }
        session.Update(path, item => item with { Stage = "Sending" });
        using var request = new HttpRequestMessage(HttpMethod.Put, Address(path)) { Content = new ByteArrayContent(bytes) };
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        using var response = await SendAsync(request, token, true).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            session.Update(path, item => item with { Stage = "Foreign", Reason = "CreateRejected" });
        ProbeSuccess(response);
            response.Dispose();
        session.Update(path, item => item with { Stage = "Owned", ETag = NormalizeTag(response.Headers.ETag?.ToString()) });
        await ProbeVerifyBytesAsync(path, bytes, token).ConfigureAwait(false);
    }

    private async Task ProbeVerifyBytesAsync(string path, byte[] bytes, CancellationToken token)
    {
        var entry = await GetMetadataAsync(path, token).ConfigureAwait(false);
        if (entry.IsDirectory || entry.Size != bytes.Length || await ReadContentHashAsync(path, null, token).ConfigureAwait(false) != CapabilityProbeSession.Hash(bytes))
            throw new IOException("检测对象内容核对不一致。");
    }

    /// <summary>清理不依赖检测取消令牌；强版本与随机内容共同核验，未知目录创建回执不能授权删除。</summary>
    async Task ICapabilityProbeSource.CleanupCapabilitiesAsync(CapabilityProbeSession session, CancellationToken token)
    {
        foreach (var resource in session.Run.Resources.OrderBy(item => item.Kind == "Directory" ? 1 : 0).ToArray())
        {
            if (resource.Stage is "Cleaned" or "Planned") continue;
            if (resource.Stage == "Foreign")
            {
                try { if (await TryGetMetadataAsync(resource.Path, token).ConfigureAwait(false) is null) session.Update(resource.Path, item => item with { Stage = "Cleaned", Reason = null }); }
                catch (Exception error) when (error is IOException or OperationCanceledException) { /* 不存在未确认时保留只读核对记录。 */ }
                continue;
            }
            try { await ProbeCleanupResourceAsync(session, resource.Path, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException or NotSupportedException)
            {
                session.Update(resource.Path, item => item with { Reason = CapabilityProbeSession.FailureReason(error) });
            }
        }
    }

    private async Task ProbeCleanupResourceAsync(CapabilityProbeSession session, string path, CancellationToken token)
    {
        var item = session.Resource(path);
        if (item.Stage is "Planned" or "Foreign") throw new CapabilityProbeSkipException("NotOwned");
        if (item.RemotePath != Address(path, item.Kind == "Directory").AbsoluteUri) throw new IOException("检测资源越过当前范围。");
        var entry = await TryGetMetadataAsync(path, token).ConfigureAwait(false);
        if (entry is null) { session.Update(path, value => value with { Stage = "Cleaned", Reason = null }); return; }
        var tag = NormalizeTag(Version(entry).ETag);
        if (!Strong(tag)) throw new CapabilityProbeCleanupException("StrongVersionRequired");
        if (item.Kind == "Directory")
        {
            if (item.Stage != "Owned" || !entry.IsDirectory || item.ETag != tag) throw new SourceConflictException("目录归属或版本无法核对。");
            await foreach (var _ in ListAsync(path, token).ConfigureAwait(false)) throw new SourceConflictException("检测目录中出现其他内容。");
        }
        else
        {
            if (entry.IsDirectory || entry.Size > 1024) throw new SourceConflictException("检测文件身份或长度变化。");
            var digest = await ReadContentHashAsync(path, null, token, tag).ConfigureAwait(false);
            if (!item.Hashes.Contains(digest, StringComparer.Ordinal)) throw new SourceConflictException("检测文件内容变化。");
        }
        if (!session.Run.Cases.Any(step => step.Id == "dav.conditionalDelete" && step.Status == ProtocolCapabilityStatus.Supported))
            throw new CapabilityProbeCleanupException("ConditionalDeleteUnverified");
        using var request = new HttpRequestMessage(HttpMethod.Delete, Address(path, entry.IsDirectory));
        request.Headers.IfMatch.ParseAdd(tag!);
        using var response = await SendAsync(request, token, true).ConfigureAwait(false);
        ProbeSuccess(response);
            response.Dispose();
        if (await TryGetMetadataAsync(path, token).ConfigureAwait(false) is not null) throw new IOException("检测资源仍存在。");
        session.Update(path, value => value with { Stage = "Cleaned", Reason = null });
    }
}
