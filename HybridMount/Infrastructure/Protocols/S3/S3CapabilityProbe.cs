using HybridMount.Infrastructure.Protocols.Capabilities;
using HybridMount.Models.Mounting;
using Amazon.S3;
using Amazon.S3.Model;
using System.Net;
using System.Security.Cryptography;

namespace HybridMount.Infrastructure.Protocols.S3;

public sealed partial class S3FileSource : ICapabilityProbeSource
{
    private string _capabilityProbeScope = "";
    private const string ProbeOwnerMetadata = "hybridmount-probe-run";

    /// <summary>显式检测实际 SDK 接口；分片只使用本次拥有的对象和 UploadId，不借用用户正文。</summary>
    async Task ICapabilityProbeSource.DetectCapabilitiesAsync(CapabilityProbeSession session, CancellationToken token)
    {
        await session.CaseAsync("s3.list", "", ProbeAsync, token).ConfigureAwait(false);
        var bytes = RandomNumberGenerator.GetBytes(32);
        var different = RandomNumberGenerator.GetBytes(33);
        string Plan(string suffix) => session.Plan(suffix, _bucket + "/" + Key(session.Prefix + suffix), "File", bytes, different);
        var file = Plan("file");
        var copy = Plan("copy");
        var multipart = Plan("multipart");
        var multipartCopy = Plan("multipart-copy");
        var abort = Plan("abort");
        await session.CaseAsync("s3.put", file, async cancel =>
        {
            if (await TryGetMetadataAsync(file, cancel).ConfigureAwait(false) is not null)
            {
                session.Update(file, item => item with { Stage = "Foreign", Reason = "AlreadyExists" });
                throw new CapabilityProbeSkipException("AlreadyExists");
            }
            session.Update(file, item => item with { Stage = "Sending" });
            var receipt = await ProbePutOwnedAsync(session, file, bytes, null, true, cancel).ConfigureAwait(false);
            ProbeReceipt(session, file, receipt.ETag, receipt.VersionId);
            await ProbeVerifyObjectAsync(session, file, bytes, cancel).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        void RequireFile() { if (session.Resource(file).Stage != "Owned") throw new CapabilityProbeSkipException("CreateRequired"); }
        await session.CaseAsync("s3.head", file, async cancel => { RequireFile(); _ = await HeadAsync(Key(file), cancel).ConfigureAwait(false); }, token).ConfigureAwait(false);
        await session.CaseAsync("s3.get", file, async cancel => { RequireFile(); await ProbeVerifyObjectAsync(session, file, bytes, cancel).ConfigureAwait(false); }, token).ConfigureAwait(false);
        await session.CaseAsync("s3.range", file, async cancel =>
        {
            RequireFile();
            var entry = await GetMetadataAsync(file, cancel).ConfigureAwait(false);
            var buffer = new byte[4];
            if (await ReadRangeAsync(entry, 2, buffer, cancel).ConfigureAwait(false) != 4 || !buffer.AsSpan().SequenceEqual(bytes.AsSpan(2, 4)) || Capabilities.RangeRead != CapabilitySupport.Supported)
                throw new CapabilityProbeUnsupportedException("RangeIgnored");
        }, token).ConfigureAwait(false);
        foreach (var create in new[] { true, false })
        {
            await session.CaseAsync(create ? "s3.create" : "s3.update", file, async cancel =>
            {
                RequireFile();
                var before = await ProbeHashObjectAsync(session, file, null, cancel).ConfigureAwait(false);
                var result = await CallCore(() => ProbePutOwnedRequestAsync(session, file, different,
                    create ? null : "\"never-match-" + session.Run.RunId.ToString("N") + "\"", create, cancel), cancel, true, ExpectedResult.PreconditionRejected).ConfigureAwait(false);
                if (result is not null)
                {
                    ProbeReceipt(session, file, result.ETag, result.VersionId);
                    throw new CapabilityProbeUnsupportedException("ConditionIgnored");
                }
                if (await ProbeHashObjectAsync(session, file, null, cancel).ConfigureAwait(false) != before) throw new IOException("条件拒绝后正文变化。");
                if (!create)
                {
                    var head = await HeadAsync(Key(file), cancel).ConfigureAwait(false);
                    var updated = await ProbePutOwnedAsync(session, file, different, head.ETag, false, cancel).ConfigureAwait(false);
                    ProbeReceipt(session, file, updated.ETag, updated.VersionId);
                    await ProbeVerifyObjectAsync(session, file, different, cancel).ConfigureAwait(false);
                    var restored = await ProbePutOwnedAsync(session, file, bytes, updated.ETag, false, cancel).ConfigureAwait(false);
                    ProbeReceipt(session, file, restored.ETag, restored.VersionId);
                    await ProbeVerifyObjectAsync(session, file, bytes, cancel).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
        }
        await session.CaseAsync("s3.conditionalDelete", file, async cancel =>
        {
            RequireFile();
            var result = await CallCore(() => _client.DeleteObjectAsync(new() { BucketName = _bucket, Key = Key(file), IfMatch = "\"never-match\"" }, cancel), cancel, true, ExpectedResult.PreconditionRejected).ConfigureAwait(false);
            if (result is not null)
            {
                session.Update(file, item => item with { Reason = "DeleteConditionIgnored", DeleteMarkerIds = string.IsNullOrEmpty(result.VersionId) ? item.DeleteMarkerIds : [.. (item.DeleteMarkerIds ?? []), result.VersionId] });
                throw new CapabilityProbeUnsupportedException("ConditionIgnored");
            }
            if (await TryGetMetadataAsync(file, cancel).ConfigureAwait(false) is null) throw new IOException("条件拒绝后对象不存在。");
        }, token).ConfigureAwait(false);
        await session.CaseAsync("s3.conditionalMove", file, async cancel =>
        {
            RequireFile();
            session.Update(copy, item => item with { Stage = "Sending" });
            var originalCopy = await ProbePutOwnedAsync(session, copy, different, null, true, cancel).ConfigureAwait(false);
            ProbeReceipt(session, copy, originalCopy.ETag, originalCopy.VersionId);
            var source = await HeadAsync(Key(file), cancel).ConfigureAwait(false);
            foreach (var sourceCondition in new[] { false, true })
            {
                var request = new CopyObjectRequest { SourceBucket = _bucket, SourceKey = Key(file), DestinationBucket = _bucket,
                    DestinationKey = Key(copy), ETagToMatch = sourceCondition ? "\"never-match\"" : source.ETag,
                    IfNoneMatch = sourceCondition ? null : "*", MetadataDirective = S3MetadataDirective.REPLACE };
                request.Metadata.Add(ProbeOwnerMetadata, session.Run.RunId.ToString("N"));
                var result = await CallCore(() => _client.CopyObjectAsync(request, cancel), cancel, true, ExpectedResult.PreconditionRejected).ConfigureAwait(false);
                if (result is not null)
                {
                    ProbeReceipt(session, copy, result.ETag, result.VersionId);
                    throw new CapabilityProbeUnsupportedException("ConditionIgnored");
                }
                await ProbeVerifyObjectAsync(session, copy, different, cancel).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
        await session.CaseAsync("s3.copy", file, async cancel =>
        {
            RequireFile();
            var target = Plan("copy-new");
            if (await TryGetMetadataAsync(target, cancel).ConfigureAwait(false) is not null) throw new CapabilityProbeSkipException("AlreadyExists");
            session.Update(target, item => item with { Stage = "Sending" });
            var source = await HeadAsync(Key(file), cancel).ConfigureAwait(false);
            var request = new CopyObjectRequest { SourceBucket = _bucket, SourceKey = Key(file), DestinationBucket = _bucket,
                DestinationKey = Key(target), ETagToMatch = source.ETag, IfNoneMatch = "*", MetadataDirective = S3MetadataDirective.REPLACE };
            request.Metadata.Add(ProbeOwnerMetadata, session.Run.RunId.ToString("N"));
            var result = await Call(() => _client.CopyObjectAsync(request, cancel), cancel, true).ConfigureAwait(false);
            ProbeReceipt(session, target, result.ETag, result.VersionId);
            await ProbeVerifyObjectAsync(session, target, bytes, cancel).ConfigureAwait(false);
        }, token).ConfigureAwait(false);

        string? uploadId = null;
        PartETag? part = null;
        await session.CaseAsync("s3.initiate", multipart, async cancel => uploadId = await ProbeBeginAsync(session, multipart, cancel).ConfigureAwait(false), token).ConfigureAwait(false);
        await session.CaseAsync("s3.part", multipart, async cancel =>
        {
            if (uploadId is null) throw new CapabilityProbeSkipException("MultipartRequired");
            var receipt = await PartAsync(Key(multipart), uploadId, 1, bytes, bytes.Length, cancel).ConfigureAwait(false);
            if (string.IsNullOrEmpty(receipt.ETag)) throw new IOException("分片回执缺少版本。");
            part = new(1, receipt.ETag);
        }, token).ConfigureAwait(false);
        await session.CaseAsync("s3.complete", multipart, async cancel =>
        {
            if (uploadId is null || part is null) throw new CapabilityProbeSkipException("PartRequired");
            var receipt = await Call(() => _client.CompleteMultipartUploadAsync(new() { BucketName = _bucket, Key = Key(multipart),
                UploadId = uploadId, PartETags = [part], IfNoneMatch = "*" }, cancel), cancel, true).ConfigureAwait(false);
            ProbeReceipt(session, multipart, receipt.ETag, receipt.VersionId);
            session.Update(multipart, item => item with { UploadIds = [] });
            await ProbeVerifyObjectAsync(session, multipart, bytes, cancel).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        await session.CaseAsync("s3.copyPart", file, async cancel =>
        {
            RequireFile();
            var id = await ProbeBeginAsync(session, multipartCopy, cancel).ConfigureAwait(false);
            var source = await HeadAsync(Key(file), cancel).ConfigureAwait(false);
            var receipt = await Call(() => _client.CopyPartAsync(new() { SourceBucket = _bucket, SourceKey = Key(file), ETagToMatch = [source.ETag],
                SourceVersionId = ImmutableVersion(source.VersionId), DestinationBucket = _bucket, DestinationKey = Key(multipartCopy),
                UploadId = id, PartNumber = 1, FirstByte = 0, LastByte = bytes.Length - 1 }, cancel), cancel, true).ConfigureAwait(false);
            if (string.IsNullOrEmpty(receipt.ETag)) throw new IOException("分片复制回执缺少版本。");
            var completed = await Call(() => _client.CompleteMultipartUploadAsync(new() { BucketName = _bucket, Key = Key(multipartCopy),
                UploadId = id, PartETags = [new(1, receipt.ETag)], IfNoneMatch = "*" }, cancel), cancel, true).ConfigureAwait(false);
            ProbeReceipt(session, multipartCopy, completed.ETag, completed.VersionId);
            session.Update(multipartCopy, item => item with { UploadIds = [] });
            await ProbeVerifyObjectAsync(session, multipartCopy, bytes, cancel).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        await session.CaseAsync("s3.conditionalComplete", file, async cancel =>
        {
            RequireFile();
            foreach (var create in new[] { true, false })
            {
                var id = await ProbeBeginAsync(session, file, cancel).ConfigureAwait(false);
                var piece = await PartAsync(Key(file), id, 1, different, different.Length, cancel).ConfigureAwait(false);
                var result = await CallCore(() => _client.CompleteMultipartUploadAsync(new() { BucketName = _bucket, Key = Key(file), UploadId = id,
                    PartETags = [new(1, piece.ETag)], IfNoneMatch = create ? "*" : null, IfMatch = create ? null : "\"never-match\"" }, cancel), cancel, true, ExpectedResult.PreconditionRejected).ConfigureAwait(false);
                if (result is not null)
                {
                    ProbeReceipt(session, file, result.ETag, result.VersionId);
                    throw new CapabilityProbeUnsupportedException("ConditionIgnored");
                }
            }
        }, token).ConfigureAwait(false);
        await session.CaseAsync("s3.abort", abort, async cancel =>
        {
            var id = await ProbeBeginAsync(session, abort, cancel).ConfigureAwait(false);
            var pieces = await Call(() => _client.ListPartsAsync(new() { BucketName = _bucket, Key = Key(abort), UploadId = id }, cancel), cancel).ConfigureAwait(false);
            if (pieces.IsTruncated == true) throw new IOException("分片清单超限。");
            _protocolEvidence.Record("s3.parts", ProtocolCapabilityStatus.Supported, abort);
            await AbortAsync(new(_bucket, Key(abort), id), cancel).ConfigureAwait(false);
            var remaining = await CallCore(() => _client.ListPartsAsync(new() { BucketName = _bucket, Key = Key(abort), UploadId = id }, cancel), cancel, false, ExpectedResult.Missing).ConfigureAwait(false);
            if (remaining is not null) throw new IOException("中止后分片会话仍存在。");
            session.Update(abort, item => item with { UploadIds = [], Stage = "Cleaned", Reason = null });
        }, token).ConfigureAwait(false);
        await session.CaseAsync("s3.delete", multipart, cancel => ProbeCleanupObjectAsync(session, multipart, cancel), token).ConfigureAwait(false);
    }

    private Task<PutObjectResponse> ProbePutOwnedRequestAsync(CapabilityProbeSession session, string path, byte[] bytes, string? etag, bool create, CancellationToken token)
    {
        var request = new PutObjectRequest { BucketName = _bucket, Key = Key(path), InputStream = new MemoryStream(bytes, false),
            AutoCloseStream = true, AutoResetStreamPosition = false, UseChunkEncoding = false, IfMatch = etag, IfNoneMatch = create ? "*" : null };
        request.Metadata.Add(ProbeOwnerMetadata, session.Run.RunId.ToString("N"));
        return _client.PutObjectAsync(request, token);
    }
    private Task<PutObjectResponse> ProbePutOwnedAsync(CapabilityProbeSession session, string path, byte[] bytes, string? etag, bool create, CancellationToken token)
        => Call(() => ProbePutOwnedRequestAsync(session, path, bytes, etag, create, token), token, true);
    private static void ProbeReceipt(CapabilityProbeSession session, string path, string? etag, string? version)
        => session.Update(path, item => item with { Stage = "Owned", ETag = etag,
            VersionIds = string.IsNullOrEmpty(version) ? item.VersionIds : [.. (item.VersionIds ?? []).Append(version).Distinct(StringComparer.Ordinal)] });

    private async Task<string> ProbeBeginAsync(CapabilityProbeSession session, string path, CancellationToken token)
    {
        session.Update(path, item => item with { Stage = item.Stage == "Owned" ? "Owned" : "Initiating" });
        var request = new InitiateMultipartUploadRequest { BucketName = _bucket, Key = Key(path) };
        request.Metadata.Add(ProbeOwnerMetadata, session.Run.RunId.ToString("N"));
        var response = await Call(() => _client.InitiateMultipartUploadAsync(request, token), token, true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(response.UploadId)) throw new IOException("分片初始化缺少UploadId，保留未知结果。");
        session.Update(path, item => item with { UploadIds = [.. (item.UploadIds ?? []), response.UploadId] });
        return response.UploadId;
    }

    private async Task ProbeVerifyObjectAsync(CapabilityProbeSession session, string path, byte[] bytes, CancellationToken token)
    {
        if (await ProbeHashObjectAsync(session, path, null, token).ConfigureAwait(false) != CapabilityProbeSession.Hash(bytes))
            throw new IOException("检测对象正文不一致。");
    }
    private async Task<string> ProbeHashObjectAsync(CapabilityProbeSession session, string path, string? version, CancellationToken token)
    {
        using var response = await Call(() => _client.GetObjectAsync(new() { BucketName = _bucket, Key = Key(path), VersionId = version }, token), token).ConfigureAwait(false);
        if (response.ContentLength is < 0 or > 1024 || response.Metadata[ProbeOwnerMetadata] != session.Run.RunId.ToString("N"))
            throw new SourceConflictException("检测对象归属或长度变化。");
        var bytes = new byte[checked((int)response.ContentLength)];
        await response.ResponseStream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await response.ResponseStream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new IOException("对象正文超出声明长度。");
        _protocolEvidence.Record("s3.get", ProtocolCapabilityStatus.Supported, path);
        return CapabilityProbeSession.Hash(bytes);
    }

    /// <summary>中止精确 UploadId，逐版本核验并删除；版本清单不可用或未知版本不能宣称零残留。</summary>
    async Task ICapabilityProbeSource.CleanupCapabilitiesAsync(CapabilityProbeSession session, CancellationToken token)
    {
        foreach (var resource in session.Run.Resources.ToArray())
        {
            if (resource.Stage is "Planned" or "Foreign" or "Cleaned") continue;
            try { await ProbeCleanupObjectAsync(session, resource.Path, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException or NotSupportedException)
            { session.Update(resource.Path, item => item with { Reason = CapabilityProbeSession.FailureReason(error) }); }
        }
    }
    private async Task ProbeCleanupObjectAsync(CapabilityProbeSession session, string path, CancellationToken token)
    {
        var resource = session.Resource(path);
        if (resource.Stage is "Planned" or "Foreign") throw new CapabilityProbeSkipException("NotOwned");
        if (resource.RemotePath != _bucket + "/" + Key(path)) throw new IOException("检测对象越过当前桶或前缀。");
        foreach (var id in resource.UploadIds ?? [])
        {
            await AbortAsync(new(_bucket, Key(path), id), token).ConfigureAwait(false);
            session.Update(path, item => item with { UploadIds = (item.UploadIds ?? []).Where(value => value != id).ToArray() });
        }
        if (resource.Stage == "Initiating" && (resource.UploadIds?.Length ?? 0) == 0)
            throw new CapabilityProbeCleanupException("UploadIdUnknown");
        var listing = await Call(() => _client.ListVersionsAsync(new ListVersionsRequest() { BucketName = _bucket, Prefix = Key(path), MaxKeys = 128 }, token), token).ConfigureAwait(false);
        _protocolEvidence.Record("s3.versions", ProtocolCapabilityStatus.Supported, path);
        if (listing.IsTruncated == true) throw new CapabilityProbeCleanupException("VersionInventoryLimit");
        foreach (var version in (listing.Versions ?? []).Where(item => item.Key == Key(path)))
        {
            if (version.IsDeleteMarker == true)
            {
                if (!(resource.DeleteMarkerIds ?? []).Contains(version.VersionId, StringComparer.Ordinal)) throw new SourceConflictException("存在未归属的删除标记，请人工核对。");
                await Call(() => _client.DeleteObjectAsync(new() { BucketName = _bucket, Key = Key(path), VersionId = version.VersionId }, token), token, true).ConfigureAwait(false);
                continue;
            }
            var digest = await ProbeHashObjectAsync(session, path, version.VersionId, token).ConfigureAwait(false);
            if (!resource.Hashes.Contains(digest, StringComparer.Ordinal)) throw new SourceConflictException("检测版本内容变化。");
            // 只有元数据运行ID及随机正文均匹配的精确版本可删除，不凭同名或前缀认领。
            session.Update(path, item => item with { VersionIds = [.. (item.VersionIds ?? []).Append(version.VersionId).Distinct(StringComparer.Ordinal)] });
            await Call(() => _client.DeleteObjectAsync(new() { BucketName = _bucket, Key = Key(path), VersionId = version.VersionId }, token), token, true).ConfigureAwait(false);
        }
        var after = await Call(() => _client.ListVersionsAsync(new ListVersionsRequest() { BucketName = _bucket, Prefix = Key(path), MaxKeys = 128 }, token), token).ConfigureAwait(false);
        if (after.IsTruncated == true || (after.Versions ?? []).Any(item => item.Key == Key(path))) throw new CapabilityProbeCleanupException("VersionsRemain");
        if (await TryGetMetadataAsync(path, token).ConfigureAwait(false) is not null)
        {
            // 非版本服务可能不在版本清单中返回对象，仍须核对元数据与随机正文并使用当前ETag。
            var head = await HeadAsync(Key(path), token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(head.VersionId)) throw new IOException("版本清单与当前对象不一致。");
            var digest = await ProbeHashObjectAsync(session, path, null, token).ConfigureAwait(false);
            if (!resource.Hashes.Contains(digest, StringComparer.Ordinal)) throw new SourceConflictException("检测正文变化。");
            if (!session.Run.Cases.Any(item => item.Id == "s3.conditionalDelete" && item.Status == ProtocolCapabilityStatus.Supported))
                throw new CapabilityProbeCleanupException("ConditionalDeleteUnverified");
            await DeleteKeyAsync(Key(path), head.ETag, token).ConfigureAwait(false);
            if (await TryGetMetadataAsync(path, token).ConfigureAwait(false) is not null) throw new IOException("检测对象仍存在。");
        }
        session.Update(path, item => item with { Stage = "Cleaned", Reason = null, VersionIds = [], UploadIds = [] });
    }
}
