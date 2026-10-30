using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>独立 HTTP WebDAV；不依赖 WebClient。枚举、范围和写入条件均按响应验证。</summary>
public sealed partial class WebDavFileSource : IFileSource, IReadDiagnosticsSource, IUploadDiagnosticsSource
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly WholeFileCache _cache;
    private readonly SemaphoreSlim _writeProbe = new(1, 1);
    private sealed record InitialRange(long Offset,byte[] Bytes);
    private readonly SharedAsyncWork<(string Path,string Version),InitialRange> _initialReads=new();
    private bool _writesVerified;
    public event Action<SourceReadDiagnostic>? ReadDiagnostic;
    private sealed record VersionInfo(string? ETag, long Size, long ModifiedTicks);
    public FileSourceCapabilities Capabilities { get; private set; } = new(CapabilitySupport.Unknown,
        CapabilitySupport.Unknown, CapabilitySupport.Unknown, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported, SourceCaseSensitivity.Unknown);
    public WebDavFileSource(FileSourceConfiguration options, SourceCredentials credentials, WholeFileCache cache, HttpMessageHandler? handler = null)
    {
        _endpoint = FileSourceFactory.Endpoint(options); _cache = cache;
        _temporaryScope=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(_endpoint.AbsoluteUri+"\0"+credentials.UserName)));
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = TimeSpan.FromMinutes(2) };
        if (credentials.UserName.Contains(':')) throw new ArgumentException("WebDAV 用户名不能包含冒号。");
        if (credentials.UserName.Length != 0)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password)));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HybridMount/" + typeof(WebDavFileSource).Assembly.GetName().Version!.ToString(3));
    }
    private Uri Address(string path, bool directory = false)
    {
        path = RelativePaths.Normalize(path);
        var suffix = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_endpoint.AbsoluteUri + suffix + (directory && suffix.Length != 0 ? "/" : ""));
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, bool mutation = false)
    {
        token.ThrowIfCancellationRequested();
        if(mutation)await PrepareTemporaryRequestAsync(request,token).ConfigureAwait(false);
        try { var response=await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if(mutation)ObserveTemporaryResponse(request,response);return response; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            if (mutation) throw new SourceOutcomeUnknownException(request.Method.Method, new IOException("网络请求未确认完成。"));
            if(error is OperationCanceledException&&token.IsCancellationRequested)throw;
            if(error is OperationCanceledException)throw new IOException("WebDAV 请求超时，请检查网络及服务响应。");
            throw new IOException("WebDAV 连接失败，请检查网络、地址及证书。");
        }
    }
    private static void Success(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            throw new SourceConflictException("远端版本或目标创建条件不满足。");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("远端条目不存在。");
        if ((int)response.StatusCode is >=400 and <500)throw new SourceRequestRejectedException((int)response.StatusCode,response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden?"WebDAV 认证或权限不足，请检查账号与应用密码。":$"WebDAV 拒绝请求（HTTP {(int)response.StatusCode}）。");
        if (!response.IsSuccessStatusCode)
            throw new IOException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "WebDAV 认证或权限不足，请检查账号与应用密码。" : $"WebDAV 返回 HTTP {(int)response.StatusCode}；未跟随重定向。");
    }
    public async Task ProbeAsync(CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, _endpoint);
        using var response = await SendAsync(request, token).ConfigureAwait(false);
        // 有些服务器不实现 OPTIONS，但仍支持 PROPFIND；认证错误不得忽略。
        if (response.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)) Success(response);
        var root = await GetMetadataAsync("", token).ConfigureAwait(false);
        if (!root.IsDirectory) throw new IOException("WebDAV 根必须是集合目录。");
    }
    private string HrefPath(string href, Uri requestUri)
    {
        if (!Uri.TryCreate(requestUri, href, out var uri) || uri.Scheme != _endpoint.Scheme || uri.Authority != _endpoint.Authority || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new IOException("WebDAV href 指向其他服务器或包含查询。");
        var escaped = uri.AbsolutePath;
        if (!escaped.StartsWith(_endpoint.AbsolutePath, StringComparison.Ordinal))
        {
            if (escaped.TrimEnd('/') == _endpoint.AbsolutePath.TrimEnd('/')) return "";
            throw new IOException("WebDAV href 越过配置根目录。");
        }
        var remainder = escaped[_endpoint.AbsolutePath.Length..].TrimEnd('/');
        if (remainder.Length == 0) return "";
        var parts = remainder.Split('/').Select(part => Uri.UnescapeDataString(part)).ToArray();
        if (parts.Any(part => part.Contains('/') || part.Contains('\\'))) throw new IOException("WebDAV href 含编码分隔符。");
        return RelativePaths.Normalize(string.Join('/', parts));
    }
    private async Task<IReadOnlyList<SourceEntry>> PropFindAsync(string path, int depth, CancellationToken token, bool allowMissing=false)
    {
        path = RelativePaths.Normalize(path); var uri = Address(path, true);
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
        request.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
        request.Content = new StringContent("""
            <?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/></d:prop></d:propfind>
            """, Encoding.UTF8, "application/xml");
        using var response = await SendAsync(request, token).ConfigureAwait(false);
        if(allowMissing&&response.StatusCode==HttpStatusCode.NotFound)return [];
        Success(response);
        if ((int)response.StatusCode != 207) throw new IOException("PROPFIND 未返回 DAV Multi-Status。");
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = XmlReader.Create(body, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        XDocument document;
        try { document = await XDocument.LoadAsync(reader, LoadOptions.None, token).ConfigureAwait(false); }
        catch (XmlException) { throw new IOException("WebDAV XML 无效或包含被禁止的实体。"); }
        if (document.Root?.Name != Dav + "multistatus") throw new IOException("DAV 响应根无效。");
        var result = new List<SourceEntry>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in document.Root.Elements(Dav + "response"))
        {
            token.ThrowIfCancellationRequested();
            var relative = HrefPath(item.Element(Dav + "href")?.Value ?? throw new IOException("DAV 条目缺少 href。"), uri);
            if (relative != path && (depth == 0 || RelativePaths.Parent(relative) != path)) throw new IOException("DAV 枚举不是深度 1 的直接子项。");
            if (!names.Add(relative)) throw new IOException("DAV 枚举包含重复路径。");
            var props = new XElement(Dav + "prop");
            foreach (var propstat in item.Elements(Dav + "propstat"))
                if (StatusCode(propstat.Element(Dav + "status")?.Value) == 200)
                    props.Add(propstat.Element(Dav + "prop")?.Elements().Select(element => new XElement(element)));
            if (props.Element(Dav + "resourcetype") is not { } resource) throw new IOException("DAV 部分条目读取失败，拒绝不完整快照。");
            var directory = resource.Element(Dav + "collection") is not null;
            long size = 0;
            if (!directory && (!long.TryParse(props.Element(Dav + "getcontentlength")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0))
                throw new IOException("DAV 文件缺少有效长度。");
            var modified = DateTimeOffset.TryParse(props.Element(Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime() : DateTimeOffset.UnixEpoch;
            var tag = NormalizeTag(props.Element(Dav + "getetag")?.Value);
            result.Add(new("", relative, directory, size, modified, JsonSerializer.Serialize(new VersionInfo(tag, size, modified.UtcTicks))));
        }
        if (!names.Contains(path)) throw new IOException("DAV 响应缺少请求目录自身，拒绝不完整快照。");
        return result;
    }
    private static int StatusCode(string? value) => value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts && int.TryParse(parts[1], out var code) ? code : 0;
    public async IAsyncEnumerable<SourceEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken token = default)
    {
        var entries = await PropFindAsync(path, 1, token).ConfigureAwait(false);
        foreach (var entry in entries) if (entry.Path != path&&(!_temporary.TryGetValue(entry.Path,out var temporary)||temporary.Stage=="Foreign")) yield return entry;
    }
    public async Task<SourceEntry> GetMetadataAsync(string path, CancellationToken token = default)
        => (await PropFindAsync(path, 0, token).ConfigureAwait(false)).Single(entry => entry.Path == path);
    /// <summary>存在性判断不以 FileNotFoundException 作为正常分支；不掩盖不完整 Multi-Status。</summary>
    public async Task<SourceEntry?> TryGetMetadataAsync(string path,CancellationToken token=default)
        => (await PropFindAsync(path,0,token,true).ConfigureAwait(false)).SingleOrDefault(entry=>entry.Path==path);
    private static VersionInfo Version(SourceEntry entry) => JsonSerializer.Deserialize<VersionInfo>(entry.Version) ?? throw new IOException("DAV 版本标记无效。");
    /// <summary>空值保留为缺失；裸值不加引号、不获得强验证能力。</summary>
    private static string? NormalizeTag(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return EntityTagHeaderValue.TryParse(value, out var parsed) && parsed.Tag != "*" ? parsed.ToString() : value;
    }
    private static bool ValidTag(string? value) => EntityTagHeaderValue.TryParse(NormalizeTag(value), out var parsed) && parsed.Tag != "*";
    private static bool Strong(string? tag) => ValidTag(tag) && EntityTagHeaderValue.TryParse(NormalizeTag(tag), out var parsed) && !parsed.IsWeak;
    /// <summary>兼容旧令牌的外围空白；保留强弱、标签值、大小和时间差异。</summary>
    public bool IsSameVersion(string left, string right)
    {
        if (left == right) return true;
        try
        {
            var a = JsonSerializer.Deserialize<VersionInfo>(left);
            var b = JsonSerializer.Deserialize<VersionInfo>(right);
            return a is not null && b is not null && a.Size == b.Size && a.ModifiedTicks == b.ModifiedTicks &&
                NormalizeTag(a.ETag) == NormalizeTag(b.ETag);
        }
        catch (JsonException) { return false; }
    }
    private static void MatchResponse(SourceEntry entry, HttpResponseMessage response)
    {
        var version = Version(entry);
        var expected = NormalizeTag(version.ETag);
        var actual = NormalizeTag(response.Headers.TryGetValues("ETag", out var values) ? values.SingleOrDefault() : null);
        if (Strong(expected) && expected != actual)
            throw new SourceConflictException("读取响应 ETag 与枚举强版本不匹配。");
        if (!Strong(expected))
        {
            if (ValidTag(expected) && ValidTag(actual) && expected != actual)
                throw new SourceConflictException("读取响应 ETag 与枚举版本不匹配。");
            if (version.ModifiedTicks != DateTimeOffset.UnixEpoch.UtcTicks &&
                response.Content.Headers.LastModified is { } modified && modified.UtcTicks != version.ModifiedTicks)
                throw new SourceConflictException("读取响应时间与枚举快照不匹配。");
        }
        if (response.Content.Headers.ContentEncoding.Count != 0) throw new IOException("读取响应采用内容编码，拒绝混淆范围。");
    }
    /// <summary>未知 Range 能力时共享首次探测和快照，所有等待者在发请求前加入；首位取消不影响其他等待者。</summary>
    public async ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if(entry.IsDirectory||offset>entry.Size)throw new ArgumentException("文件读取范围无效。");
        var count=(int)Math.Min(buffer.Length,entry.Size-offset);if(count==0)return 0;
        if(!Strong(Version(entry).ETag)||Capabilities.RangeRead==CapabilitySupport.Unsupported)
            return await CachedReadAsync(entry,offset,buffer[..count],null,token).ConfigureAwait(false);
        if(Capabilities.RangeRead==CapabilitySupport.Unknown)
        {
            var first=await _initialReads.RunAsync((entry.Path,entry.Version),async cancel=>
            {
                // 共享工作拥有缓冲区，首位调用者取消后不会继续访问其内存。
                var bytes=new byte[count];await ReadRemoteRangeAsync(entry,offset,bytes,cancel).ConfigureAwait(false);return new InitialRange(offset,bytes);
            },token).ConfigureAwait(false);
            if(first.Offset==offset&&first.Bytes.Length==count){first.Bytes.CopyTo(buffer);return count;}
            if(Capabilities.RangeRead==CapabilitySupport.Unsupported)
                return await CachedReadAsync(entry,offset,buffer[..count],null,token).ConfigureAwait(false);
        }
        return await ReadRemoteRangeAsync(entry,offset,buffer[..count],token).ConfigureAwait(false);
    }
    private async ValueTask<int> ReadRemoteRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token)
    {
        var count=buffer.Length;var version=Version(entry);HttpResponseMessage? response=null;
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,Address(entry.Path));
            request.Headers.Range=new RangeHeaderValue(offset,offset+count-1);request.Headers.IfMatch.ParseAdd(NormalizeTag(version.ETag)!);
            response=await SendAsync(request,token).ConfigureAwait(false);Success(response);MatchResponse(entry,response);
            if(response.StatusCode==HttpStatusCode.OK)
            {
                var adopted=response;response=null;
                var read=await CachedReadAsync(entry,offset,buffer,adopted,token).ConfigureAwait(false);
                Capabilities=Capabilities with{RangeRead=CapabilitySupport.Unsupported};return read;
            }
            var range=response.Content.Headers.ContentRange;
            if(response.StatusCode!=HttpStatusCode.PartialContent||range?.Unit!="bytes"||range.From!=offset||range.To!=offset+count-1||range.Length!=entry.Size||response.Content.Headers.ContentLength!=count)
                throw new IOException("WebDAV Content-Range 或长度无效。");
            await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var received=0;var completed=false;
            try
            {
                while(received<count){var read=await stream.ReadAsync(buffer[received..count],token).ConfigureAwait(false);if(read==0)throw new EndOfStreamException();received+=read;}
                var extra=await stream.ReadAsync(new byte[1],token).ConfigureAwait(false);received+=extra;if(extra!=0)throw new IOException("范围响应包含多余字节。");
                Capabilities=Capabilities with{RangeRead=CapabilitySupport.Supported};completed=true;return count;
            }
            finally{ReadDiagnostic?.Invoke(new("range",received,null,completed));}
        }
        finally{response?.Dispose();}
    }

    private sealed class ResponseLease(HttpResponseMessage? response) : IDisposable
    {
        private HttpResponseMessage? _response=response;
        public HttpResponseMessage? Take()=>Interlocked.Exchange(ref _response,null);
        public void Dispose()=>Take()?.Dispose();
    }
    private ValueTask<int> CachedReadAsync(SourceEntry entry,long offset,Memory<byte> buffer,HttpResponseMessage? existing,CancellationToken token)
    {
        var lease=new ResponseLease(existing);
        return _cache.ReadAsync(_endpoint.AbsoluteUri, entry, offset, buffer, async (output, cancel) =>
        {
            HttpResponseMessage? owned = lease.Take();
            var tag=NormalizeTag(Version(entry).ETag);
            var reason=existing is not null?"server_ignored_range":Strong(tag)?"range_previously_ignored":tag is null?"missing_etag":ValidTag(tag)?"weak_etag":"nonstandard_etag";
            long received=0;var completed=false;ReadDiagnostic?.Invoke(new("whole_file",0,reason,false));
            try
            {
                var response = owned;
                if (response is null)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, Address(entry.Path));
                    var version = Version(entry); if (Strong(version.ETag)) request.Headers.IfMatch.ParseAdd(NormalizeTag(version.ETag)!);
                    response = owned = await SendAsync(request, cancel).ConfigureAwait(false);
                }
                Success(response); MatchResponse(entry, response);
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength != entry.Size) throw new IOException("整文件响应状态或长度不一致。");
                await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                var bytes = new byte[65536]; long remaining = entry.Size;
                using var baselineHash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, remaining)), cancel).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException();
                    received+=read;baselineHash.AppendData(bytes,0,read);await output.WriteAsync(bytes.AsMemory(0, read), cancel).ConfigureAwait(false); remaining -= read;
                }
                if (await stream.ReadAsync(new byte[1], cancel).ConfigureAwait(false) != 0) throw new IOException("整文件响应过长。");
                if (!Strong(Version(entry).ETag))
                {
                    var after = await GetMetadataAsync(entry.Path, cancel).ConfigureAwait(false);
                    if (!IsSameVersion(entry.Version, after.Version))
                        throw new SourceConflictException("整文件快照下载期间远端元数据变化，请刷新后重试。");
                }
                try{ContentBaseline?.Invoke(new(entry.Path,entry.Version,Convert.ToHexString(baselineHash.GetHashAndReset())));}catch{ /* 基线通知不改变读取结果。 */ }
                Capabilities = Capabilities with { RangeRead = CapabilitySupport.Unsupported };
                completed=true;
            }
            finally { owned?.Dispose();ReadDiagnostic?.Invoke(new("whole_file",received,reason,completed)); }
        }, token, lease);
    }
    private static void Condition(HttpRequestMessage request, string? expectedVersion)
    {
        if (expectedVersion is null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else
        {
            var version = JsonSerializer.Deserialize<VersionInfo>(expectedVersion);
            if (!Strong(version?.ETag)) throw new NotSupportedException("此条目无强 ETag，拒绝条件修改。");
            request.Headers.IfMatch.ParseAdd(NormalizeTag(version!.ETag)!);
        }
    }
    public async Task<SourceEntry> CreateDirectoryAsync(string path, CancellationToken token = default)
    {
        path = NonRoot(path);
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), Address(path, true)); Condition(request, null);
        using var response = await SendAsync(request, token, true).ConfigureAwait(false);
        if(response.StatusCode==HttpStatusCode.BadRequest)throw new SourceRequestRejectedException(400,"WebDAV 拒绝创建目录（HTTP 400）；请确认目标父目录允许写入，部分服务的账户根不能新建普通目录。");
        Success(response);
        try { return await GetMetadataAsync(path, token).ConfigureAwait(false); }
        catch(Exception error)when(error is IOException or OperationCanceledException){throw new SourceOutcomeUnknownException("写入后核对",new IOException("远端成功响应后未能读取元数据。"));}
    }
    public async Task<SourceEntry> MoveAsync(string path, string destination, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); destination = NonRoot(destination);
        if (RelativePaths.Contains(path, destination)) throw new ArgumentException("不能移入自身。");
        await VerifyMutationAsync("MOVE",token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), Address(path)); Condition(request, expectedVersion);
        request.Headers.Add("Destination", Address(destination).AbsoluteUri); request.Headers.Add("Overwrite", "F");
        using var response = await SendAsync(request, token, true).ConfigureAwait(false); Success(response);
        try { return await GetMetadataAsync(destination, token).ConfigureAwait(false); }
        catch(Exception error)when(error is IOException or OperationCanceledException){throw new SourceOutcomeUnknownException("移动后核对",new IOException("远端成功响应后未能读取元数据。"));}
    }
    public async Task DeleteAsync(string path, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyMutationAsync("DELETE",token).ConfigureAwait(false);
        var metadata = await GetMetadataAsync(path, token).ConfigureAwait(false);
        if (metadata.IsDirectory)
            await foreach (var _ in ListAsync(path, token).ConfigureAwait(false)) throw new NotSupportedException("首版仅允许删除空远端目录，避免递归条件覆盖不完整。");
        using var request = new HttpRequestMessage(HttpMethod.Delete, Address(path, metadata.IsDirectory)); Condition(request, expectedVersion);
        using var response = await SendAsync(request, token, true).ConfigureAwait(false); Success(response);
    }
    private static string NonRoot(string path) => RelativePaths.Normalize(path) is { Length: > 0 } normalized ? normalized : throw new ArgumentException("不能修改源根。");
    public async ValueTask DisposeAsync() { await _initialReads.DisposeAsync().ConfigureAwait(false);await _cache.DisposeAsync().ConfigureAwait(false);_http.Dispose();_writeProbe.Dispose(); }
}

/// <summary>请求只借用调用者的流；HttpContent 释放不能关闭调用者数据源。</summary>
internal sealed class BorrowedStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
