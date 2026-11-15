using HybridMount.Helpers.Paths;
using HybridMount.Infrastructure.Configuration;
using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Files;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Infrastructure.Protocols.WebDAV;
using HybridMount.Models.Configuration;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.Services.Sync;
using HybridMount.Tests.Fixtures.Protocols;
using HybridMount.Tests.Integration.Protocols;
using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
namespace HybridMount.Tests.Fixtures.Protocols;

/// <summary>只补充发现及 REPORT，GET/PUT 继续走现有真实 HTTP 协议服务。</summary>
internal sealed class SyncDavFixture : IAsyncDisposable
{
    internal static readonly XNamespace D="DAV:";
    public readonly DavFixture Dav;
    public readonly string Registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
    public bool Advertised=true;
    public int Sequence=1;
    public Func<string,string?,HttpTestServer.Response>? Report;
    public SyncDavFixture(DavFixture? dav=null)
    {
        Dav=dav??new();var original=Dav.Server.Handler;
        Dav.Server.Handler=request=>
        {
            var path=Uri.UnescapeDataString(new Uri(Dav.Server.Address,request.Target).AbsolutePath[5..]).TrimEnd('/');
            if(request.Method=="PROPFIND"&&Encoding.UTF8.GetString(request.Body).Contains("supported-report-set"))
            {
                var property=new XElement(D+"prop",new XElement(D+"supported-report-set",Advertised?new XElement(D+"supported-report",new XElement(D+"report",new XElement(D+"sync-collection"))):null),new XElement(D+"sync-token","urn:test:"+Sequence));
                return Task.FromResult(Multi("urn:test:"+Sequence,[new XElement(D+"response",new XElement(D+"href","/dav/"+Escape(path)),new XElement(D+"propstat",property,new XElement(D+"status","HTTP/1.1 200 OK")))]));
            }
            if(request.Method=="REPORT")
            {
                var value=XDocument.Parse(Encoding.UTF8.GetString(request.Body)).Root!.Element(D+"sync-token")!.Value;var token=value.Length==0?null:value;
                return Task.FromResult(Report?.Invoke(path,token)??DefaultReport(path,token));
            }
            return original(request);
        };
    }
    public MountRepository Repository()=>new(Path.Combine(Dav.Area,"data"),Registry);
    internal HttpTestServer.Response DefaultReport(string path,string? token)=>Multi("urn:test:"+Sequence,token=="urn:test:"+Sequence?[]:
        Dav.Objects.Keys.Where(x=>x.Length!=0&&RelativePaths.Parent(x)==path).Select(Item));
    internal XElement Item(string path)
    {
        var item=Dav.Objects[path];return new XElement(D+"response",new XElement(D+"href","/dav/"+Escape(path)+(item.Directory?"/":"")),new XElement(D+"propstat",new XElement(D+"prop",
            new XElement(D+"resourcetype",item.Directory?new XElement(D+"collection"):null),new XElement(D+"getcontentlength",item.Bytes.Length),new XElement(D+"getlastmodified","Thu, 29 Oct 2026 12:00:00 GMT"),
            new XElement(D+"getetag",Dav.WeakEtag?"W/"+item.ETag:item.ETag)),new XElement(D+"status","HTTP/1.1 200 OK")));
    }
    internal static string Escape(string path)=>string.Join('/',path.Split('/').Select(Uri.EscapeDataString));
    internal static XElement Resource(string href,int status)=>new(D+"response",new XElement(D+"href",href),new XElement(D+"status",$"HTTP/1.1 {status} Result"));
    internal static HttpTestServer.Response Multi(string token,IEnumerable<XElement> resources)=>HttpTestServer.Response.Text(207,new XElement(D+"multistatus",resources,new XElement(D+"sync-token",token)).ToString());
    public async ValueTask DisposeAsync(){Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Registry,false);await Dav.DisposeAsync();}
}
