using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using WebDAVClient;
using WebDAVClient.Extensions;
using WebDAVClient.Helpers;
using WebDAVClient.Protocol;
namespace HybridMount.Services.Mounting;

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
    private static XElement? Property(DavResourceResponse resource,string name)
    {
        var values=resource.GetProperties(new("DAV:",name)).ToArray();
        if(values.Length>1)throw new IOException("DAV 属性重复，拒绝歧义快照。");
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
            return reports?.Descendants(Dav+"sync-collection").Any()==true&&!string.IsNullOrWhiteSpace(value);
        }
        catch(WebDAVException){throw new IOException("DAV 集合发现响应格式无效。");}
        catch(OperationCanceledException error)when(!token.IsCancellationRequested){throw WebDavNetworkDiagnostics.Failure(error,"PROPFIND 集合发现",timeout:true);}
    }
    /// <summary>单批查询，校验路径及资源状态；绝不将属性 404 或普通 HTTP 404 解释为删除。</summary>
    public async Task<RemoteChangeBatch> GetCollectionChangesAsync(string collection,string? syncToken,CancellationToken token)
    {
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(MetadataDeadline);
        try
        {
            var result=await _dav.SyncCollectionAsync(LibraryPath(Address(collection,true)),syncToken,MetadataProperties,1000,new ReportOptions{Depth=DavDepth.Zero},limit.Token).ConfigureAwait(false);
            CheckSyncResponse(result.Response);
            var entries=new List<SourceEntry>();var deleted=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var resource in result.Response.Resources)
            {
                var path=HrefPath(resource.Hrefs.Single(),Address(collection,true));
                if(!seen.Add(path)||path!=collection&&RelativePaths.Parent(path)!=collection)throw new IOException("DAV 同步响应包含越界或重复路径。");
                if(resource.StatusCode==507&&path==collection)continue;
                if(resource.StatusCode==404)
                {if(syncToken is null||path==collection)throw new IOException("DAV 初始基线或集合自身不能作为删除事件。");deleted.Add(path);continue;}
                if(resource.StatusCode is not null and not 200)throw new RemoteSyncException(resource.StatusCode.Value);
                var type=Property(resource,"resourcetype")??throw new IOException("DAV 同步条目缺少类型，未推进检查点。");
                var directory=type.Element(Dav+"collection") is not null;long size=0;
                if(!directory&&(!long.TryParse(Property(resource,"getcontentlength")?.Value,NumberStyles.None,CultureInfo.InvariantCulture,out size)||size<0))throw new IOException("DAV 同步文件缺少长度。");
                var modified=DateTimeOffset.TryParse(Property(resource,"getlastmodified")?.Value,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date)?date.ToUniversalTime():DateTimeOffset.UnixEpoch;
                var tag=NormalizeTag(Property(resource,"getetag")?.Value);
                if(path!=collection&&(!_temporary.TryGetValue(path,out var temporary)||temporary.Stage=="Foreign"))
                    entries.Add(ObserveMetadata(new("",path,directory,size,modified,JsonSerializer.Serialize(new VersionInfo(tag,size,modified.UtcTicks)))));
            }
            if(result.HasMore&&result.SyncToken==syncToken)throw new IOException("DAV 续批令牌未前进，拒绝无限循环。");
            return new(entries,deleted,result.SyncToken!,result.HasMore);
        }
        catch(WebDAVException){throw new IOException("DAV 同步 REPORT 格式无效，未推进检查点。");}
        catch(OperationCanceledException error)when(!token.IsCancellationRequested){throw WebDavNetworkDiagnostics.Failure(error,"REPORT 远端检测",timeout:true);}
    }
}
