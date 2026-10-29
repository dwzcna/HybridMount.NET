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
public sealed class WebDavFileSource : IFileSource
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly WholeFileCache _cache;
    private readonly SemaphoreSlim _writeProbe = new(1, 1);
    private bool _writesVerified;
    private sealed record VersionInfo(string? ETag, long Size, long ModifiedTicks);
    public FileSourceCapabilities Capabilities { get; private set; } = new(CapabilitySupport.Unknown,
        CapabilitySupport.Unknown, CapabilitySupport.Unknown, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported, SourceCaseSensitivity.Unknown);
    public WebDavFileSource(FileSourceConfiguration options, SourceCredentials credentials, WholeFileCache cache, HttpMessageHandler? handler = null)
    {
        _endpoint = FileSourceFactory.Endpoint(options); _cache = cache;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4,
            AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(20) }) { Timeout = TimeSpan.FromMinutes(2) };
        if (credentials.UserName.Contains(':')) throw new ArgumentException("WebDAV 用户名不能包含冒号。");
        if (credentials.UserName.Length != 0)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password)));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HybridMount/0.0.3");
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
        try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            if (mutation) throw new SourceOutcomeUnknownException(request.Method.Method, new IOException("网络请求未确认完成。"));
            if (error is OperationCanceledException) throw;
            throw new IOException("WebDAV 连接失败，请检查网络、地址及证书。");
        }
    }
    private static void Success(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            throw new SourceConflictException("远端版本或目标创建条件不满足。");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("远端条目不存在。");
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
    private async Task<IReadOnlyList<SourceEntry>> PropFindAsync(string path, int depth, CancellationToken token)
    {
        path = RelativePaths.Normalize(path); var uri = Address(path, true);
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
        request.Headers.Add("Depth", depth.ToString(CultureInfo.InvariantCulture));
        request.Content = new StringContent("""
            <?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/></d:prop></d:propfind>
            """, Encoding.UTF8, "application/xml");
        using var response = await SendAsync(request, token).ConfigureAwait(false); Success(response);
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
            var tag = props.Element(Dav + "getetag")?.Value;
            if (tag is not null && !EntityTagHeaderValue.TryParse(tag, out _)) throw new IOException("DAV ETag 格式无效。");
            result.Add(new("", relative, directory, size, modified, JsonSerializer.Serialize(new VersionInfo(tag, size, modified.UtcTicks))));
        }
        if (!names.Contains(path)) throw new IOException("DAV 响应缺少请求目录自身，拒绝不完整快照。");
        return result;
    }
    private static int StatusCode(string? value) => value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts && int.TryParse(parts[1], out var code) ? code : 0;
    public async IAsyncEnumerable<SourceEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken token = default)
    {
        var entries = await PropFindAsync(path, 1, token).ConfigureAwait(false);
        foreach (var entry in entries) if (entry.Path != path) yield return entry;
    }
    public async Task<SourceEntry> GetMetadataAsync(string path, CancellationToken token = default)
        => (await PropFindAsync(path, 0, token).ConfigureAwait(false)).Single(entry => entry.Path == path);
    private static VersionInfo Version(SourceEntry entry) => JsonSerializer.Deserialize<VersionInfo>(entry.Version) ?? throw new IOException("DAV 版本标记无效。");
    private static bool Strong(string? tag) => tag is not null && EntityTagHeaderValue.TryParse(tag, out var parsed) && !parsed.IsWeak;
    private static void MatchResponse(SourceEntry entry, HttpResponseMessage response)
    {
        var version = Version(entry);
        if (version.ETag is not null && response.Headers.ETag?.ToString() != version.ETag) throw new SourceConflictException("读取响应 ETag 与枚举版本不匹配。");
        if (version.ETag is null && response.Content.Headers.LastModified?.UtcTicks != version.ModifiedTicks)
            throw new SourceConflictException("读取响应时间与枚举快照不匹配，无法验证内容。");
        if (response.Content.Headers.ContentEncoding.Count != 0) throw new IOException("读取响应采用内容编码，拒绝混淆范围。");
    }
    public async ValueTask<int> ReadRangeAsync(SourceEntry entry, long offset, Memory<byte> buffer, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (entry.IsDirectory || offset > entry.Size) throw new ArgumentException("文件读取范围无效。");
        var count = (int)Math.Min(buffer.Length, entry.Size - offset); if (count == 0) return 0;
        var version = Version(entry);
        if (!Strong(version.ETag) || Capabilities.RangeRead == CapabilitySupport.Unsupported)
            return await CachedReadAsync(entry, offset, buffer[..count], null, token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, Address(entry.Path));
        request.Headers.Range = new RangeHeaderValue(offset, offset + count - 1);
        request.Headers.IfMatch.ParseAdd(version.ETag!);
        using var response = await SendAsync(request, token).ConfigureAwait(false); Success(response); MatchResponse(entry, response);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            Capabilities = Capabilities with { RangeRead = CapabilitySupport.Unsupported };
            return await CachedReadAsync(entry, offset, buffer[..count], response, token).ConfigureAwait(false);
        }
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != offset || range.To != offset + count - 1 || range.Length != entry.Size || response.Content.Headers.ContentLength != count)
            throw new IOException("WebDAV Content-Range 或长度无效。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await stream.ReadExactlyAsync(buffer[..count], token).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new IOException("范围响应包含多余字节。");
        Capabilities = Capabilities with { RangeRead = CapabilitySupport.Supported }; return count;
    }
    private ValueTask<int> CachedReadAsync(SourceEntry entry, long offset, Memory<byte> buffer, HttpResponseMessage? existing, CancellationToken token)
        => _cache.ReadAsync(_endpoint.AbsoluteUri, entry, offset, buffer, async (output, cancel) =>
        {
            HttpResponseMessage? owned = null;
            try
            {
                var response = existing;
                if (response is null)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, Address(entry.Path));
                    var version = Version(entry); if (Strong(version.ETag)) request.Headers.IfMatch.ParseAdd(version.ETag!);
                    response = owned = await SendAsync(request, cancel).ConfigureAwait(false);
                }
                Success(response); MatchResponse(entry, response);
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength != entry.Size) throw new IOException("整文件响应状态或长度不一致。");
                await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                var bytes = new byte[65536]; long remaining = entry.Size;
                while (remaining > 0)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(0, (int)Math.Min(bytes.Length, remaining)), cancel).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException();
                    await output.WriteAsync(bytes.AsMemory(0, read), cancel).ConfigureAwait(false); remaining -= read;
                }
                if (await stream.ReadAsync(new byte[1], cancel).ConfigureAwait(false) != 0) throw new IOException("整文件响应过长。");
                Capabilities = Capabilities with { RangeRead = CapabilitySupport.Unsupported };
            }
            finally { owned?.Dispose(); }
        }, token);
    private static void Condition(HttpRequestMessage request, string? expectedVersion)
    {
        if (expectedVersion is null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else
        {
            var version = JsonSerializer.Deserialize<VersionInfo>(expectedVersion);
            if (!Strong(version?.ETag)) throw new NotSupportedException("此条目无强 ETag，拒绝条件修改。");
            request.Headers.IfMatch.ParseAdd(version!.ETag!);
        }
    }
    /// <summary>首次写入前用独立随机文件验证创建/更新冲突；连接探测本身只读。</summary>
    private async Task VerifyWritesAsync(CancellationToken token)
    {
        if (_writesVerified) return; await _writeProbe.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_writesVerified) return;
            if (Capabilities.ConditionalWrite == CapabilitySupport.Unsupported) throw new NotSupportedException("服务器未通过条件写验证。");
            var path = ".hybridmount-probe-" + Guid.NewGuid().ToString("N"); var created = false;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Put, Address(path)) { Content = new ByteArrayContent([17, 31]) })
                {
                    request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                    using var response = await SendAsync(request, token, true).ConfigureAwait(false); Success(response); created = true;
                }
                foreach (var createOnly in new[] { true, false })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Put, Address(path)) { Content = new ByteArrayContent([99]) };
                    if (createOnly) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                    else request.Headers.IfMatch.ParseAdd("\"hybridmount-never-match-" + Guid.NewGuid().ToString("N") + "\"");
                    using var response = await SendAsync(request, token, true).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.PreconditionFailed)
                    { Capabilities = Capabilities with { ConditionalWrite = CapabilitySupport.Unsupported }; throw new NotSupportedException("服务器忽略条件写，已拒绝修改用户内容。"); }
                }
                _writesVerified = true; Capabilities = Capabilities with { ConditionalWrite = CapabilitySupport.Supported };
            }
            finally
            {
                if (created)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    using var request = new HttpRequestMessage(HttpMethod.Delete, Address(path));
                    using var response = await SendAsync(request, cleanup.Token, true).ConfigureAwait(false);
                    Success(response);
                }
            }
        }
        finally { _writeProbe.Release(); }
    }
    public async Task<SourceEntry> UploadAsync(string path, Stream content, string? expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyWritesAsync(token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Put, Address(path)) { Content = new StreamContent(new BorrowedStream(content)) };
        Condition(request, expectedVersion);
        using var response = await SendAsync(request, token, true).ConfigureAwait(false); Success(response);
        try { return await GetMetadataAsync(path, token).ConfigureAwait(false); }
        catch(Exception error)when(error is IOException or OperationCanceledException){throw new SourceOutcomeUnknownException("写入后核对",new IOException("远端成功响应后未能读取元数据。"));}
    }
    public async Task<SourceEntry> CreateDirectoryAsync(string path, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyWritesAsync(token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), Address(path, true)); Condition(request, null);
        using var response = await SendAsync(request, token, true).ConfigureAwait(false); Success(response);
        try { return await GetMetadataAsync(path, token).ConfigureAwait(false); }
        catch(Exception error)when(error is IOException or OperationCanceledException){throw new SourceOutcomeUnknownException("写入后核对",new IOException("远端成功响应后未能读取元数据。"));}
    }
    public async Task<SourceEntry> MoveAsync(string path, string destination, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); destination = NonRoot(destination);
        if (RelativePaths.Contains(path, destination)) throw new ArgumentException("不能移入自身。");
        await VerifyWritesAsync(token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(new HttpMethod("MOVE"), Address(path)); Condition(request, expectedVersion);
        request.Headers.Add("Destination", Address(destination).AbsoluteUri); request.Headers.Add("Overwrite", "F");
        using var response = await SendAsync(request, token, true).ConfigureAwait(false); Success(response);
        try { return await GetMetadataAsync(destination, token).ConfigureAwait(false); }
        catch(Exception error)when(error is IOException or OperationCanceledException){throw new SourceOutcomeUnknownException("移动后核对",new IOException("远端成功响应后未能读取元数据。"));}
    }
    public async Task DeleteAsync(string path, string expectedVersion, CancellationToken token = default)
    {
        path = NonRoot(path); await VerifyWritesAsync(token).ConfigureAwait(false);
        var metadata = await GetMetadataAsync(path, token).ConfigureAwait(false);
        if (metadata.IsDirectory)
            await foreach (var _ in ListAsync(path, token).ConfigureAwait(false)) throw new NotSupportedException("首版仅允许删除空远端目录，避免递归条件覆盖不完整。");
        using var request = new HttpRequestMessage(HttpMethod.Delete, Address(path, metadata.IsDirectory)); Condition(request, expectedVersion);
        using var response = await SendAsync(request, token, true).ConfigureAwait(false); Success(response);
    }
    private static string NonRoot(string path) => RelativePaths.Normalize(path) is { Length: > 0 } normalized ? normalized : throw new ArgumentException("不能修改源根。");
    public ValueTask DisposeAsync() { _http.Dispose(); _writeProbe.Dispose(); return ValueTask.CompletedTask; }
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
