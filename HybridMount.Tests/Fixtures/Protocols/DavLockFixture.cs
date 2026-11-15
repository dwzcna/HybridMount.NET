using HybridMount.Models.Mounting;
using HybridMount.Tests.Fixtures.Protocols;
using HybridMount.Tests.Integration.Protocols;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Xml.Linq;
namespace HybridMount.Tests.Fixtures.Protocols;

/// <summary>围绕现有 DAV HTTP 服务补充锁协议；可模拟不可靠的标签和锁实现。</summary>
internal sealed class DavLockFixture : IAsyncDisposable
{
    public DavFixture Dav { get; }=new();
    public ConcurrentDictionary<string,string> Locks { get; }=new(StringComparer.Ordinal);
    public bool RawTags,IgnoreExclusive,IgnoreDeleteMove,BareLockHeader;
    private readonly Func<HttpTestServer.Request,Task<HttpTestServer.Response>> _inner;
    public DavLockFixture()
    {
        _inner=Dav.Server.Handler;Dav.XmlTag=item=>RawTags?item.ETag.Trim('"'):item.ETag;Dav.HttpTag=item=>RawTags?item.ETag.Trim('"'):item.ETag;
        Dav.Server.Handler=Handle;
    }
    private Task<HttpTestServer.Response> Handle(HttpTestServer.Request request)
    {
        var uri=new Uri(Dav.Server.Address,request.Target);var path=Uri.UnescapeDataString(uri.AbsolutePath[5..]).TrimEnd('/');
        if(request.Method=="LOCK")
        {
            if(request.Body.Length==0)
            {
                if(!Locks.TryGetValue(path,out var existing)||request.Headers.GetValueOrDefault("If")!="(<"+existing+">)")return Result(412);
                return LockResponse(200,existing);
            }
            if(request.Headers.GetValueOrDefault("If-Match")=="*"&&!Dav.Objects.ContainsKey(path))return Result(412);
            if(Locks.ContainsKey(path)&&!IgnoreExclusive)return Result(423);
            var created=!Dav.Objects.ContainsKey(path);if(created)Dav.Put(path,[]);
            var value="opaquelocktoken:"+Guid.NewGuid();Locks[path]=value;return LockResponse(created?201:200,value);
        }
        if(request.Method=="UNLOCK")
        {
            if(!Locks.TryGetValue(path,out var value)||request.Headers.GetValueOrDefault("Lock-Token")!="<"+value+">")return Result(409);
            Locks.TryRemove(path,out _);return Result(204);
        }
        if(request.Method is "PUT" or "DELETE" or "MOVE")
        {
            if(Locks.TryGetValue(path,out var value)&&request.Headers.GetValueOrDefault("If")!="(<"+value+">)")return Result(423);
            if(IgnoreDeleteMove&&request.Method is "DELETE" or "MOVE")
            {var headers=new Dictionary<string,string>(request.Headers,StringComparer.OrdinalIgnoreCase);headers.Remove("If-Match");request=request with{Headers=headers};}
        }
        return _inner(request);
    }
    private static Task<HttpTestServer.Response> Result(int status)=>Task.FromResult(new HttpTestServer.Response(status,[]));
    private Task<HttpTestServer.Response> LockResponse(int status,string token)
    {
        XNamespace d="DAV:";
        var xml=new XElement(d+"prop",new XElement(d+"lockdiscovery",new XElement(d+"activelock",
            new XElement(d+"locktype",new XElement(d+"write")),new XElement(d+"lockscope",new XElement(d+"exclusive")),
            new XElement(d+"depth","0"),new XElement(d+"lockroot",new XElement(d+"href",Dav.Server.Address.AbsoluteUri+"dav/")),new XElement(d+"timeout","Second-300"),new XElement(d+"locktoken",new XElement(d+"href",token)))));
        return Task.FromResult(HttpTestServer.Response.Text(status,xml.ToString(),new(){["Lock-Token"]=BareLockHeader?token:"<"+token+">"}));
    }
    public ValueTask DisposeAsync()=>Dav.DisposeAsync();
}
