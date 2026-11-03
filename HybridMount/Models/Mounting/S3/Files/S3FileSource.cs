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

/// <summary>SigV4 S3 通用桶驱动；版本读取、分页、条件写与复制后删除分别处理。</summary>
public sealed partial class S3FileSource : IFileSource, IReadDiagnosticsSource, IUploadDiagnosticsSource, IRemoteChangeSource, IRemoteSnapshotSource
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket, _prefix, _work;
    private readonly long _multipartThreshold;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private bool _writesVerified, _copyVerified, _multipartVerified;
    public event Action<SourceReadDiagnostic>? ReadDiagnostic;
    public FileSourceCapabilities Capabilities { get; private set; } = new(CapabilitySupport.Unknown, CapabilitySupport.Unknown,
        CapabilitySupport.Unknown, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported, SourceCaseSensitivity.Sensitive);
    private static SourceEntry VirtualDirectory(string path) => new("", path, true, 0, DateTimeOffset.UnixEpoch, JsonSerializer.Serialize(new ObjectVersion(null, null, true)));
    private static SourceEntry Metadata(string path, GetObjectMetadataResponse head, bool directory)
    {
        if (head.ContentLength < 0 || head.LastModified is null || string.IsNullOrEmpty(head.ETag) || (directory && head.ContentLength != 0)) throw new IOException("S3 元数据不完整或目录标记不是空对象。");
        return new("", path, directory, directory ? 0 : head.ContentLength, new DateTimeOffset(DateTime.SpecifyKind(head.LastModified.Value, DateTimeKind.Utc)),
            JsonSerializer.Serialize(new ObjectVersion(head.ETag, ImmutableVersion(head.VersionId))));
    }
    private Task<GetObjectMetadataResponse> HeadAsync(string key, CancellationToken token)
        => Call(() => _client.GetObjectMetadataAsync(new() { BucketName = _bucket, Key = key }, token), token);
    public async Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)
        => await TryGetMetadataAsync(path,token).ConfigureAwait(false)??throw new FileNotFoundException("S3 条目不存在。");
    /// <summary>区分对象、目录标记及虚拟前缀；仅明确 404 返回空，不转译成多层异常。</summary>
    public async Task<SourceEntry?> TryGetMetadataAsync(string path,CancellationToken token=default)
    {
        path=RelativePaths.Normalize(path);if(path.Length==0)return VirtualDirectory("");
        async Task<GetObjectMetadataResponse?> Head(string key)=>await CallCore(()=>_client.GetObjectMetadataAsync(new(){BucketName=_bucket,Key=key},token),token,false,ExpectedResult.Missing,nameof(GetMetadataAsync)).ConfigureAwait(false);
        var head=await Head(Key(path)).ConfigureAwait(false);if(head is not null)return Metadata(path,head,false);
        head=await Head(Key(path)+"/").ConfigureAwait(false);if(head is not null)return Metadata(path,head,true);
        var list=await Call(()=>_client.ListObjectsV2Async(new(){BucketName=_bucket,Prefix=Key(path)+"/",MaxKeys=1},token),token).ConfigureAwait(false);
        if(list.IsTruncated is null||list.IsTruncated==true&&!(list.S3Objects?.Count>0||list.CommonPrefixes?.Count>0))
            throw new IOException("S3 存在性查询不完整，不能确认远端删除。");
        return list.S3Objects?.Count>0||list.CommonPrefixes?.Count>0?VirtualDirectory(path):null;
    }
    public async IAsyncEnumerable<SourceEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken token = default)
    {
        path = RelativePaths.Normalize(path); var prefix = Key(path) + (path.Length == 0 ? "" : "/");
        var candidates = new Dictionary<string, (bool Directory, S3Object? Object)>(StringComparer.Ordinal);
        var folded = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, bool directory, S3Object? item)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("S3 枚举越过前缀。");
            var relative = key[_prefix.Length..].TrimEnd('/');
            if (relative == path) { if (item?.Size != 0) throw new IOException("S3 目录标记非空。"); return; }
            RelativePaths.Normalize(relative);
            if (RelativePaths.Parent(relative) != path) throw new IOException("S3 服务器未遵循 delimiter 深度 1。");
            if (folded.TryGetValue(relative, out var original) && original != relative) throw new IOException("S3 名称大小写冲突，不能映射到本地。");
            folded[relative] = relative;
            if (candidates.TryGetValue(relative, out var existing))
            {
                if (!directory || !existing.Directory) throw new IOException("S3 对象与目录前缀冲突。");
                if (existing.Object is null && item is not null) candidates[relative] = (true, item);
            }
            else candidates.Add(relative, (directory, item));
        }
        string? continuation = null; var tokens = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var page = await Call(() => _client.ListObjectsV2Async(new() { BucketName = _bucket, Prefix = prefix, Delimiter = "/", ContinuationToken = continuation, MaxKeys = 1000 }, token), token).ConfigureAwait(false);
            foreach (var item in page.S3Objects ?? [])
            {
                if (item.Key is null || item.Size is null || item.Size < 0) throw new IOException("S3 枚举缺少对象键或大小。");
                Add(item.Key, item.Key.EndsWith('/'), item);
            }
            foreach (var directory in page.CommonPrefixes ?? []) Add(directory, true, null);
            if (page.IsTruncated is null) throw new IOException("S3 枚举缺少分页完成标记。");
            continuation = page.IsTruncated.Value ? page.NextContinuationToken : null;
            if (page.IsTruncated.Value && (string.IsNullOrEmpty(continuation) || !tokens.Add(continuation))) throw new IOException("S3 分页游标缺失或重复，快照不完整。");
        } while (continuation is not null);
        // 所有页面收齐后再输出，单个文件 HEAD 固定 VersionId，拒绝枚举过程中发生的版本变化。
        foreach (var (relative, candidate) in candidates.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (candidate.Directory && candidate.Object is null) { var directory=await GetMetadataAsync(relative,token).ConfigureAwait(false);if(!directory.IsDirectory)throw new IOException("S3 文件与目录冲突。");yield return directory; continue; }
            var head = await HeadAsync(Key(relative) + (candidate.Directory ? "/" : ""), token).ConfigureAwait(false);
            if (head.ETag != candidate.Object!.ETag || head.ContentLength != candidate.Object.Size) throw new SourceConflictException("S3 枚举后对象发生变化，请重扫。");
            yield return Metadata(relative, head, candidate.Directory);
        }
    }
    public async ValueTask<int> ReadRangeAsync(SourceEntry entry, long offset, Memory<byte> buffer, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (entry.IsDirectory || offset > entry.Size) throw new ArgumentException("S3 读取范围无效。");
        var count = (int)Math.Min(buffer.Length, entry.Size - offset); if (count == 0) return 0;
        var version = Version(entry); var etag = RequiredETag(version);
        using var response = await Call(() => _client.GetObjectAsync(new() { BucketName = _bucket, Key = Key(entry.Path),
            VersionId = version.VersionId, EtagToMatch = etag, ByteRange = new ByteRange(offset, offset + count - 1) }, token), token).ConfigureAwait(false);
        if(response.HttpStatusCode==HttpStatusCode.OK)Capabilities=Capabilities with{RangeRead=CapabilitySupport.Unsupported};
        if (response.HttpStatusCode != HttpStatusCode.PartialContent || !ContentRangeHeaderValue.TryParse(response.ContentRange, out var range) ||
            range.Unit != "bytes" || range.From != offset || range.To != offset + count - 1 || range.Length != entry.Size || response.ContentLength != count ||
            response.ETag != etag || (version.VersionId is not null && response.VersionId != version.VersionId))
            throw new SourceConflictException("S3 范围、长度或版本响应不匹配。");
        var received=0;var completed=false;
        try
        {
            while(received<count){var read=await response.ResponseStream.ReadAsync(buffer[received..count],token).ConfigureAwait(false);if(read==0)throw new EndOfStreamException();received+=read;}
            var extra=await response.ResponseStream.ReadAsync(new byte[1],token).ConfigureAwait(false);received+=extra;if(extra!=0)throw new IOException("S3 范围响应包含多余字节。");
            Capabilities=Capabilities with{RangeRead=CapabilitySupport.Supported};completed=true;return count;
        }
        finally{ReadDiagnostic?.Invoke(new("range",received,null,completed));}
    }
    /// <summary>复制回执与目标版本一致才允许条件删源；多个请求不构成原子移动。</summary>
    private async Task<SourceEntry> MoveFileAsync(SourceEntry entry, string destination, CancellationToken token)
    {
        await CheckTargetAsync(destination,null,token).ConfigureAwait(false);
        var version=Version(entry);var key=Key(destination);var acknowledged=false;
        try
        {
            ObjectVersion receipt;
            if(_copyVerified&&entry.Size<=5L*1024*1024*1024)
            {
                var copied=await Call(()=>_client.CopyObjectAsync(new(){SourceBucket=_bucket,SourceKey=Key(entry.Path),SourceVersionId=version.VersionId,
                    ETagToMatch=RequiredETag(version),DestinationBucket=_bucket,DestinationKey=key,IfNoneMatch="*"},token),token,true).ConfigureAwait(false);
                acknowledged=true;receipt=new(copied.ETag,ImmutableVersion(copied.VersionId));
            }
            else
            {
                Directory.CreateDirectory(_work);var path=Path.Combine(_work,Guid.NewGuid().ToString("N")+".move.tmp");
                try
                {
                    await using var spool=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,65536,FileOptions.Asynchronous);
                    var bytes=new byte[1024*1024];long offset=0;
                    while(offset<entry.Size){var count=await ReadRangeAsync(entry,offset,bytes,token).ConfigureAwait(false);await spool.WriteAsync(bytes.AsMemory(0,count),token).ConfigureAwait(false);offset+=count;}
                    spool.Position=0;var uploaded=await UploadAsync(destination,spool,null,token).ConfigureAwait(false);
                    acknowledged=true;receipt=Version(uploaded);
                }
                finally{File.Delete(path);}
            }
            token.ThrowIfCancellationRequested();
            var receiptTag=RequiredETag(receipt);
            var copy=await GetMetadataAsync(destination,token).ConfigureAwait(false);var current=Version(copy);
            if(copy.IsDirectory||copy.Size!=entry.Size||current.ETag!=receiptTag||
                receipt.VersionId is not null&&current.VersionId is not null&&receipt.VersionId!=current.VersionId)
                throw new SourcePartialMoveException([new(entry.Path,"Copied",$"目标 {destination} 与本次复制回执的类型、长度或版本不一致；源保留。")]);
            token.ThrowIfCancellationRequested();
            try{await DeleteAsync(entry.Path,entry.Version,token).ConfigureAwait(false);}
            catch(NotSupportedException)
            {throw new SourcePartialMoveException([new(entry.Path,"Copied",$"目标 {destination} 已核对；服务未通过可靠的条件删除验证，源保留。")]);}
            catch(Exception error)when(error is IOException or OperationCanceledException)
            {throw new SourcePartialMoveException([new(entry.Path,"Copied",$"目标 {destination} 已核对，但源删除未确认；请核对两端。")]);}
            return copy;
        }
        catch(SourcePartialMoveException){throw;}
        catch(Exception error)when(error is IOException or OperationCanceledException or NotSupportedException)
        {throw new SourcePartialMoveException([new(entry.Path,acknowledged?"Copied":"Unconfirmed",$"复制到 {destination} 或目标核对未确认；未执行后续源删除，请核对两端。")]);}
    }
    public async Task<SourceEntry> MoveAsync(string path, string destination, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); destination = NonRoot(destination); if (RelativePaths.Contains(path, destination)) throw new ArgumentException("不能移动到自身或子目录。");
        await VerifyPutCapabilitiesAsync(RelativePaths.Parent(destination),token).ConfigureAwait(false);RequirePut(true);await VerifyCopyAsync(token).ConfigureAwait(false); var entry = await GetMetadataAsync(path, token).ConfigureAwait(false);
        if (entry.Version != expectedVersion) throw new SourceConflictException("S3 移动版本变化。");
        if (!entry.IsDirectory) return await MoveFileAsync(entry, destination, token).ConfigureAwait(false);
        var snapshot = new List<SourceEntry>();
        async Task Collect(string parent) { await foreach (var child in ListAsync(parent, token).ConfigureAwait(false)) { snapshot.Add(child); if (child.IsDirectory) await Collect(child.Path).ConfigureAwait(false); } }
        await Collect(path).ConfigureAwait(false); var results = new List<MountItemResult>();
        try
        {
            var target = await CreateDirectoryAsync(destination, token).ConfigureAwait(false);
            foreach (var child in snapshot.OrderBy(item => item.Path.Length))
            {
                var name = destination + child.Path[path.Length..];
                if (child.IsDirectory) await CreateDirectoryAsync(name, token).ConfigureAwait(false);
                else await MoveFileAsync(child, name, token).ConfigureAwait(false);
                results.Add(new(child.Path, "Completed", child.IsDirectory ? "已创建目标目录。" : "已复制并移除源对象。"));
            }
            foreach (var directory in snapshot.Where(item => item.IsDirectory).Append(entry).OrderByDescending(item => item.Path.Length))
            {
                try { var current = await GetMetadataAsync(directory.Path, token).ConfigureAwait(false); await DeleteAsync(current.Path, current.Version, token).ConfigureAwait(false); }
                catch (FileNotFoundException) { }
            }
            return target;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or NotSupportedException)
        {
            if (error is SourcePartialMoveException partial) results.AddRange(partial.Results);
            results.Add(new(path, "Failed", "目录移动部分完成，未完成源内容保留；请核对逐项结果。")); throw new SourcePartialMoveException(results);
        }
    }
    public ValueTask DisposeAsync() { _client.Dispose(); _probeGate.Dispose(); return ValueTask.CompletedTask; }
}
