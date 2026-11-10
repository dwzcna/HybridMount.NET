using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using HybridMount.Commons;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>协议内按职责拆分的 Transport 实现；公开入口与版本字段保持兼容。</summary>
public sealed partial class WebDavFileSource
{
    private static readonly XNamespace Dav = "DAV:";
    public WebDavFileSource(FileSourceConfiguration options, SourceCredentials credentials, WholeFileCache cache, HttpMessageHandler? handler = null)
    {
        _endpoint = FileSourceFactory.Endpoint(options); _cache = cache;
        _temporaryScope=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(_endpoint.AbsoluteUri+"\0"+credentials.UserName)));
        if (credentials.UserName.Contains(':')) throw new ArgumentException("WebDAV 用户名不能包含冒号。");
        var certificatePin=NormalizeCertificatePin(options);
        _http = new HttpClient(new DavTransport(this, credentials, handler ?? CreateHttpHandler(certificatePin)))
          { Timeout=Timeout.InfiniteTimeSpan };
        _dav = new WebDAVClient.DavClient(_endpoint, _http, new() { MetadataTimeout=TimeSpan.FromSeconds(120) });
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, bool mutation = false,bool strictRead=false)
    {
        token.ThrowIfCancellationRequested();
        if(mutation)await PrepareTemporaryRequestAsync(request,token).ConfigureAwait(false);
        var previousStrict=_strictRead.Value;_strictRead.Value=previousStrict||strictRead;
        try
        {
            var response=await ExecuteDavAsync(request,token).ConfigureAwait(false);
            if(mutation)
                try{await ObserveTemporaryResponseAsync(request,response,token).ConfigureAwait(false);}
                catch{response.Dispose();throw;}
            return response;
        }
        catch(SourceConnectionException error)
        {
            if(mutation)throw new SourceOutcomeUnknownException(request.Method.Method,error);
            throw;
        }
        catch (WebDAVClient.Helpers.WebDAVException)
        {
            if(mutation)throw new SourceOutcomeUnknownException(request.Method.Method,new IOException("DAV 响应无法确认写入结果。"));
            throw new IOException("WebDAV 响应格式、范围或 XML 无效。");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            if (mutation) throw new SourceOutcomeUnknownException(request.Method.Method, new IOException("网络请求未确认完成。"));
            if(error is OperationCanceledException&&token.IsCancellationRequested)throw;
            throw WebDavNetworkDiagnostics.Failure(error,request.Method.Method+" 响应",timeout:error is OperationCanceledException);
        }
        finally{_strictRead.Value=previousStrict;}
    }
    private static void Success(HttpResponseMessage response)
    {
        var retry=response.Headers.RetryAfter?.Delta;
        if(retry is null&&response.Headers.RetryAfter?.Date is {} date)retry=date>DateTimeOffset.UtcNow?date-DateTimeOffset.UtcNow:TimeSpan.Zero;
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            throw new SourceConflictException("远端版本或目标创建条件不满足。");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new FileNotFoundException("远端条目不存在。");
        if ((int)response.StatusCode is >=400 and <500)throw new SourceRequestRejectedException((int)response.StatusCode,response.StatusCode switch
        {
            HttpStatusCode.Unauthorized=>"WebDAV 认证失败（HTTP 401），请检查账号与应用密码。",
            HttpStatusCode.Forbidden=>"WebDAV 拒绝访问此路径或操作（HTTP 403），请检查权限及服务器访问规则。",
            _=>$"WebDAV 拒绝请求（HTTP {(int)response.StatusCode}）。"
        },retry);
        if((int)response.StatusCode>=500)throw new RemoteSyncException((int)response.StatusCode,retryAfter:retry);
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
        // 文件元数据使用原始资源 URI；尾斜杠只用于深度 1 的集合枚举。
        path = RelativePaths.Normalize(path); var uri = Address(path, depth != 0);
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
            result.Add(ObserveMetadata(new("", relative, directory, size, modified, JsonSerializer.Serialize(new VersionInfo(tag, size, modified.UtcTicks)))));
        }
        if (!names.Contains(path)) throw new IOException("DAV 响应缺少请求目录自身，拒绝不完整快照。");
        return result;
    }
    private static int StatusCode(string? value) => value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts && int.TryParse(parts[1], out var code) ? code : 0;
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

}
