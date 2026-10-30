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
public sealed class S3FileSource : IFileSource, IReadDiagnosticsSource
{
    private readonly IAmazonS3 _client;
    private readonly string _diagnosticContext;
    private sealed class ResponseHints { internal string? Region,RequestId; internal bool Certificate; }
    private static readonly AsyncLocal<ResponseHints?> Hints=new();
    private readonly string _bucket, _prefix, _work;
    private readonly long _multipartThreshold;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private bool _writesVerified, _copyVerified, _multipartVerified;
    private sealed record ObjectVersion(string? ETag, string? VersionId, bool Virtual = false);
    private sealed record UploadRecord(string Bucket, string Key, string UploadId);
    public event Action<SourceReadDiagnostic>? ReadDiagnostic;
    public FileSourceCapabilities Capabilities { get; private set; } = new(CapabilitySupport.Unknown, CapabilitySupport.Unknown,
        CapabilitySupport.Unknown, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported, SourceCaseSensitivity.Sensitive);
    public S3FileSource(FileSourceConfiguration options, SourceCredentials credentials, string workDirectory, long multipartThreshold = 16L * 1024 * 1024, IAmazonS3? client = null)
    {
        var endpoint = ValidateOptions(options);
        _diagnosticContext=$"SigV4；签名区域 {options.Region}"+(options.Region=="us-east-1"?"（默认值，尚待服务确认）":"（配置值）")+
            $"；寻址 {(options.PathStyle?"Path-Style":"Virtual-Host")}；凭据来源 {(options.UseDefaultCredentials?"SDK 默认凭据链":"表单／DPAPI 访问密钥")}";

        _bucket = options.Bucket; _prefix = options.Prefix.Trim('/').Length == 0 ? "" : RelativePaths.Normalize(options.Prefix.Trim('/')) + "/";
        _work = workDirectory; _multipartThreshold = Math.Max(5L * 1024 * 1024, multipartThreshold);
        if (client is not null) { _client = client; return; }
        var config = new AmazonS3Config { ServiceURL = endpoint.AbsoluteUri.TrimEnd('/'), AuthenticationRegion = options.Region,
            ForcePathStyle = options.PathStyle, UseHttp = endpoint.Scheme == "http", MaxErrorRetry = 0,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED, ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            HttpClientFactory = new NoRedirectHttpFactory(), CacheHttpClient = false, DisableLogging = true };
        if (options.UseDefaultCredentials) _client = new AmazonS3Client(config);
        else
        {
            if (string.IsNullOrWhiteSpace(credentials.AccessKey) || string.IsNullOrWhiteSpace(credentials.SecretKey)) throw new ArgumentException("请填写 S3 访问密钥或选择 SDK 默认凭据链。");
            AWSCredentials identity = credentials.SessionToken.Length == 0 ? new BasicAWSCredentials(credentials.AccessKey, credentials.SecretKey)
                : new SessionAWSCredentials(credentials.AccessKey, credentials.SecretKey, credentials.SessionToken);
            _client = new AmazonS3Client(identity, config);
        }
    }
    /// <summary>本地表单提交前校验，错误不持久化成不可编辑的挂载根。</summary>
    public static Uri ValidateOptions(FileSourceConfiguration options)
    {
        var endpoint=FileSourceFactory.Endpoint(options);
        if(endpoint.AbsolutePath!="/"||options.Bucket.Length is <3 or >63||options.Bucket.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('.' or '-'))||
            string.IsNullOrWhiteSpace(options.Region)||options.Region.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c!='-'))
            throw new ArgumentException("请检查 S3 接入点、桶名与签名区域；接入点只包含协议与主机。");
        if(options.Prefix.Length!=0)RelativePaths.Normalize(options.Prefix.Trim('/'));
        return endpoint;
    }
    private sealed class NoRedirectHttpFactory : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig config) => new(new DiagnosticHandler(new SocketsHttpHandler { AllowAutoRedirect = false,
            MaxConnectionsPerServer = 4, ConnectTimeout = TimeSpan.FromSeconds(20) })) { Timeout = TimeSpan.FromMinutes(2) };
    }
    /// <summary>仅保留有限的错误字段；区域不通过 SDK 私有字段读取，也不自动改写配置。</summary>
    private sealed class DiagnosticHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            HttpResponseMessage response;
            try { response=await base.SendAsync(request,token).ConfigureAwait(false); }
            catch(HttpRequestException error)
            {
                if(Hints.Value is {} failed && (error.HttpRequestError==HttpRequestError.SecureConnectionError || error.InnerException is System.Security.Authentication.AuthenticationException))failed.Certificate=true;
                throw;
            }
            if(Hints.Value is {} hints)
            {
                if(response.Headers.TryGetValues("x-amz-bucket-region",out var regions))hints.Region=regions.FirstOrDefault();
                if(response.Headers.TryGetValues("x-amz-request-id",out var requests))hints.RequestId=requests.FirstOrDefault();
                if(!response.IsSuccessStatusCode && response.Content.Headers.ContentLength is >0 and <=65536)
                {
                    try
                    {
                        await response.Content.LoadIntoBufferAsync(65536,token).ConfigureAwait(false);
                        var bytes=await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                        using var reader=System.Xml.XmlReader.Create(new MemoryStream(bytes),new System.Xml.XmlReaderSettings
                        {DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536});
                        var xml=System.Xml.Linq.XDocument.Load(reader);
                        hints.Region??=xml.Root?.Elements().FirstOrDefault(e=>e.Name.LocalName=="Region")?.Value;
                    }
                    catch(System.Xml.XmlException) { }
                }
            }
            return response;
        }
    }
    private string Key(string path) => _prefix + RelativePaths.Normalize(path);
    private static string NonRoot(string path) => RelativePaths.Normalize(path) is { Length: > 0 } result ? result : throw new ArgumentException("不能修改源根。");
    private static ObjectVersion Version(SourceEntry entry) => JsonSerializer.Deserialize<ObjectVersion>(entry.Version) ?? throw new IOException("S3 版本无效。");
    private static ObjectVersion Version(string expected) => JsonSerializer.Deserialize<ObjectVersion>(expected) ?? throw new IOException("S3 版本无效。");
    private static string RequiredETag(ObjectVersion version) => EntityTagHeaderValue.TryParse(version.ETag, out var tag) && !tag.IsWeak
        ? version.ETag! : throw new NotSupportedException("S3 对象缺少有效 ETag，拒绝条件修改。");
    private static string? ImmutableVersion(string? value) => string.IsNullOrEmpty(value) || value == "null" ? null : value;
    /// <summary>不输出 SDK 原始异常、服务端 XML 或 URL；修改请求不自动重试。</summary>
    private async Task<T> Call<T>(Func<Task<T>> action, CancellationToken token, bool mutation = false, [CallerMemberName] string stage = "")
    {
        token.ThrowIfCancellationRequested();
        var previousHints=Hints.Value;var hints=new ResponseHints();Hints.Value=hints;
        try { return await action().ConfigureAwait(false); }
        catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        { throw new SourceConflictException("S3 对象版本或目标创建条件不满足。"); }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound && error.ErrorCode != "NoSuchBucket" && stage != nameof(ProbeAsync))
        { throw new FileNotFoundException("S3 对象或桶不存在。"); }
        catch (Exception error) when (error is AmazonServiceException or AmazonClientException or HttpRequestException or IOException or OperationCanceledException)
        {
            if (mutation && (error is not AmazonS3Exception service || (int)service.StatusCode >= 500 || (int)service.StatusCode == 0))
                throw new SourceOutcomeUnknownException("S3 修改", new SourceConnectionException(Diagnose(error,stage,hints)));
            if (error is OperationCanceledException) throw;
            throw new SourceConnectionException(Diagnose(error,stage,hints));
        }
        finally { Hints.Value=previousHints; }
    }
    public async Task ProbeAsync(CancellationToken token = default)
    {
        var page=await Call(() => _client.ListObjectsV2Async(new() { BucketName = _bucket, Prefix = _prefix, Delimiter = "/", MaxKeys = 1 }, token), token).ConfigureAwait(false);
        if(page.HttpStatusCode!=HttpStatusCode.OK || page.IsTruncated is null)
            throw new SourceConnectionException(new(nameof(ProbeAsync),ConnectionFailureKind.Service,(int)page.HttpStatusCode,null,null,null,"服务未返回完整的 S3 ListObjectsV2 响应；"+_diagnosticContext));

    }
    /// <summary>将 SDK 错误转换为有限字段；不读取 ResponseBody、Message 或授权信息。</summary>
    private ConnectionDiagnostic Diagnose(Exception error,string stage,ResponseHints hints)
    {
        static string? Field(string? value,int max=160) => !string.IsNullOrEmpty(value) && value.Length<=max &&
            value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_' or '/' or '+' or '=' or '.')?value:null;
        var service=error as AmazonS3Exception;var status=service is null?0:(int)service.StatusCode;
        var code=Field(service?.ErrorCode);var kind=ConnectionFailureKind.Service;
        var explanation="S3 服务返回非预期响应，请依据服务码与请求 ID 排查。";
        if(code is "SignatureDoesNotMatch" or "AuthorizationHeaderMalformed" or "InvalidRegion" || status is 301 or 307)
        {kind=ConnectionFailureKind.Signature;explanation="签名或签名上下文不匹配，请核对区域、Path-Style、路径及系统时间。";}
        else if(status==401 || code is "InvalidAccessKeyId" or "ExpiredToken" or "InvalidToken" or "TokenRefreshRequired")
        {kind=ConnectionFailureKind.Authentication;explanation="认证未通过；请核对凭据来源与有效期，当前响应不能单独确定密钥或区域是否正确。";}
        else if(status==403)
        {kind=ConnectionFailureKind.Permission;explanation="访问被拒绝，请核对桶及前缀权限、策略或服务端认证约束。";}
        else if(status==404 || code=="NoSuchBucket")
        {kind=ConnectionFailureKind.NotFound;explanation="桶或请求资源不存在，请核对接入点与桶名。";}
        else if(status==0)
        {
            var certificate=hints.Certificate;
            for(Exception? nested=error;nested is not null;nested=nested.InnerException)
                if(nested is System.Security.Authentication.AuthenticationException || nested is HttpRequestException { HttpRequestError:HttpRequestError.SecureConnectionError })certificate=true;
            kind=certificate?ConnectionFailureKind.Certificate:ConnectionFailureKind.Network;
            explanation=certificate?"TLS 或证书验证失败，请核对主机名与证书链。":"请求未获得 HTTP 响应，请核对网络、DNS、代理及凭据来源。";
        }
        return new(stage,kind,status==0?null:status,code,Field(service?.RequestId)??Field(hints.RequestId),Field(hints.Region,64),explanation+"；"+_diagnosticContext);
    }
    private static SourceEntry VirtualDirectory(string path) => new("", path, true, 0, DateTimeOffset.UnixEpoch, JsonSerializer.Serialize(new ObjectVersion(null, null, true)));
    private static SourceEntry Metadata(string path, GetObjectMetadataResponse head, bool directory)
    {
        if (head.ContentLength < 0 || head.LastModified is null || string.IsNullOrEmpty(head.ETag) || (directory && head.ContentLength != 0)) throw new IOException("S3 元数据不完整或目录标记不是空对象。");
        return new("", path, directory, directory ? 0 : head.ContentLength, new DateTimeOffset(DateTime.SpecifyKind(head.LastModified.Value, DateTimeKind.Utc)),
            JsonSerializer.Serialize(new ObjectVersion(head.ETag, ImmutableVersion(head.VersionId))));
    }
    private Task<GetObjectMetadataResponse> HeadAsync(string key, CancellationToken token)
        => Call(() => _client.GetObjectMetadataAsync(new() { BucketName = _bucket, Key = key }, token), token);
    public async Task<SourceEntry> GetMetadataAsync(string path, CancellationToken token = default)
    {
        path = RelativePaths.Normalize(path); if (path.Length == 0) return VirtualDirectory("");
        try { return Metadata(path, await HeadAsync(Key(path), token).ConfigureAwait(false), false); }
        catch (FileNotFoundException) { }
        try { return Metadata(path, await HeadAsync(Key(path) + "/", token).ConfigureAwait(false), true); }
        catch (FileNotFoundException) { }
        var list = await Call(() => _client.ListObjectsV2Async(new() { BucketName = _bucket, Prefix = Key(path) + "/", MaxKeys = 1 }, token), token).ConfigureAwait(false);
        if (list.S3Objects?.Count > 0 || list.CommonPrefixes?.Count > 0) return VirtualDirectory(path);
        throw new FileNotFoundException("S3 条目不存在。");
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
    private Task<PutObjectResponse> PutAsync(string key, Stream stream, string? etag, CancellationToken token)
        => Call(() => _client.PutObjectAsync(new() { BucketName = _bucket, Key = key, InputStream = new BorrowedStream(stream),
            AutoCloseStream = false, AutoResetStreamPosition = false, UseChunkEncoding = false, IfMatch = etag, IfNoneMatch = etag is null ? "*" : null }, token), token, true);
    private Task<DeleteObjectResponse> DeleteKeyAsync(string key, string? etag, CancellationToken token)
        => Call(() => _client.DeleteObjectAsync(new() { BucketName = _bucket, Key = key, IfMatch = etag }, token), token, true);
    /// <summary>随机对象验证 If-None-Match、If-Match、条件删除及目标复制条件，不碰用户对象。</summary>
    private async Task VerifyWritesAsync(CancellationToken token)
    {
        if (_writesVerified) return; await _probeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_writesVerified) return;
            if (Capabilities.ConditionalWrite == CapabilitySupport.Unsupported) throw new NotSupportedException("此 S3 服务未通过条件写验证。");
            var first = _prefix + ".hybridmount-probe-" + Guid.NewGuid().ToString("N"); var second = first + "-destination"; var created = new List<string>();
            try
            {
                await PutAsync(first, new MemoryStream([17, 31]), null, token).ConfigureAwait(false); created.Add(first);
                await PutAsync(second, new MemoryStream([47]), null, token).ConfigureAwait(false); created.Add(second);
                async Task MustConflict(Func<Task> operation)
                {
                    try { await operation().ConfigureAwait(false); }
                    catch (SourceConflictException) { return; }
                    Capabilities = Capabilities with { ConditionalWrite = CapabilitySupport.Unsupported };
                    throw new NotSupportedException("S3 服务忽略条件修改，已拒绝用户内容写入。");
                }
                await MustConflict(async () => await PutAsync(first, new MemoryStream([99]), null, token).ConfigureAwait(false)).ConfigureAwait(false);
                var wrong = "\"hybridmount-never-match-" + Guid.NewGuid().ToString("N") + "\"";
                await MustConflict(async () => await PutAsync(first, new MemoryStream([99]), wrong, token).ConfigureAwait(false)).ConfigureAwait(false);
                await MustConflict(async () => await DeleteKeyAsync(first, wrong, token).ConfigureAwait(false)).ConfigureAwait(false);
                var head = await HeadAsync(first, token).ConfigureAwait(false);
                try
                {
                    await Call(() => _client.CopyObjectAsync(new() { SourceBucket = _bucket, SourceKey = first, DestinationBucket = _bucket, DestinationKey = second,
                        ETagToMatch = head.ETag, IfNoneMatch = "*" }, token), token, true).ConfigureAwait(false);
                    _copyVerified = false;
                }
                catch (SourceConflictException) { _copyVerified = true; }
                catch (IOException) { _copyVerified = false; }
                _writesVerified = true; Capabilities = Capabilities with { ConditionalWrite = CapabilitySupport.Supported };
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                foreach (var key in created) await DeleteKeyAsync(key, null, cleanup.Token).ConfigureAwait(false);
            }
        }
        finally { _probeGate.Release(); }
    }
    /// <summary>在条件请求之外核对目录映射；对象键与同名前缀不能同时作为可写目标。</summary>
    private async Task CheckTargetAsync(string path,string? expectedVersion,CancellationToken token)
    {
        var parent=RelativePaths.Parent(path);
        if(parent.Length>0 && !(await GetMetadataAsync(parent,token).ConfigureAwait(false)).IsDirectory)throw new SourceConflictException("S3 父项是文件，不能创建子项。");
        try
        {
            var current=await GetMetadataAsync(path,token).ConfigureAwait(false);
            if(current.IsDirectory||expectedVersion is null||current.Version!=expectedVersion)throw new SourceConflictException("S3 目标已存在、是目录或版本不匹配。");
        }
        catch(FileNotFoundException){if(expectedVersion is not null)throw new SourceConflictException("S3 更新目标已不存在。");}
    }
    public async Task<SourceEntry> UploadAsync(string path, Stream content, string? expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyWritesAsync(token).ConfigureAwait(false);
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
            if (input.Length - input.Position >= _multipartThreshold) await MultipartAsync(Key(path), input, etag, token).ConfigureAwait(false);
            else await PutAsync(Key(path), input, etag, token).ConfigureAwait(false);
            try { return await GetMetadataAsync(path, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { throw new SourceOutcomeUnknownException("S3 上传后核对", new IOException("写入成功后未能确认元数据。")); }
        }
        finally { if (input != content) await input.DisposeAsync().ConfigureAwait(false); if (temporary is not null) File.Delete(temporary); }
    }
    private async Task<string> BeginUploadAsync(string key, CancellationToken token)
    {
        var response = await Call(() => _client.InitiateMultipartUploadAsync(new() { BucketName = _bucket, Key = key }, token), token, true).ConfigureAwait(false);
        if (string.IsNullOrEmpty(response.UploadId)) throw new SourceOutcomeUnknownException("S3 分片初始化", new IOException("服务器未返回 UploadId。"));
        return response.UploadId;
    }
    private async Task<UploadPartResponse> PartAsync(string key, string id, int part, byte[] bytes, int count, CancellationToken token)
        => await Call(() => _client.UploadPartAsync(new() { BucketName = _bucket, Key = key, UploadId = id, PartNumber = part, PartSize = count,
            InputStream = new MemoryStream(bytes, 0, count, false), UseChunkEncoding = false }, token), token, true).ConfigureAwait(false);
    private async Task AbortAsync(UploadRecord upload, CancellationToken token)
    {
        try { await Call(() => _client.AbortMultipartUploadAsync(new() { BucketName = upload.Bucket, Key = upload.Key, UploadId = upload.UploadId }, token), token, true).ConfigureAwait(false); }
        catch (FileNotFoundException) { }
    }
    private async Task VerifyMultipartAsync(CancellationToken token)
    {
        if (_multipartVerified) return; await _probeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_multipartVerified) return;
            var key = _prefix + ".hybridmount-probe-multipart-" + Guid.NewGuid().ToString("N");
            await PutAsync(key, new MemoryStream([17]), null, token).ConfigureAwait(false);
            try
            {
                foreach (var createOnly in new[] { true, false })
                {
                    var id = await BeginUploadAsync(key, token).ConfigureAwait(false);
                    try
                    {
                        var part = await PartAsync(key, id, 1, [31], 1, token).ConfigureAwait(false);
                        try
                        {
                            await Call(() => _client.CompleteMultipartUploadAsync(new() { BucketName = _bucket, Key = key, UploadId = id,
                                PartETags = [new PartETag(1, part.ETag)], IfNoneMatch = createOnly ? "*" : null, IfMatch = createOnly ? null : "\"hybridmount-never-match\"" }, token), token, true).ConfigureAwait(false);
                        }
                        catch (SourceConflictException) { continue; }
                        throw new NotSupportedException("S3 分片完成接口未通过条件写验证。");
                    }
                    finally { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await AbortAsync(new(_bucket, key, id), cleanup.Token).ConfigureAwait(false); }
                }
                _multipartVerified = true;
            }
            finally { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await DeleteKeyAsync(key, null, cleanup.Token).ConfigureAwait(false); }
        }
        finally { _probeGate.Release(); }
    }
    private async Task MultipartAsync(string key, Stream input, string? etag, CancellationToken token)
    {
        await VerifyMultipartAsync(token).ConfigureAwait(false);
        var id = await BeginUploadAsync(key, token).ConfigureAwait(false); var record = new UploadRecord(_bucket, key, id);
        var journal = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".multipart.json");
        var completed = false;
        try
        {
            Directory.CreateDirectory(_work);
            var journalTemporary=journal+".tmp";
            try { await File.WriteAllTextAsync(journalTemporary,JsonSerializer.Serialize(record),token).ConfigureAwait(false);File.Move(journalTemporary,journal,true); }
            finally{File.Delete(journalTemporary);}
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
            await Call(() => _client.CompleteMultipartUploadAsync(new() { BucketName = _bucket, Key = key, UploadId = id, PartETags = parts,
                IfMatch = etag, IfNoneMatch = etag is null ? "*" : null }, token), token, true).ConfigureAwait(false);
            completed = true; File.Delete(journal);
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
    /// <summary>单独恢复本程序已知分片；读取连接可在恢复失败时继续，探测绝不调用此方法。</summary>
    public async Task RecoverKnownUploadsAsync(CancellationToken token=default)
    {
        if (!Directory.Exists(_work)) return;
        foreach (var path in Directory.EnumerateFiles(_work, "*.multipart.json"))
        {
            var record = JsonSerializer.Deserialize<UploadRecord>(await File.ReadAllTextAsync(path, token).ConfigureAwait(false)) ?? throw new IOException("分片恢复记录损坏。");
            if (record.Bucket != _bucket || !record.Key.StartsWith(_prefix, StringComparison.Ordinal) || !Path.GetFileName(path).EndsWith(".multipart.json")) throw new IOException("分片恢复范围不匹配。");
            await AbortAsync(record, token).ConfigureAwait(false); File.Delete(path);
        }
    }
    public async Task<SourceEntry> CreateDirectoryAsync(string path, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyWritesAsync(token).ConfigureAwait(false);
        try { await GetMetadataAsync(path, token).ConfigureAwait(false); throw new SourceConflictException("S3 名称或前缀已存在。"); }
        catch (FileNotFoundException) { }
        await PutAsync(Key(path) + "/", Stream.Null, null, token).ConfigureAwait(false);
        return await GetMetadataAsync(path, token).ConfigureAwait(false);
    }
    public async Task DeleteAsync(string path, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyWritesAsync(token).ConfigureAwait(false);
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
    private async Task<SourceEntry> MoveFileAsync(SourceEntry entry, string destination, CancellationToken token)
    {
        await CheckTargetAsync(destination,null,token).ConfigureAwait(false);
        var version = Version(entry); var key = Key(destination);
        if (_copyVerified && entry.Size <= 5L * 1024 * 1024 * 1024)
            await Call(() => _client.CopyObjectAsync(new() { SourceBucket = _bucket, SourceKey = Key(entry.Path), SourceVersionId = version.VersionId,
                ETagToMatch = RequiredETag(version), DestinationBucket = _bucket, DestinationKey = key, IfNoneMatch = "*" }, token), token, true).ConfigureAwait(false);
        else
        {
            Directory.CreateDirectory(_work); var path = Path.Combine(_work, Guid.NewGuid().ToString("N") + ".move.tmp");
            try
            {
                await using var spool = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous);
                var bytes = new byte[1024 * 1024]; long offset = 0;
                while (offset < entry.Size) { var count = await ReadRangeAsync(entry, offset, bytes, token).ConfigureAwait(false); await spool.WriteAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false); offset += count; }
                spool.Position = 0; await UploadAsync(destination, spool, null, token).ConfigureAwait(false);
            }
            finally { File.Delete(path); }
        }
        SourceEntry copy;
        try { copy=await GetMetadataAsync(destination,token).ConfigureAwait(false); }
        catch(Exception error)when(error is IOException or OperationCanceledException)
        { throw new SourcePartialMoveException([new(entry.Path,"Copied","复制请求成功，但目标核对未完成；源保留。")]); }
        if (copy.Size != entry.Size) throw new SourcePartialMoveException([new(entry.Path, "Copied", "目标大小未通过核对，源保留。")]);
        try { await DeleteAsync(entry.Path, entry.Version, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        { throw new SourcePartialMoveException([new(entry.Path, "Copied", "复制完成但源删除未确认；请核对两端。")]); }
        return copy;
    }
    public async Task<SourceEntry> MoveAsync(string path, string destination, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); destination = NonRoot(destination); if (RelativePaths.Contains(path, destination)) throw new ArgumentException("不能移动到自身或子目录。");
        await VerifyWritesAsync(token).ConfigureAwait(false); var entry = await GetMetadataAsync(path, token).ConfigureAwait(false);
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

/// <summary>目录/文件移动不是事务；成功的目标和仍保留的源通过逐项结果交付调用者。</summary>
public sealed class SourcePartialMoveException(IReadOnlyList<MountItemResult> results) : IOException("S3 移动部分完成，请核对源和目标。")
{
    public IReadOnlyList<MountItemResult> Results { get; } = results;
}
