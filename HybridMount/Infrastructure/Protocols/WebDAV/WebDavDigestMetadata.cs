using HybridMount.Models.Mounting;
using System.Net;
using System.Text.Json;

namespace HybridMount.Infrastructure.Protocols.WebDAV;

public sealed partial class WebDavFileSource
{
    /// <summary>弱版本完整扫描逐文件HEAD；增量仅处理已变化条目。元数据冲突最多重新读取一次，不推进不一致快照。</summary>
    private async Task<IReadOnlyList<SourceEntry>> PropFindAsync(string path,int depth,CancellationToken token,bool allowMissing=false)
    {
        var entries=await PropFindRawAsync(path,depth,token,allowMissing).ConfigureAwait(false);var result=new List<SourceEntry>(entries.Count);
        foreach(var entry in entries)result.Add(await AttachDigestAsync(entry,token).ConfigureAwait(false));return result;
    }
    private async Task<SourceEntry> AttachDigestAsync(SourceEntry entry,CancellationToken token)
    {
        if(entry.IsDirectory||Strong(Version(entry).ETag))return entry;
        for(var attempt=0;attempt<2;attempt++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Head,Address(entry.Path));
            using var response=await SendAsync(request,token).ConfigureAwait(false);
            if(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)return entry;
            Success(response);if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("WebDAV HEAD未返回完整资源元数据。");
            if(response.Content.Headers.ContentEncoding.Any(value=>!value.Equals("identity",StringComparison.OrdinalIgnoreCase)))throw new IOException("WebDAV HEAD不是identity表示。");
            _protocolEvidence.Record("dav.head",ProtocolCapabilityStatus.Supported,entry.Path);
            var digest=WebDavReprDigest.Parse(response.Headers.TryGetValues("Repr-Digest",out var fields)?fields:[],response.RequestMessage?.RequestUri??request.RequestUri!);
            if(digest is null)return entry;
            var version=Version(entry);var tag=NormalizeTag(response.Headers.ETag?.ToString());
            var consistent=response.Content.Headers.ContentLength==entry.Size&&
                (version.ETag is null||tag is null||NormalizeTag(version.ETag)==tag)&&
                (version.ModifiedTicks==DateTimeOffset.UnixEpoch.UtcTicks||response.Content.Headers.LastModified is not {} modified||version.ModifiedTicks==modified.UtcTicks);
            if(consistent)
            {
                _protocolEvidence.Record("dav.digest",ProtocolCapabilityStatus.Supported,entry.Path);
                return entry with{Version=JsonSerializer.Serialize(version with{Representation=digest})};
            }
            if(attempt==0){entry=(await PropFindRawAsync(entry.Path,0,token).ConfigureAwait(false)).Single();continue;}
            throw new SourceConflictException("WebDAV HEAD与目录枚举持续不一致，未推进远端检查点。");
        }
        throw new InvalidOperationException();
    }
}
