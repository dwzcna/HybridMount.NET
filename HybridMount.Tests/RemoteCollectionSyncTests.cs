using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>RFC 6578 通过真实 HTTP DavClient 验证；不作为真实服务器兼容性认证。</summary>
public sealed class RemoteCollectionSyncTests
{
    [Fact]
    public async Task DiscoveryAndReportsAreReadOnlyAndPerDirectory()
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();
        var engine=new RemoteSyncEngine(mount,"scope",source,source,repo);await engine.PollAsync(false,TimeSpan.Zero,default);
        Assert.Contains("RFC 6578",engine.Mode);Assert.Equal(2,repo.RemoteCollections(mount,"scope").Count);
        fixture.Dav.Put("空目录/child.txt",[1]);fixture.Sequence++;
        await engine.PollAsync(false,TimeSpan.Zero,default);
        Assert.Contains(repo.RemotePending(mount,"scope"),x=>x.Path=="空目录/child.txt");
        var reports=fixture.Dav.Server.Requests.Where(x=>x.Method=="REPORT").ToArray();Assert.Equal(4,reports.Length);
        Assert.All(reports,x=>{Assert.Equal("0",x.Headers["Depth"]);Assert.Equal("1",XDocument.Parse(Encoding.UTF8.GetString(x.Body)).Root!.Element(SyncDavFixture.D+"sync-level")!.Value);});
        Assert.All(fixture.Dav.Server.Requests,x=>Assert.Contains(x.Method,new[]{"PROPFIND","REPORT"}));
    }
    [Theory]
    [InlineData("https://other.invalid/dav/item")][InlineData("/outside/item")][InlineData("/dav/a/b")][InlineData("/dav/%2e%2e/escape")]
    public async Task UnsafeReportHrefNeverBecomesDeletion(string href)
    {
        await using var fixture=new SyncDavFixture();fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:test:1",[SyncDavFixture.Resource(href,404)]);
        await using var source=fixture.Dav.Source();await Assert.ThrowsAnyAsync<IOException>(()=>source.GetCollectionChangesAsync("","urn:test:0",default));
    }
    [Fact]
    public async Task OnlyResourceLevel404MeansDeleted()
    {
        await using var fixture=new SyncDavFixture();var entry=fixture.Item("文件.bin");
        entry.Elements(SyncDavFixture.D+"propstat").Single().Element(SyncDavFixture.D+"prop")!.Element(SyncDavFixture.D+"getetag")!.Remove();
        entry.Add(new XElement(SyncDavFixture.D+"propstat",new XElement(SyncDavFixture.D+"prop",new XElement(SyncDavFixture.D+"getetag")),new XElement(SyncDavFixture.D+"status","HTTP/1.1 404 Not Found")));
        fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:test:1",[entry,SyncDavFixture.Resource("/dav/gone",404)]);
        await using var source=fixture.Dav.Source();var batch=await source.GetCollectionChangesAsync("","urn:test:0",default);
        Assert.Single(batch.Entries);Assert.Equal(new[]{"gone"},batch.Deleted);
        fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:test:2",[new XElement(SyncDavFixture.D+"response",new XElement(SyncDavFixture.D+"href","/dav/file"),
            new XElement(SyncDavFixture.D+"propstat",new XElement(SyncDavFixture.D+"prop",new XElement(SyncDavFixture.D+"resourcetype")),new XElement(SyncDavFixture.D+"status","HTTP/1.1 404 Not Found")))]);
        await Assert.ThrowsAnyAsync<IOException>(()=>source.GetCollectionChangesAsync("","urn:test:1",default));
    }
    [Fact]
    public async Task Initial404AndOrdinaryHttp404AreErrors()
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();
        fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:test:1",[SyncDavFixture.Resource("/dav/gone",404)]);
        await Assert.ThrowsAnyAsync<IOException>(()=>source.GetCollectionChangesAsync("",null,default));
        fixture.Report=(_,_)=>new(404,[]);
        var error=await Assert.ThrowsAsync<RemoteSyncException>(()=>source.GetCollectionChangesAsync("","urn:test:0",default));Assert.Equal(404,error.Status);
    }
    [Fact]
    public async Task PartialBaselinePublishesAdditionsWithoutInferringMissingAndRecoversFromRoot()
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();
        var old=await source.GetMetadataAsync("文件.bin");repo.SaveEntry(mount,new("old","gone","gone",old.Version,false,Materialized:true));
        var calls=0;fixture.Report=(_,token)=>++calls==1?SyncDavFixture.Multi("urn:test:next",[fixture.Item("文件.bin"),SyncDavFixture.Resource("/dav/",507)]):new(503,[]);
        var engine=new RemoteSyncEngine(mount,"scope",source,source,repo);
        await Assert.ThrowsAsync<RemoteSyncException>(()=>engine.PollAsync(false,TimeSpan.Zero,default));
        Assert.Contains(repo.RemotePending(mount,"scope"),item=>item.Path=="文件.bin"&&item.Entry is not null);Assert.DoesNotContain(repo.RemotePending(mount,"scope"),item=>item.Entry is null);var checkpoint=Assert.Single(repo.RemoteCollections(mount,"scope"));
        Assert.True(checkpoint.BuildingBaseline);Assert.Equal("Full",checkpoint.Mode);Assert.Null(checkpoint.Baseline);
        fixture.Report=(_,token)=>{Assert.Null(token);return SyncDavFixture.Multi("urn:test:done",[fixture.Item("文件.bin")]);};
        var restarted=new MountRepository(repo.DataDirectory,fixture.Registry);await new RemoteSyncEngine(mount,"scope",source,source,restarted).PollAsync(false,TimeSpan.Zero,default);
        Assert.False(Assert.Single(restarted.RemoteCollections(mount,"scope")).BuildingBaseline);
        Assert.Contains(restarted.RemotePending(mount,"scope"),x=>x.Path=="gone"&&x.Entry is null);
        Assert.Contains(restarted.RemotePending(mount,"scope"),x=>x.Path=="文件.bin"&&x.Entry is not null);
    }
    [Fact]
    public async Task InvalidTokenRebuildKeepsOldStateUntilComplete()
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();
        var engine=new RemoteSyncEngine(mount,"scope",source,source,repo);await engine.PollAsync(false,TimeSpan.Zero,default);
        fixture.Dav.Objects.TryRemove("文件.bin",out _);var reset=false;
        fixture.Report=(path,token)=>token is not null?HttpTestServer.Response.Text(403,"<d:error xmlns:d=\"DAV:\"><d:valid-sync-token/></d:error>",new(){["Content-Type"]="application/xml"}):
            reset?fixture.DefaultReport(path,token):new(503,[]);
        await Assert.ThrowsAsync<RemoteSyncException>(()=>engine.PollAsync(false,TimeSpan.Zero,default));
        Assert.Contains(repo.RemoteShadow(mount,"scope"),x=>x.Entry.Path=="文件.bin");
        reset=true;await engine.PollAsync(false,TimeSpan.Zero,default);
        Assert.DoesNotContain(repo.RemoteShadow(mount,"scope"),x=>x.Entry.Path=="文件.bin");
        Assert.Contains(repo.RemotePending(mount,"scope"),x=>x.Path=="文件.bin"&&x.Entry is null);
    }
    [Fact]
    public async Task UnsupportedAdvertisementFallsBackAndFailedEnumerationDoesNotDelete()
    {
        await using var fixture=new SyncDavFixture{Advertised=false};await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();
        var engine=new RemoteSyncEngine(mount,"scope",source,source,repo);await engine.PollAsync(false,TimeSpan.Zero,default);
        Assert.StartsWith("完整对账",engine.Mode);var checkpoint=repo.RemoteCollections(mount,"scope").Single(x=>x.Path=="");
        var inner=fixture.Dav.Server.Handler;fixture.Dav.Server.Handler=request=>request.Method=="PROPFIND"&&request.Headers.GetValueOrDefault("Depth")=="1"?Task.FromResult(new HttpTestServer.Response(403,[])):inner(request);
        await Assert.ThrowsAnyAsync<IOException>(()=>engine.PollAsync(true,TimeSpan.Zero,default));
        Assert.Equal(checkpoint,repo.RemoteCollections(mount,"scope").Single(x=>x.Path==""));Assert.Contains(repo.RemoteShadow(mount,"scope"),x=>x.Entry.Path=="文件.bin");
        Assert.DoesNotContain(repo.RemotePending(mount,"scope"),x=>x.Entry is null);
        Assert.DoesNotContain(fixture.Dav.Server.Requests,x=>x.Method=="REPORT");
    }
    [Fact]
    public async Task ExplicitWeakChangeGetsNewObservationAndFreshWholeSnapshot()
    {
        await using var fixture=new SyncDavFixture();fixture.Dav.WeakEtag=true;await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();
        var engine=new RemoteSyncEngine(mount,"scope",source,source,repo);await engine.PollAsync(false,TimeSpan.Zero,default);
        var old=repo.RemoteShadow(mount,"scope").Single(x=>x.Entry.Path=="文件.bin").Entry;var first=new byte[3];await source.ReadRangeAsync(old,0,first);
        var original=fixture.Dav.Objects["文件.bin"];fixture.Dav.Objects["文件.bin"]=original with{Bytes=Enumerable.Repeat((byte)99,original.Bytes.Length).ToArray()};fixture.Sequence++;
        await engine.PollAsync(false,TimeSpan.Zero,default);var changed=repo.RemoteShadow(mount,"scope").Single(x=>x.Entry.Path=="文件.bin").Entry;
        Assert.False(source.IsSameVersion(old.Version,changed.Version));var second=new byte[3];await source.ReadRangeAsync(changed,0,second);Assert.Equal(new byte[]{99,99,99},second);Assert.NotEqual(first,second);
    }
    [Fact]
    public async Task CursorAndPendingAreScopedAndReplayableAcrossRepositoryRestart()
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();
        await new RemoteSyncEngine(mount,"account-A",source,source,repo).PollAsync(false,TimeSpan.Zero,default);
        var restart=new MountRepository(repo.DataDirectory,fixture.Registry);Assert.NotEmpty(restart.RemotePending(mount,"account-A"));Assert.NotEmpty(restart.RemoteCollections(mount,"account-A"));
        Assert.Empty(restart.RemotePending(mount,"account-B"));Assert.Empty(restart.RemoteCollections(mount,"account-B"));
        var first=restart.RemotePending(mount,"account-A").First();Assert.True(restart.HasPendingRemote(mount,first.Path));restart.SaveRemotePending(first with{State="Completed"});
        Assert.False(restart.HasPendingRemote(mount,first.Path));
    }
    [Fact]
    public async Task EmptyUnchangedCycleCreatesNoAdditionalPendingOrOperations()
    {
        await using var fixture=new SyncDavFixture();fixture.Dav.Objects.Clear();fixture.Dav.Objects[""]=new([],"\"root\"",true);
        await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();var engine=new RemoteSyncEngine(mount,"scope",source,source,repo);
        await engine.PollAsync(false,TimeSpan.Zero,default);await engine.PollAsync(false,TimeSpan.Zero,default);
        Assert.Empty(repo.RemotePending(mount,"scope"));Assert.Empty(repo.LoadOperations());
    }
    [Theory]
    [InlineData("strong")][InlineData("weak")][InlineData("bare")][InlineData("missing")]
    public async Task FiveReconnectsKeepCanceledIntentAndCompletedObservationWithoutNewTasks(string shape)
    {
        await using var fixture=new SyncDavFixture();
        if(shape=="weak")fixture.Dav.WeakEtag=true;
        if(shape is "bare" or "missing")foreach(var pair in fixture.Dav.Objects.ToArray())fixture.Dav.Objects[pair.Key]=pair.Value with{ETag=shape=="bare"?pair.Value.ETag.Trim('"'):""};
        await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();
        await new RemoteSyncEngine(mount,"repeat",source,source,repo).PollAsync(true,TimeSpan.Zero,default);
        var original=repo.RemotePending(mount,"repeat").ToArray();Assert.NotEmpty(original);
        foreach(var item in original)
        {
            if(item.Entry!.IsDirectory){repo.SaveEntry(mount,new("local-"+item.Id,item.Path,item.Path,item.Entry.Version,true,Materialized:true));repo.SaveRemotePending(item with{State="Completed"});}
            else repo.SaveRemotePending(item with{State="Canceled",Error="explicit cancellation"});
        }
        var expected=repo.RemotePending(mount,"repeat").ToArray();
        for(var i=0;i<5;i++)await new RemoteSyncEngine(mount,"repeat",source,source,repo).PollAsync(true,TimeSpan.Zero,default);
        Assert.Equal(expected,repo.RemotePending(mount,"repeat"));
        Assert.DoesNotContain(fixture.Dav.Server.Requests,x=>x.Method is "GET" or "PUT" or "DELETE");
    }
    [Fact]
    public async Task MissingLocalPublicationIsRepairedRatherThanHiddenByUnchangedShadow()
    {
        await using var fixture=new SyncDavFixture();fixture.Dav.WeakEtag=true;await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();
        await new RemoteSyncEngine(mount,"repair",source,source,repo).PollAsync(true,TimeSpan.Zero,default);
        foreach(var item in repo.RemotePending(mount,"repair"))repo.SaveRemotePending(item with{State="Completed"});
        var count=repo.RemotePending(mount,"repair").Count;
        await new RemoteSyncEngine(mount,"repair",source,source,repo).PollAsync(true,TimeSpan.Zero,default);
        Assert.True(repo.RemotePending(mount,"repair").Count>count);Assert.Contains(repo.RemotePending(mount,"repair"),x=>x.State=="Pending");
    }
    [Fact(Timeout=120000)]
    public async Task Legacy861UnchangedEventsDrainWithoutBodyReadOrIdentityChange()
    {
        await using var fixture=new DavFixture();fixture.XmlTag=item=>item.ETag.Trim('"');
        for(var i=0;i<1773;i++)fixture.Put("bulk-"+i.ToString("D4")+".bin",[(byte)(i%251)]);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);
        var repo=new MountRepository(Path.Combine(fixture.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(fixture.Area,"logs.db"));
        await using var manager=new MountManager(repo,logger);var mount=await manager.AddAsync("legacy backlog",local,true,source:fixture.Options,credentials:new("user","app-password"));
        try
        {
            await manager.SetRemoteDetectionAsync(mount.Id,false,60,300);var all=repo.AllRemotePending(mount.Id);Assert.Equal(1775,all.Count);
            var backlog=all.Where(x=>x.Entry is {IsDirectory:false}).Take(861).ToArray();Assert.Equal(861,backlog.Length);
            var first=repo.Entries(mount.Id).First(x=>!x.IsDirectory);var path=RelativePaths.ToLocal(local,first.LocalPath);var identity=LocalFileState.Read(path).Identity;
            foreach(var item in backlog)repo.SaveRemotePending(item with{State="Pending"});
            var started=System.Diagnostics.Stopwatch.GetTimestamp();await manager.DetectRemoteForTestsAsync(mount.Id);
            var seconds=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            Assert.All(repo.AllRemotePending(mount.Id),item=>Assert.Equal("Completed",item.State));Assert.Equal(1775,repo.AllRemotePending(mount.Id).Count);
            Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.DoesNotContain(fixture.Server.Requests,x=>x.Method is "GET" or "PUT" or "DELETE");
            Assert.True(seconds<=30,$"861-event drain took {seconds:F3}s");
        }
        finally
        {
            await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }
    [Fact]
    public async Task FactoryRestoresWeakObservationBeforeItsFirstMetadataRead()
    {
        await using var fixture=new SyncDavFixture();fixture.Dav.WeakEtag=true;var repository=fixture.Repository();var local=Path.Combine(fixture.Dav.Area,"unused-view");Directory.CreateDirectory(local);
        var mount=new MountDefinition(Guid.NewGuid(),"恢复观测",local,Source:fixture.Dav.Options);new CredentialStore(repository.DataDirectory).Save(mount.Id,new("user","app-password"));
        var factory=new FileSourceFactory();SourceEntry changed;
        await using(var first=await factory.CreateAsync(mount,repository,false,default))
        {
            var dav=Assert.IsType<WebDavFileSource>(first);var scope=dav.RemoteScope(0);var engine=new RemoteSyncEngine(mount.Id,scope,dav,dav,repository);
            await engine.PollAsync(false,TimeSpan.Zero,default);
            var item=fixture.Dav.Objects["文件.bin"];fixture.Dav.Objects["文件.bin"]=item with{Bytes=Enumerable.Repeat((byte)91,item.Bytes.Length).ToArray()};fixture.Sequence++;
            await engine.PollAsync(false,TimeSpan.Zero,default);changed=repository.RemoteShadow(mount.Id,scope).Single(x=>x.Entry.Path=="文件.bin").Entry;
        }
        await using(var resumed=await factory.CreateAsync(mount,repository,false,default))
        {
            var metadata=await resumed.GetMetadataAsync("文件.bin");Assert.True(resumed.IsSameVersion(changed.Version,metadata.Version));
            var bytes=new byte[3];await resumed.ReadRangeAsync(metadata,0,bytes);Assert.Equal(new byte[]{91,91,91},bytes);
        }
        await using var revised=await factory.CreateAsync(mount with{CredentialRevision=1},repository,false,default);
        Assert.False(revised.IsSameVersion(changed.Version,(await revised.GetMetadataAsync("文件.bin")).Version));
    }
    [Fact]
    public async Task ExplicitUnsupportedReportFallsBackButAuthenticationDoesNot()
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();var repo=fixture.Repository();var mount=Guid.NewGuid();
        fixture.Report=(_,_)=>HttpTestServer.Response.Text(403,"<d:error xmlns:d=\"DAV:\"><d:supported-report/></d:error>",new(){["Content-Type"]="application/xml"});
        var engine=new RemoteSyncEngine(mount,"unsupported",source,source,repo);await engine.PollAsync(false,TimeSpan.Zero,default);Assert.StartsWith("完整对账",engine.Mode);
        fixture.Report=(_,_)=>new(401,[]);var error=await Assert.ThrowsAsync<RemoteSyncException>(()=>new RemoteSyncEngine(mount,"auth",source,source,repo).PollAsync(false,TimeSpan.Zero,default));
        Assert.True(error.AuthenticationFailure);Assert.Empty(repo.RemoteCollections(mount,"auth"));Assert.Empty(repo.RemoteShadow(mount,"auth"));
    }
    [Fact]
    public void HmxbkPreservesRemoteDetectionSettings()
    {
        var item=new MountBackupItem("DAV","old",new(FileSourceKind.WebDav,"https://example.test/dav/"),new("user","password"),RemoteAutoDetectEnabled:false,RemoteIncrementalSeconds:90,RemoteFullSeconds:600);
        var bytes=MountBackupCodec.Encode(new(1,DateTimeOffset.UtcNow,[item]),"password-long");
        Assert.Equal(item,MountBackupCodec.Decode(bytes,"password-long").Mounts.Single());
    }
}

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
