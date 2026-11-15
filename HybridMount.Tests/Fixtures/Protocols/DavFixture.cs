using HybridMount.Helpers.Paths;
using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Files;
using HybridMount.Infrastructure.Files.Content;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Infrastructure.Protocols.WebDAV;
using HybridMount.Infrastructure.Windows.CloudFiles;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.Tests.Fixtures.Protocols;
using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
namespace HybridMount.Tests.Fixtures.Protocols;

internal sealed class DavFixture : IAsyncDisposable
{
    internal sealed record Item(byte[] Bytes,string ETag,bool Directory=false);
    public string Area { get; }=Path.Combine(Path.GetTempPath(),"HybridMount.HttpTests",Guid.NewGuid().ToString("N"));
    public HttpTestServer Server { get; }=new();
    public ConcurrentDictionary<string,Item> Objects { get; }=new(StringComparer.Ordinal);
    public byte[] Bytes { get; }=Enumerable.Range(0,16397).Select(index=>(byte)(index*17)).ToArray();
    public bool IgnoreRange,WeakEtag,BadRange,IgnoreConditions;
    public string? XmlOverride;
    public Func<Item,string?>? XmlTag,HttpTag;
    private int _version;
    public FileSourceConfiguration Options=>new(FileSourceKind.WebDav,new Uri(Server.Address,"dav/").AbsoluteUri,AllowHttp:true);
    public DavFixture(){Directory.CreateDirectory(Area);Objects[""]=new([],"\"root\"",true);Objects["空目录"]=new([],"\"dir\"",true);Put("文件.bin",Bytes);Server.Handler=Handle;}
    public void Put(string path,byte[] bytes)=>Objects[path]=new(bytes,"\"v"+Interlocked.Increment(ref _version)+"\"");
    public WebDavFileSource Source()=>new(Options,new("user","app-password"),new WholeFileCache(Path.Combine(Area,"cache")));
    private string Tag(Item item)=>WeakEtag?"W/"+item.ETag:item.ETag;
    private Task<HttpTestServer.Response> Handle(HttpTestServer.Request request)
    {
        var uri=new Uri(Server.Address,request.Target);var path=Uri.UnescapeDataString(uri.AbsolutePath[5..]).TrimEnd('/');
        HttpTestServer.Response response;
        Objects.TryGetValue(path,out var item);
        if(request.Method=="OPTIONS")response=new(200,[],new(){["DAV"]="1",["Allow"]="PROPFIND,GET,PUT,MKCOL,MOVE,DELETE"});
        else if(request.Method=="PROPFIND")
        {
            if(item is null)return Task.FromResult(new HttpTestServer.Response(404,[]));
            XNamespace d="DAV:";
            var selected=Objects.Where(pair=>pair.Key==path||(request.Headers["Depth"]=="1"&&pair.Key.Length!=0&&RelativePaths.Parent(pair.Key)==path));
            var xml=new XElement(d+"multistatus",selected.Select(pair=>new XElement(d+"response",new XElement(d+"href","/dav/"+string.Join('/',pair.Key.Split('/').Select(Uri.EscapeDataString))+(pair.Value.Directory&&pair.Key!=""?"/":"")),
                new XElement(d+"propstat",new XElement(d+"status","HTTP/1.1 200 OK"),new XElement(d+"prop",new XElement(d+"resourcetype",pair.Value.Directory?new XElement(d+"collection"):null),
                    new XElement(d+"getcontentlength",pair.Value.Bytes.Length),new XElement(d+"getlastmodified","Thu, 29 Oct 2026 12:00:00 GMT"),new XElement(d+"getetag",XmlTag is null?Tag(pair.Value):XmlTag(pair.Value)))))));
            response=HttpTestServer.Response.Text(207,XmlOverride??xml.ToString());
        }
        else if(request.Method=="GET")
        {
            if(item is null)return Task.FromResult(new HttpTestServer.Response(404,[]));
            if(request.Headers.TryGetValue("If-Match",out var match)&&match!=Tag(item))return Task.FromResult(new HttpTestServer.Response(412,[]));
            var headers=new Dictionary<string,string>{["Last-Modified"]="Thu, 29 Oct 2026 12:00:00 GMT"};
            var responseTag=HttpTag is null?Tag(item):HttpTag(item);if(responseTag is not null)headers["ETag"]=responseTag;
            if(!IgnoreRange&&request.Headers.TryGetValue("Range",out var range))
            {var limits=range[6..].Split('-').Select(int.Parse).ToArray();headers["Content-Range"]=$"bytes {limits[0]+(BadRange?1:0)}-{limits[1]}/{item.Bytes.Length}";response=new(206,item.Bytes[limits[0]..(limits[1]+1)],headers);}
            else response=new(200,item.Bytes,headers);
        }
        else if(request.Method is "PUT" or "MKCOL" or "MOVE" or "DELETE")
        {
            if(!IgnoreConditions&&((request.Headers.GetValueOrDefault("If-None-Match")=="*"&&item is not null)||(request.Headers.TryGetValue("If-Match",out var match)&&match!=item?.ETag)))return Task.FromResult(new HttpTestServer.Response(412,[]));
            if(request.Method=="PUT")Put(path,request.Body);
            else if(request.Method=="MKCOL")Objects[path]=new([],"\"directory\"",true);
            else if(request.Method=="DELETE")Objects.TryRemove(path,out _);
            else {var destination=Uri.UnescapeDataString(new Uri(request.Headers["Destination"]).AbsolutePath[5..]);if(Objects.ContainsKey(destination))return Task.FromResult(new HttpTestServer.Response(412,[]));Objects[destination]=item!;Objects.TryRemove(path,out _);}
            response=new(request.Method is "DELETE" or "MOVE"?204:201,[]);
        }
        else response=new(405,[]);
        return Task.FromResult(response);
    }
    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        try{if(Directory.Exists(Area)&&!MountPathGuard.ContainsCloudPlaceholders(Area))Directory.Delete(Area,true);}
        catch(System.ComponentModel.Win32Exception)
        {
            // 只在隔离夹具失败时补充精确路径；不跳过占位保护，也不跟随重解析点清理外部目标。
            var pending=new Stack<string>();pending.Push(Area);
            while(pending.TryPop(out var directory))foreach(var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes=File.GetAttributes(path);
                try{_ = CloudFilesNative.Inspect(path);}
                catch(Exception error){throw new IOException("隔离夹具清理核验失败："+Path.GetRelativePath(Area,path)+"；attributes="+attributes,error);}
                if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))==FileAttributes.Directory)pending.Push(path);
            }
            throw;
        }
    }
}
