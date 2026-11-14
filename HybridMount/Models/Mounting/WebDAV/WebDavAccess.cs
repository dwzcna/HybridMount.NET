using System.Net;
using System.Xml.Linq;
using WebDAVClient;
using WebDAVClient.Extensions;
using WebDAVClient.Protocol;

namespace HybridMount.Models.Mounting.WebDAV;

public sealed partial class WebDavFileSource:IFileSourceAccess
{
    private readonly object _accessGate=new();
    private readonly Dictionary<string,SourceAccessSnapshot> _access=new(StringComparer.Ordinal);
    public SourceAccessSnapshot? AccessAt(string path)
    {lock(_accessGate)return _access.GetValueOrDefault(RelativePaths.Normalize(path));}
    public void InvalidateAccess(){lock(_accessGate)_access.Clear();}
    private SourceAccessSnapshot RememberAccess(string path,XElement? value,string reason)
    {
        var snapshot=ParseAccess(_protocolEvidence.Snapshot.SessionId,path,value,reason);
        lock(_accessGate)
        {
            if(!_access.ContainsKey(path)&&_access.Count>=512)_access.Remove(_access.MinBy(pair=>pair.Value.ObservedUtc).Key);
            _access[path]=snapshot;
        }
        _protocolEvidence.Record("dav.privileges",reason=="Verified"&&snapshot.Reason=="Verified"?ProtocolCapabilityStatus.Supported:ProtocolCapabilityStatus.Unknown,path,
            snapshot.Reason=="Permission"?ProtocolCapabilityReason.Permission:snapshot.Reason=="Verified"?ProtocolCapabilityReason.None:ProtocolCapabilityReason.Uncertain);
        return snapshot;
    }
    /// <summary>解析RFC3744有效权限集合。有效空集合为拒绝；无法解释、属性缺失或受限保持未知，绝不从名称推断。</summary>
    internal static SourceAccessSnapshot ParseAccess(Guid session,string path,XElement? value,string reason="Verified")
    {
        var names=new HashSet<string>(StringComparer.Ordinal);
        if(value is not null&&reason=="Verified")
        {
            if(value.Name!=Dav+"current-user-privilege-set"||!string.IsNullOrWhiteSpace(string.Concat(value.Nodes().OfType<XText>().Select(node=>node.Value))))reason="Malformed";
            foreach(var item in value.Elements())
            {
                var parts=item.Elements().ToArray();
                if(item.Name!=Dav+"privilege"||parts.Length!=1||parts[0].HasElements||!string.IsNullOrWhiteSpace(parts[0].Value)||parts[0].Name.Namespace!=Dav||
                    parts[0].Name.LocalName is not("all" or "read" or "write" or "write-content" or "write-properties" or "bind" or "unbind" or "read-acl" or "write-acl" or "read-current-user-privilege-set" or "unlock"))reason="Malformed";
                else names.Add(parts[0].Name.LocalName);
            }
        }
        else if(value is null&&reason=="Verified")reason="Missing";
        SourceAccessDecision Get(string privilege)=>reason!="Verified"?SourceAccessDecision.Unknown:
            names.Contains("all")||names.Contains(privilege)||privilege!="read"&&names.Contains("write")?SourceAccessDecision.Allowed:SourceAccessDecision.Denied;
        return new(session,path,DateTimeOffset.UtcNow,Get("read"),Get("write-content"),Get("bind"),Get("unbind"),reason);
    }
    private async Task<SourceAccessSnapshot> QueryAccessAsync(string path,bool refresh,CancellationToken token)
    {
        path=RelativePaths.Normalize(path);var before=AccessAt(path);
        if(!refresh&&before is not null&&before.SessionId==_protocolEvidence.Snapshot.SessionId&&DateTimeOffset.UtcNow-before.ObservedUtc<TimeSpan.FromSeconds(60))return before;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(MetadataDeadline);
        try
        {
            var result=await _dav.PropFindAsync(LibraryPath(Address(path)),new PropFindOptions{Depth=DavDepth.Zero,Selection=DavPropertySelection.Selected,
                Properties=[new("DAV:","current-user-privilege-set")]},deadline.Token).ConfigureAwait(false);
            if(result.StatusCode is 405 or 501)return RememberAccess(path,null,"Unsupported");
            if(result.StatusCode is 401 or 403)return RememberAccess(path,null,"Permission");
            if(result.StatusCode!=207||result.Resources.Count!=1)return RememberAccess(path,null,"Uncertain");
            var resource=result.Resources[0];
            if(resource.Hrefs.Count!=1||MapHref(resource.Hrefs[0],Address(path),result.EffectiveUri)!=path||resource.StatusCode is >=400)return RememberAccess(path,null,"Uncertain");
            return AccessResource(resource,path);
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(Exception error)when(error is IOException or HttpRequestException or OperationCanceledException or WebDAVClient.Helpers.WebDAVException)
        {return RememberAccess(path,null,"Uncertain");}
    }
    private SourceAccessSnapshot AccessResource(DavResourceResponse resource,string path)
    {
        var properties=resource.GetProperties(new("DAV:","current-user-privilege-set")).ToArray();
        if(properties.Length==0)return RememberAccess(path,null,"Missing");
        if(properties.Length!=1)return RememberAccess(path,null,"Malformed");
        var property=properties[0];
        return property.StatusCode==200?RememberAccess(path,property.Value.ToElement(),"Verified"):
            RememberAccess(path,null,property.StatusCode==404?"Missing":property.StatusCode==403?"Permission":"Uncertain");
    }
    private async Task RequireAccessAsync(string path,SourcePrivilege privilege,CancellationToken token,bool refresh=true)
    {
        var access=await QueryAccessAsync(path,refresh,token).ConfigureAwait(false);
        if(access.For(privilege)==SourceAccessDecision.Allowed)return;
        // 可选属性未实现时保留原有经验证的条件／锁路径；不制造权限结论，查询故障则暂缓而不发送目标写入。
        if(access.For(privilege)==SourceAccessDecision.Unknown&&access.Reason is "Missing" or "Unsupported")return;
        throw new SourcePermissionException(access,privilege);
    }
    /// <summary>最终不可逆请求前刷新精确资源权限；锁释放不受此预检阻挡，避免资源无法收尾。</summary>
    private async Task CheckRequestAccessAsync(HttpRequestMessage request,CancellationToken token)
    {
        var path=HrefPath(request.RequestUri!.AbsoluteUri,_endpoint);
        switch(request.Method.Method)
        {
            case "PUT":
                if(request.Headers.IfNoneMatch.Any(value=>value.Tag=="*"))await RequireAccessAsync(RelativePaths.Parent(path),SourcePrivilege.Bind,token).ConfigureAwait(false);
                else await RequireAccessAsync(path,SourcePrivilege.WriteContent,token).ConfigureAwait(false);
                break;
            case "MKCOL":await RequireAccessAsync(RelativePaths.Parent(path),SourcePrivilege.Bind,token).ConfigureAwait(false);break;
            case "DELETE":await RequireAccessAsync(RelativePaths.Parent(path),SourcePrivilege.Unbind,token).ConfigureAwait(false);break;
            case "MOVE":
                await RequireAccessAsync(RelativePaths.Parent(path),SourcePrivilege.Unbind,token).ConfigureAwait(false);
                var target=HrefPath(new Uri(request.Headers.GetValues("Destination").Single()).AbsoluteUri,_endpoint);
                await RequireAccessAsync(RelativePaths.Parent(target),SourcePrivilege.Bind,token).ConfigureAwait(false);break;
        }
    }
}
