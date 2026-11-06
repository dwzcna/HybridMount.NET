using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>协议内按职责拆分的 Uploads 实现；公开入口与版本字段保持兼容。</summary>
public sealed partial class S3FileSource
{
    private Task<PutObjectResponse> PutAsync(string key, Stream stream, string? etag, CancellationToken token)
        => Call(() => _client.PutObjectAsync(new() { BucketName = _bucket, Key = key, InputStream = new BorrowedStream(stream),
            AutoCloseStream = false, AutoResetStreamPosition = false, UseChunkEncoding = false, IfMatch = etag, IfNoneMatch = etag is null ? "*" : null }, token), token, true);
    private Task<PutObjectResponse?> ProbePutAsync(string key,Stream stream,string? etag,CancellationToken token)
        =>CallCore(()=>_client.PutObjectAsync(new(){BucketName=_bucket,Key=key,InputStream=new BorrowedStream(stream),
            AutoCloseStream=false,AutoResetStreamPosition=false,UseChunkEncoding=false,IfMatch=etag,IfNoneMatch=etag is null?"*":null},token),token,true,ExpectedResult.PreconditionRejected);
    private Task<DeleteObjectResponse> DeleteKeyAsync(string key, string? etag, CancellationToken token)
        => Call(() => _client.DeleteObjectAsync(new() { BucketName = _bucket, Key = key, IfMatch = etag }, token), token, true);
    /// <summary>在条件请求之外核对目录映射；对象键与同名前缀不能同时作为可写目标。</summary>
    private async Task CheckTargetAsync(string path,string? expectedVersion,CancellationToken token)
    {
        var parent=RelativePaths.Parent(path);
        if(parent.Length>0 && !(await GetMetadataAsync(parent,token).ConfigureAwait(false)).IsDirectory)throw new SourceConflictException("S3 父项是文件，不能创建子项。");
        var current=await TryGetMetadataAsync(path,token).ConfigureAwait(false);
        if(current is null){if(expectedVersion is not null)throw new SourceConflictException("S3 更新目标已不存在。");return;}
        if(current.IsDirectory||expectedVersion is null||current.Version!=expectedVersion)throw new SourceConflictException("S3 目标已存在、是目录或版本不匹配。");
    }
    private async Task<SourceEntry> UploadCoreAsync(string path, Stream content, string? expectedVersion,IProgress<UploadProgress>? progress,CancellationToken token)
    {
        path = NonRoot(path);
        await CheckTargetAsync(path,expectedVersion,token).ConfigureAwait(false);
        var etag = expectedVersion is null ? null : RequiredETag(Version(expectedVersion));
        string? temporary = null; Stream input = content;
        try
        {
            if (!content.CanSeek)
            {
                Directory.CreateDirectory(_work); temporary = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".upload.tmp");
                var spool = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous); input = spool;
                await content.CopyToAsync(spool, token).ConfigureAwait(false); spool.Position = 0;
            }
            var length=input.Length-input.Position;
            using var reporting=new UploadProgressStream(input,progress);
            (string? ETag,string? VersionId) receipt;
            if(length>=_multipartThreshold)receipt=await MultipartAsync(Key(path),reporting,etag,token).ConfigureAwait(false);
            else
            {
                var response=await PutAsync(Key(path),reporting,etag,token).ConfigureAwait(false);
                receipt=(response.ETag,ImmutableVersion(response.VersionId));
            }
            try
            {
                var metadata=await GetMetadataAsync(path,token).ConfigureAwait(false);var version=Version(metadata);
                if(metadata.IsDirectory||metadata.Size!=length||string.IsNullOrEmpty(receipt.ETag)||version.ETag!=receipt.ETag||
                    // 一些服务只在 PUT 返回 VersionId，HEAD 没有可查询版本；此时只记录并核对 ETag，不构造历史版本身份。
                    (receipt.VersionId is not null&&version.VersionId is not null&&version.VersionId!=receipt.VersionId))throw new IOException("上传回执与当前远端对象不一致。");
                try{ContentBaseline?.Invoke(new(path,metadata.Version,reporting.Digest));}catch{ /* 不因观察者失败重试写请求。 */ }
                return metadata;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { throw new SourceOutcomeUnknownException("S3 上传后核对", new IOException("写入成功后未能确认元数据。")); }
        }
        finally { if (input != content) await input.DisposeAsync().ConfigureAwait(false); if (temporary is not null) File.Delete(temporary); }
    }
    internal Action<string>? MultipartJournalPending {get;set;}
    private static async Task SaveUploadJournalAsync(string path,UploadRecord record,CancellationToken token)
    {
        var temporary=path+".tmp";
        try
        {
            await using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {await JsonSerializer.SerializeAsync(output,record,cancellationToken:token).ConfigureAwait(false);await output.FlushAsync(token).ConfigureAwait(false);output.Flush(true);}
            File.Move(temporary,path,true);
        }
        finally{File.Delete(temporary);}
    }
    private async Task<(UploadRecord Record,string Journal)> BeginUploadAsync(string key, CancellationToken token)
    {
        Directory.CreateDirectory(_work);var client=Guid.NewGuid().ToString("N");var record=new UploadRecord(_bucket,key,ClientToken:client,StartedUtc:DateTimeOffset.UtcNow);
        var journal=Path.Combine(_work,client+".multipart.json");await SaveUploadJournalAsync(journal,record,token).ConfigureAwait(false);MultipartJournalPending?.Invoke(journal);
        var response = await Call(() => _client.InitiateMultipartUploadAsync(new() { BucketName = _bucket, Key = key }, token), token, true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(response.UploadId)) throw new SourceOutcomeUnknownException("S3 分片初始化", new IOException("服务器未返回 UploadId。"));
        record=record with{UploadId=response.UploadId};
        try{await SaveUploadJournalAsync(journal,record,token).ConfigureAwait(false);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(20));await AbortAsync(record,cleanup.Token).ConfigureAwait(false);File.Delete(journal);}
            catch(Exception cleanup)when(cleanup is IOException or UnauthorizedAccessException or OperationCanceledException){ }
            throw;
        }
        return(record,journal);
    }
    private async Task<UploadPartResponse> PartAsync(string key, string id, int part, byte[] bytes, int count, CancellationToken token)
        => await Call(() => _client.UploadPartAsync(new() { BucketName = _bucket, Key = key, UploadId = id, PartNumber = part, PartSize = count,
            InputStream = new MemoryStream(bytes, 0, count, false), UseChunkEncoding = false }, token), token, true).ConfigureAwait(false);
    private async Task AbortAsync(UploadRecord upload, CancellationToken token)
    {
        try { await Call(() => _client.AbortMultipartUploadAsync(new() { BucketName = upload.Bucket, Key = upload.Key, UploadId = upload.UploadId }, token), token, true).ConfigureAwait(false); }
        catch (FileNotFoundException) { }
    }
    private async Task VerifyMultipartAsync(string keyParent,CancellationToken token)
    {
        if (_multipartVerified) return; await _probeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_multipartVerified) return;
            var key = keyParent + ".hybridmount-probe-multipart-" + Guid.NewGuid().ToString("N");
            await PutAsync(key, new MemoryStream([17]), null, token).ConfigureAwait(false);
            try
            {
                foreach (var createOnly in new[] { true, false })
                {
                    var (record,journal)=await BeginUploadAsync(key,token).ConfigureAwait(false);var id=record.UploadId!;
                    try
                    {
                        var part = await PartAsync(key, id, 1, [31], 1, token).ConfigureAwait(false);
                        var probe=await CallCore(() => _client.CompleteMultipartUploadAsync(new() { BucketName = _bucket, Key = key, UploadId = id,
                            PartETags = [new PartETag(1, part.ETag)], IfNoneMatch = createOnly ? "*" : null, IfMatch = createOnly ? null : "\"hybridmount-never-match\"" }, token), token, true,ExpectedResult.PreconditionRejected).ConfigureAwait(false);
                        if(probe is null)continue;
                        throw new NotSupportedException("S3 分片完成接口未通过条件写验证。");
                    }
                    finally { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await AbortAsync(record,cleanup.Token).ConfigureAwait(false);File.Delete(journal); }
                }
                _multipartVerified = true;WriteCapabilities=WriteCapabilities with{MultipartComplete=CapabilitySupport.Supported};
            }
            finally { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await DeleteKeyAsync(key, null, cleanup.Token).ConfigureAwait(false); }
        }
        finally { _probeGate.Release(); }
    }
    private async Task<(string? ETag,string? VersionId)> MultipartAsync(string key, Stream input, string? etag, CancellationToken token)
    {
        await VerifyMultipartAsync(key[..(key.LastIndexOf('/')+1)],token).ConfigureAwait(false);
        var (record,journal)=await BeginUploadAsync(key,token).ConfigureAwait(false);var id=record.UploadId!;
        var completed = false;
        try
        {
            var length = input.Length - input.Position;
            var partSize = (int)Math.Min(128L * 1024 * 1024, Math.Max(8L * 1024 * 1024, (length + 9999) / 10000));
            if ((length + partSize - 1) / partSize > 10000) throw new NotSupportedException("对象超过首版分片大小限制。");
            var bytes = ArrayPool<byte>.Shared.Rent(partSize);
            var parts = new List<PartETag>();
            try
            {
                for (var number = 1; length > 0; number++)
                {
                    var count = (int)Math.Min(partSize, length); await input.ReadExactlyAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false);
                    var part = await PartAsync(key, id, number, bytes, count, token).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(part.ETag)) throw new IOException("S3 分片缺少 ETag。");
                    parts.Add(new(number, part.ETag)); length -= count;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(bytes, true); }
            var receipt=await Call(() => _client.CompleteMultipartUploadAsync(new() { BucketName = _bucket, Key = key, UploadId = id, PartETags = parts,
                IfMatch = etag, IfNoneMatch = etag is null ? "*" : null }, token), token, true).ConfigureAwait(false);
            completed = true; File.Delete(journal);return(receipt.ETag,ImmutableVersion(receipt.VersionId));
        }
        finally
        {
            if (!completed)
            {
                try { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await AbortAsync(record, cleanup.Token).ConfigureAwait(false); File.Delete(journal); }
                catch (Exception error)when(error is IOException or OperationCanceledException or UnauthorizedAccessException) { /* 保留本程序的 UploadId，下次连接只清理该记录。 */ }
            }
        }
    }
    public async Task<SourceEntry> CreateDirectoryAsync(string path, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyPutCapabilitiesAsync(RelativePaths.Parent(path),token).ConfigureAwait(false);RequirePut(true);
        if(await TryGetMetadataAsync(path,token).ConfigureAwait(false) is not null)throw new SourceConflictException("S3 名称或前缀已存在。");
        await PutAsync(Key(path) + "/", Stream.Null, null, token).ConfigureAwait(false);
        return await GetMetadataAsync(path, token).ConfigureAwait(false);
    }
    public async Task DeleteAsync(string path, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyDeleteAsync(token).ConfigureAwait(false);
        var entry = await GetMetadataAsync(path, token).ConfigureAwait(false);
        if (entry.Version != expectedVersion) throw new SourceConflictException("S3 删除版本变化。");
        if (entry.IsDirectory)
        {
            await foreach (var _ in ListAsync(path, token).ConfigureAwait(false)) throw new NotSupportedException("S3 目录非空，拒绝隐式递归删除。");
            if (Version(entry).Virtual) return;
        }
        // 不传 VersionId：版本化桶创建删除标记，绝不永久清理历史版本。
        await DeleteKeyAsync(Key(path) + (entry.IsDirectory ? "/" : ""), RequiredETag(Version(entry)), token).ConfigureAwait(false);
    }

}
