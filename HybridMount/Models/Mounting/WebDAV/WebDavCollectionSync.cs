using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using WebDAVClient;
using WebDAVClient.Extensions;
using WebDAVClient.Helpers;
using WebDAVClient.Protocol;
namespace HybridMount.Models.Mounting.WebDAV;

public sealed partial class WebDavFileSource
{
    private readonly ConcurrentDictionary<string,long> _observations=new(StringComparer.Ordinal);
    private SourceEntry ObserveMetadata(SourceEntry entry)=>_observations.TryGetValue(entry.Path,out var value)?WithObservation(entry,value):entry;
    /// <summary>观测代次只区分明确变化的缓存，不改变原 ETag 或版本校验条件。</summary>
    public SourceEntry WithObservation(SourceEntry entry,long observation)
    {
        _observations[entry.Path]=observation;
        return entry with{Version=JsonSerializer.Serialize(Version(entry) with{Observation=observation})};
    }
    public void RestoreObservations(IEnumerable<RemoteShadowEntry> entries)
    {foreach(var entry in entries)_observations[entry.Entry.Path]=entry.Observation;}
    public bool IsSameVersionHint(string expected,string observed)
    {
        try
        {
            var left=JsonSerializer.Deserialize<VersionInfo>(expected);var right=JsonSerializer.Deserialize<VersionInfo>(observed);
            return left is not null&&right is not null&&left.Size==right.Size&&left.ModifiedTicks==right.ModifiedTicks&&NormalizeTag(left.ETag)==NormalizeTag(right.ETag);
        }
        catch(JsonException){return false;}
    }
    public bool HasReliableVersion(SourceEntry entry)=>Strong(Version(entry).ETag);
    private static XElement? Property(DavResourceResponse resource,string name,bool report=false)
    {
        var values=resource.GetProperties(new("DAV:",name)).ToArray();
        if(values.Length>1)throw report?new InvalidRemoteChangeResponseException("DAV 同步属性重复，拒绝歧义快照。"):new IOException("DAV 属性重复，拒绝歧义快照。");
        return values.Length==1&&values[0].StatusCode==200?values[0].Value.ToElement():null;
    }
    private static void CheckSyncResponse(DavResponse response)
    {
        if(response.StatusCode==207)return;
        var unsupported=response.Errors.Any(x=>x.ToElement().DescendantsAndSelf(Dav+"supported-report").Any());
        var invalid=response.Errors.Any(x=>x.ToElement().DescendantsAndSelf(Dav+"valid-sync-token").Any());
        TimeSpan? retry=null;
        if(int.TryParse(response.GetHeader("Retry-After"),out var seconds)&&seconds>=0)retry=TimeSpan.FromSeconds(seconds);
        else if(DateTimeOffset.TryParse(response.GetHeader("Retry-After"),out var date))retry=date>DateTimeOffset.UtcNow?date-DateTimeOffset.UtcNow:TimeSpan.Zero;
        throw new RemoteSyncException(response.StatusCode,invalid,retry,unsupported);
    }
    /// <summary>发现每个集合的属性；广告只作为候选，实际支持仍须有效 REPORT 确认。</summary>
    public async Task<bool> SupportsCollectionSyncAsync(string collection,CancellationToken token)
    {
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(MetadataDeadline);
        try
        {
            var result=await _dav.PropFindAsync(LibraryPath(Address(collection,true)),new PropFindOptions{Depth=DavDepth.Zero,Selection=DavPropertySelection.Selected,
                Properties=[new("DAV:","supported-report-set"),new("DAV:","sync-token")]},limit.Token).ConfigureAwait(false);
            CheckSyncResponse(result);
            if(result.Resources.Count!=1||HrefPath(result.Resources[0].Hrefs.Single(),Address(collection,true))!=collection)throw new IOException("DAV 集合发现响应不完整。");
            var resource=result.Resources[0];
            if(resource.StatusCode is not null and not 200)throw new RemoteSyncException(resource.StatusCode.Value);
            var reports=Property(resource,"supported-report-set");var value=Property(resource,"sync-token")?.Value;
            var supported = reports?.Descendants(Dav+"sync-collection").Any()==true&&!string.IsNullOrWhiteSpace(value);
            if (supported) _protocolEvidence.Record("dav.report", ProtocolCapabilityStatus.Advertised, collection, ProtocolCapabilityReason.Declared);
            return supported;
        }
        catch(WebDAVException){throw new IOException("DAV 集合发现响应格式无效。");}
        catch(OperationCanceledException error)when(!token.IsCancellationRequested){throw WebDavNetworkDiagnostics.Failure(error,"PROPFIND 集合发现",timeout:true);}
    }
    /// <summary>单批查询，校验路径及资源状态；绝不将属性 404 或普通 HTTP 404 解释为删除。</summary>
    public async Task<RemoteChangeBatch> GetCollectionChangesAsync(string collection,string? syncToken,CancellationToken token)
    {
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(MetadataDeadline);
        var observation=new WireObservation();var previous=_wire.Value;_wire.Value=observation;
        try
        {
            var result=await _dav.SyncCollectionAsync(LibraryPath(Address(collection,true)),syncToken,MetadataProperties,1000,new ReportOptions{Depth=DavDepth.Zero},limit.Token).ConfigureAwait(false);
            CheckSyncResponse(result.Response);
            var document=result.Response.Document?.ToElement();
            var tokens=document?.Elements(Dav+"sync-token").ToArray();
            if(document?.Name!=Dav+"multistatus"||tokens is not {Length:1}||tokens[0].HasElements||tokens[0].Value!=result.SyncToken)
                throw new InvalidRemoteChangeResponseException("DAV 同步响应根或令牌结构无效。");
            var entries=new List<SourceEntry>();var deleted=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var resource in result.Response.Resources)
            {
                if(resource.Hrefs.Count!=1||resource.Hrefs.Any(x=>x.StartsWith("//",StringComparison.Ordinal)))throw new InvalidRemoteChangeResponseException("DAV 同步 href 数量无效或包含双斜杠地址。");
                string path;
                try{path=HrefPath(resource.Hrefs.Single(),Address(collection,true));}
                catch(Exception error)when(error is IOException or ArgumentException or UriFormatException)
                {throw new InvalidRemoteChangeResponseException("DAV 同步 href 无效或越过配置根，未跟随该地址。",error);}
                if(!seen.Add(path)||path!=collection&&RelativePaths.Parent(path)!=collection)throw new InvalidRemoteChangeResponseException("DAV 同步响应包含越界或重复路径。");
                if(path!=collection&&IsInternalTemporary(path)&&resource.StatusCode!=404)continue;
                if(resource.StatusCode==507){if(path==collection)continue;throw new InvalidRemoteChangeResponseException("DAV 续批标记不属于请求集合。");}
                if(resource.StatusCode==404)
                {if(syncToken is null||path==collection)throw new InvalidRemoteChangeResponseException("DAV 初始基线或集合自身不能作为删除事件。");if(!IsInternalTemporary(path))deleted.Add(path);continue;}
                if(resource.StatusCode is not null and not 200)throw new RemoteSyncException(resource.StatusCode.Value);
                var type=Property(resource,"resourcetype",true)??throw new InvalidRemoteChangeResponseException("DAV 同步条目缺少类型，未推进检查点。");
                var directory=type.Element(Dav+"collection") is not null;long size=0;
                if(path==collection&&!directory)throw new InvalidRemoteChangeResponseException("DAV 同步集合自身的类型无效。");
                if(!directory&&(!long.TryParse(Property(resource,"getcontentlength",true)?.Value,NumberStyles.None,CultureInfo.InvariantCulture,out size)||size<0))throw new InvalidRemoteChangeResponseException("DAV 同步文件缺少长度。");
                var modified=DateTimeOffset.TryParse(Property(resource,"getlastmodified",true)?.Value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date)?date.ToUniversalTime():DateTimeOffset.UnixEpoch;
                var tag=NormalizeTag(Property(resource,"getetag",true)?.Value);
                if(path!=collection)
                    entries.Add(ObserveMetadata(new("",path,directory,size,modified,JsonSerializer.Serialize(new VersionInfo(tag,size,modified.UtcTicks)))));
            }
            if(result.HasMore&&result.SyncToken==syncToken)throw new InvalidRemoteChangeResponseException("DAV 续批令牌未前进，拒绝无限循环。");
            if(string.IsNullOrWhiteSpace(result.SyncToken)||result.SyncToken.Length>65536)throw new InvalidRemoteChangeResponseException("DAV 同步响应缺少有效令牌。");
            _protocolEvidence.Record("dav.report", ProtocolCapabilityStatus.Supported, collection);
            return new(entries,deleted,result.SyncToken,result.HasMore);
        }
        catch(System.Xml.XmlException error)when(observation.Status==207){throw new InvalidRemoteChangeResponseException("DAV 同步 REPORT XML 无效。",error);}
        catch(WebDAVException error)when(observation.Status==207&&error.InnerException is not (IOException or HttpRequestException or OperationCanceledException))
        {throw new InvalidRemoteChangeResponseException("DAV 同步 REPORT XML、令牌或续批结构无效，未推进检查点。",error);}
        catch(WebDAVException error)
        {
            if(observation.Status!=0&&observation.Status!=207)throw new RemoteSyncException(observation.Status);
            throw new IOException("DAV 同步 REPORT 未能完整读取。",error);
        }
        catch(OperationCanceledException error)when(!token.IsCancellationRequested){throw WebDavNetworkDiagnostics.Failure(error,"REPORT 远端检测",timeout:true);}
        finally{_wire.Value=previous;}
    }
}
