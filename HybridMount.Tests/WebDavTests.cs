using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>真实 HTTP 协议回归与真实 CFAPI 网络源集成；不将本地服务当真实提供商验收。</summary>
public sealed class WebDavTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bare-value")]
    [InlineData("W/\"weak\"")]
    [InlineData("  \"strong\"  ")]
    public async Task OptionalDirectoryTagsNeverBlockAndLegacyWhitespaceIsEquivalent(string tag)
    {
        await using var fixture=new DavFixture();
        fixture.XmlTag=item=>item.Directory?tag:item.ETag;
        await using var source=fixture.Source();await source.ProbeAsync();
        Assert.Equal(2,(await All(source.ListAsync(""))).Count);
        var entry=await source.GetMetadataAsync("文件.bin");
        var legacy=entry.Version.Replace("\\u0022v1\\u0022","  \\u0022v1\\u0022  ");
        Assert.NotEqual(entry.Version,legacy);
        Assert.True(source.IsSameVersion(entry.Version,legacy));
        Assert.False(source.IsSameVersion(entry.Version,entry.Version.Replace("v1","v2")));
    }
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("bare")]
    [InlineData("W/\"weak\"")]
    public async Task UnreliableFileTagsUseReadOnlyFullSnapshot(string tag)
    {
        await using var fixture=new DavFixture();
        fixture.XmlTag=_=>tag;fixture.HttpTag=_=>null;
        await using var source=fixture.Source();
        var entry=await source.GetMetadataAsync("文件.bin");var data=new byte[17];
        await source.ReadRangeAsync(entry,6,data);
        Assert.Equal(fixture.Bytes[6..23],data);
        Assert.Equal(CapabilitySupport.Unsupported,source.Capabilities.RangeRead);
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Headers.ContainsKey("Range")||r.Headers.ContainsKey("If-Match")||r.Method=="PUT");
    }
    [Fact]
    public async Task StrongResponseDifferenceStillRejectsAndOuterWhitespaceNormalizes()
    {
        await using var fixture=new DavFixture();
        fixture.XmlTag=item=>"  "+item.ETag+"  ";
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");
        await source.ReadRangeAsync(entry,0,new byte[4]);
        fixture.HttpTag=_=>"\"changed\"";
        await Assert.ThrowsAsync<SourceConflictException>(async()=>await source.ReadRangeAsync(entry,0,new byte[4]));
    }
    [Fact]
    public async Task DepthOneAuthenticationAndVersionPinnedRanges()
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();await source.ProbeAsync();
        var entries=await All(source.ListAsync(""));Assert.Equal(2,entries.Count);Assert.All(entries,entry=>Assert.Equal("",entry.StableId));
        var metadata=await source.GetMetadataAsync("文件.bin");var bytes=new byte[113];
        Assert.Equal(113,await source.ReadRangeAsync(metadata,4093,bytes));Assert.Equal(fixture.Bytes[4093..4206],bytes);
        Assert.Equal(CapabilitySupport.Supported,source.Capabilities.RangeRead);
        Assert.Contains(fixture.Server.Requests,request=>request.Method=="PROPFIND"&&request.Headers["Depth"]=="1");
        Assert.All(fixture.Server.Requests,request=>Assert.Equal("Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("user:app-password")),request.Headers["Authorization"]));
        fixture.Put("文件.bin",[1,2,3]);
        await Assert.ThrowsAsync<SourceConflictException>(async()=>await source.ReadRangeAsync(metadata,0,bytes));
    }
    [Fact]
    public async Task IgnoredRangeBecomesOneImmutableWholeFileCache()
    {
        await using var fixture=new DavFixture{IgnoreRange=true};await using var source=fixture.Source();
        var metadata=await source.GetMetadataAsync("文件.bin");var bytes=new byte[71];
        Assert.Equal(71,await source.ReadRangeAsync(metadata,777,bytes));Assert.Equal(fixture.Bytes[777..848],bytes);
        Assert.Equal(71,await source.ReadRangeAsync(metadata,4093,bytes));Assert.Equal(fixture.Bytes[4093..4164],bytes);
        Assert.Equal(1,fixture.Server.Requests.Count(request=>request.Method=="GET"));
        Assert.Equal(CapabilitySupport.Unsupported,source.Capabilities.RangeRead);
    }
    [Fact]
    public async Task WeakEtagUsesSingleWholeFileResponse()
    {
        await using var fixture=new DavFixture{WeakEtag=true};await using var source=fixture.Source();
        var metadata=await source.GetMetadataAsync("文件.bin");var bytes=new byte[3];await source.ReadRangeAsync(metadata,17,bytes);
        Assert.Equal(fixture.Bytes[17..20],bytes);Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="GET"&&request.Headers.ContainsKey("Range"));
    }
    [Fact]
    public async Task RejectMalformedRangeAndCrossRootAndExternalEntities()
    {
        await using var fixture=new DavFixture{BadRange=true};await using var source=fixture.Source();
        var metadata=await source.GetMetadataAsync("文件.bin");await Assert.ThrowsAsync<IOException>(async()=>await source.ReadRangeAsync(metadata,4,new byte[3]));
        fixture.XmlOverride="""<!DOCTYPE foo [<!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]><d:multistatus xmlns:d="DAV:">&x;</d:multistatus>""";
        await Assert.ThrowsAsync<IOException>(()=>source.GetMetadataAsync(""));
        fixture.XmlOverride="""<d:multistatus xmlns:d="DAV:"><d:response><d:href>/outside/file</d:href></d:response></d:multistatus>""";
        await Assert.ThrowsAsync<IOException>(()=>source.GetMetadataAsync(""));
    }
    [Fact]
    public async Task ConditionalCreateUpdateMoveDeleteAndIgnoredConditionsRejected()
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();
        using var input=new MemoryStream([1,2,3]);var created=await source.UploadAsync("new.bin",input,null);Assert.True(input.CanRead);
        Assert.Equal(CapabilitySupport.Supported,source.Capabilities.ConditionalWrite);
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("new.bin",Stream.Null,null));
        var updated=await source.UploadAsync("new.bin",new MemoryStream([4]),created.Version);
        await source.CreateDirectoryAsync("dir");var moved=await source.MoveAsync("new.bin","dir/moved.bin",updated.Version);
        await source.DeleteAsync(moved.Path,moved.Version);Assert.False(fixture.Objects.ContainsKey("dir/moved.bin"));
        Assert.DoesNotContain(fixture.Objects.Keys,name=>name.StartsWith(".hybridmount-probe-"));
        await using var ignoring=new DavFixture{IgnoreConditions=true};await using var unsafeSource=ignoring.Source();
        await unsafeSource.UploadAsync("user-file.bin",Stream.Null,null);
        await Assert.ThrowsAsync<SourceConflictException>(()=>unsafeSource.UploadAsync("user-file.bin",new MemoryStream([99]),null));Assert.Empty(ignoring.Objects["user-file.bin"].Bytes);
    }
    [Fact]
    public async Task CredentialCiphertextAndReservedProtocolsAndLegacyDefaults()
    {
        await using var fixture=new DavFixture();var store=new CredentialStore(fixture.Area);var id=Guid.NewGuid();
        var secret=new SourceCredentials("account","unique-secret-value");store.Save(id,secret);Assert.Equal(secret,store.Load(id));
        Assert.DoesNotContain("unique-secret-value",Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(fixture.Area,"Credentials",id.ToString("N")+".bin"))));
        Assert.Throws<ArgumentException>(()=>FileSourceFactory.Endpoint(new(FileSourceKind.WebDav,"https://user:password@example.com/dav/")));
        Assert.Throws<ArgumentException>(()=>FileSourceFactory.Endpoint(new(FileSourceKind.WebDav,"http://example.com/")));
        var repository=new MountRepository(Path.Combine(fixture.Area,"data"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));
        foreach(var kind in new[]{FileSourceKind.Smb,FileSourceKind.Nfs})
            await Assert.ThrowsAsync<NotSupportedException>(()=>new FileSourceFactory().CreateAsync(new(id,"reserved",fixture.Area,Source:new(kind)),repository,false,default));
    }
    [Fact(Timeout=120000)]
    public async Task RealCfApiWebDavRefreshCacheAndLocalProtection()
    {
        await using var fixture=new DavFixture();var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(fixture.Area,"data"),registry);
        await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));
        await using var manager=new MountManager(repository,logger);
        var mount=await manager.AddAsync("HTTP CFAPI 测试",local,true,source:fixture.Options,credentials:new("user","app-password"));
        try
        {
            var path=Path.Combine(local,"文件.bin");Assert.Equal(fixture.Bytes,await Task.Run(()=>File.ReadAllBytes(path)).WaitAsync(TimeSpan.FromSeconds(20)));
            var keep=await Finished(manager,manager.Queue(mount.Id,"keepOnDevice",[path]));Assert.Equal("Completed",keep.State);
            manager.CacheLimitBytes=1;await manager.EnforceCacheLimitAsync();Assert.True(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
            await Finished(manager,manager.Queue(mount.Id,"freeSpace",[path]));
            var replacement=Encoding.UTF8.GetBytes("远端更新后的全新内容");fixture.Put("文件.bin",replacement);
            var refresh=await Finished(manager,manager.Queue(mount.Id,"refresh"));Assert.Equal("Completed",refresh.State);
            Assert.Equal(replacement.Length,new FileInfo(path).Length);Assert.Equal(replacement,await Task.Run(()=>File.ReadAllBytes(path)).WaitAsync(TimeSpan.FromSeconds(20)));
            await manager.EnforceCacheLimitAsync();Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
            await File.WriteAllTextAsync(path,"本地修改保留");fixture.Put("文件.bin",[1,2,3,4]);
            var conflict=await Finished(manager,manager.Queue(mount.Id,"refresh"));Assert.Equal("Failed",conflict.State);
            await manager.EnforceCacheLimitAsync();Assert.Equal("本地修改保留",await File.ReadAllTextAsync(path));
        }
        finally
        {
            var cleared=await Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));Assert.Equal("Completed",cleared.State);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }
    [Fact(Timeout=120000)]
    public async Task CaseCollisionEnumerationDoesNotCreatePartialPlaceholders()
    {
        await using var fixture=new DavFixture();fixture.Put("Case.txt",[1]);fixture.Put("case.txt",[2]);
        var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(fixture.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));
        await using var manager=new MountManager(repository,logger);
        await Assert.ThrowsAsync<IOException>(()=>manager.AddAsync("碰撞测试",local,true,source:fixture.Options));
        Assert.Empty(Directory.EnumerateFileSystemEntries(local));var mount=Assert.Single(manager.Mounts).Definition;
        var result=await Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));Assert.True(result.State=="Completed",result.Error+string.Join(";",result.Results));
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
    }
    internal static async Task<List<SourceEntry>> All(IAsyncEnumerable<SourceEntry> source){var list=new List<SourceEntry>();await foreach(var item in source)list.Add(item);return list;}
    internal static async Task<MountOperationSnapshot> Finished(MountManager manager,string id)
    {
        var deadline=DateTime.UtcNow.AddSeconds(30);
        while(DateTime.UtcNow<deadline){var job=manager.GetOperation(id)!;if(job.State is not("Queued" or "Running"))return job;await Task.Delay(20);}
        manager.Cancel(id);throw new TimeoutException("CFAPI task");
    }
}

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
        if(Directory.Exists(Area)&&!MountPathGuard.ContainsCloudPlaceholders(Area))Directory.Delete(Area,true);
    }
}
