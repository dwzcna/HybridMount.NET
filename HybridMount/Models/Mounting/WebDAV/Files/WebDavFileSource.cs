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

/// <summary>基于 WebDAVClient DavClient 的 WebDAV 驱动；应用负责版本、缓存、写入条件和生命周期。</summary>
public sealed partial class WebDavFileSource : IFileSource, IReadDiagnosticsSource, IUploadDiagnosticsSource, IRemoteChangeSource, IRemoteUsageSource
{
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly WebDAVClient.DavClient _dav;
    private readonly WholeFileCache _cache;
    private readonly SemaphoreSlim _writeProbe = new(1, 1);
    private sealed record InitialRange(long Offset,byte[] Bytes);
    private readonly SharedAsyncWork<(string Path,string Version),InitialRange> _initialReads=new();
    private bool _writesVerified;
    public event Action<SourceReadDiagnostic>? ReadDiagnostic;
    public FileSourceCapabilities Capabilities { get; private set; } = new(CapabilitySupport.Unknown,
        CapabilitySupport.Unknown, CapabilitySupport.Unknown, CapabilitySupport.Unsupported, CapabilitySupport.Unsupported, SourceCaseSensitivity.Unknown);
    private Uri Address(string path, bool directory = false)
    {
        path = RelativePaths.Normalize(path);
        var suffix = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_endpoint.AbsoluteUri + suffix + (directory && suffix.Length != 0 ? "/" : ""));
    }
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
            try { response=await SendAsync(request,token).ConfigureAwait(false); }
            catch(DavMalformedRangeException){throw new IOException("DAV 206 范围响应无效，未转为整文件读取。");}
            catch (DavRangeIgnoredException)
            {
                Capabilities=Capabilities with{RangeRead=CapabilitySupport.Unsupported};
                return await CachedReadAsync(entry,offset,buffer,null,token).ConfigureAwait(false);
            }
            Success(response);MatchResponse(entry,response);
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
    public async ValueTask DisposeAsync() { await _initialReads.DisposeAsync().ConfigureAwait(false);await _cache.DisposeAsync().ConfigureAwait(false);_dav.Dispose();_http.Dispose();_writeProbe.Dispose(); }
}
