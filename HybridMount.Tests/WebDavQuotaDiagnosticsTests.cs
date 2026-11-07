using System.Text;
using System.Xml.Linq;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>现有 DAV 客户端上的配额拒绝与只读能力快照；任何场景都不能发送 PATCH。</summary>
public sealed class WebDavQuotaDiagnosticsTests
{
    [Theory]
    [InlineData("<d:error xmlns:d='DAV:'><d:quota-not-exceeded/></d:error>","账户配额不足")]
    [InlineData("<d:error xmlns:d='DAV:'><d:sufficient-disk-space/></d:error>","服务器磁盘不足")]
    [InlineData("<d:error xmlns:d='DAV:'><d:other/></d:error>","存储不足")]
    [InlineData("<d:error xmlns:d='foreign:'><d:quota-not-exceeded/></d:error>","存储不足")]
    [InlineData("<broken","存储不足")][InlineData("","存储不足")]
    public async Task Definite507RejectsWithoutUnknownResultOrAutomaticResend(string body,string reason)
    {
        await using var fixture=new DavFixture();var original=fixture.Server.Handler;var notices=0;
        fixture.Server.Handler=request=>request.Method=="PUT"&&request.Target=="/dav/rejected.bin"?
            Task.FromResult(HttpTestServer.Response.Text(507,body,new(){["Content-Type"]="application/xml"})):original(request);
        await using var source=fixture.Source();((IMountSourceRuntime)source).CapacityInvalidated+=()=>notices++;
        var error=await Assert.ThrowsAsync<SourceRequestRejectedException>(()=>source.UploadAsync("rejected.bin",new MemoryStream([1,2]),null));
        Assert.Equal(507,error.HttpStatus);Assert.Contains(reason,error.Message);Assert.Contains("显式重试",error.Message);Assert.Equal(1,notices);
        Assert.Single(fixture.Server.Requests,x=>x.Method=="PUT"&&x.Target=="/dav/rejected.bin");Assert.False(fixture.Objects.ContainsKey("rejected.bin"));
        Assert.DoesNotContain(fixture.Server.Requests,x=>x.Method=="PATCH");
    }
    [Theory]
    [InlineData(false,false,false)][InlineData(true,false,false)][InlineData(false,true,false)]
    [InlineData(false,false,true)][InlineData(true,true,true)]
    public async Task OptionsAnnouncementsRemainUnverifiedAndSnapshotReadsNeverSendRequests(bool method,bool format,bool sabre)
    {
        await using var fixture=new DavFixture();var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="OPTIONS"?Task.FromResult(new HttpTestServer.Response(200,[],new()
        { ["DAV"]=sabre?"1, 3, sabredav-partialupdate":"1",["Allow"]=method?"GET, PUT, PATCH":"GET, PUT",["Accept-Patch"]=format?"application/example, application/octet-stream":""})):original(request);
        await using var source=fixture.Source();Assert.Null(source.CapabilitySnapshot);await source.ProbeAsync();var count=fixture.Server.Requests.Count;
        var snapshot=Assert.IsType<WebDavCapabilitySnapshot>(source.CapabilitySnapshot);Assert.Equal(method,snapshot.PatchAdvertised);Assert.Equal(format,snapshot.HasPatchFormats);Assert.Equal(sabre,snapshot.SabrePartialUpdate);
        for(var i=0;i<3;i++){var text=((IMountSourceRuntime)source).ConnectionDiagnostics!;Assert.Contains("实际写入未验证",text);Assert.Contains("条件 PUT",text);}
        Assert.Equal(count,fixture.Server.Requests.Count);Assert.DoesNotContain(fixture.Server.Requests,x=>x.Method=="PATCH");
    }
    [Fact(Timeout=60000)]
    public async Task FailedUploadKeepsSnapshotIssuesAndQuotaInvalidatesUntilExplicitRetry()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var original=test.Dav.Server.Handler;var reject=true;
        test.Dav.Server.Handler=request=>Quota(request)?Task.FromResult(HttpTestServer.Response.Text(207,QuotaXml())):
            reject&&request.Method=="PUT"&&request.Target=="/dav/quota-target.bin"?Task.FromResult(HttpTestServer.Response.Text(507,"<d:error xmlns:d='DAV:'><d:quota-not-exceeded/></d:error>",new(){["Content-Type"]="application/xml"})):original(request);
        await test.Manager.SetCapacityAsync(test.Id,true,null);await test.Manager.RefreshCapacityAsync(test.Id,true);
        var definition=test.Repository.LoadDefinitions().Single();var queries=test.Dav.Server.Requests.Count(Quota);
        await File.WriteAllBytesAsync(Path.Combine(test.Local,"quota-target.bin"),[1,2,3]);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        var failed=await test.Wait("quota-target.bin","Failed");Assert.Contains("账户配额不足",failed.Error);Assert.True(File.Exists(failed.SnapshotPath));Assert.NotNull(failed.Attention);
        Assert.Null(failed.NextAttemptUtc);Assert.Null(failed.Receipt);Assert.Single(test.Manager.UploadIssues,x=>x.Id==failed.Id);
        await test.Manager.RefreshCapacityAsync(test.Id);Assert.True(test.Dav.Server.Requests.Count(Quota)>queries);
        await test.Manager.ScanUploadChangesAsync(test.Id);await Task.Delay(400);
        Assert.Single(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target=="/dav/quota-target.bin");Assert.Equal("Failed",test.Repository.Upload(failed.Id)!.State);
        var before=test.Dav.Server.Requests.Count;var diagnostic=await test.Manager.ReadDiagnosticsAsync(test.Id);Assert.Contains("Accept-Patch",diagnostic);Assert.Equal(before,test.Dav.Server.Requests.Count);
        reject=false;Assert.True(test.Manager.RetryUpload(failed.Id));await test.Wait("quota-target.bin");Assert.Empty(test.Manager.UploadIssues);
        Assert.Equal(definition,test.Repository.LoadDefinitions().Single());Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="PATCH");
    }
    [Fact]
    public async Task InvalidationDoesNotDeadlockHeldLeaseAndDoesNotReuseOtherMountsCachedCapacity()
    {
        using var coordinator=new CapacityCoordinator();var calls=0;Task<RemoteCapacity> Query(CancellationToken _){calls++;return Task.FromResult(new RemoteCapacity("scope",100,1));}
        using(var lease=await coordinator.EnterAsync("scope",default))
        {await lease.ReadAsync("mount-a",Query,false,default);await lease.ReadAsync("mount-b",Query,false,default);coordinator.Invalidate("scope");Assert.Null(lease.Snapshot("mount-a"));}
        using(var lease=await coordinator.EnterAsync("scope",default)){await lease.ReadAsync("mount-a",Query,false,default);await lease.ReadAsync("mount-b",Query,false,default);}
        Assert.Equal(4,calls);
    }
    [Fact]
    public async Task LateCapacityQueryCannotPublishOverInvalidation()
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();var release=new TaskCompletionSource<RemoteCapacity>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);RemoteCapacitySnapshot? snapshot=null;
        await using var session=new RemoteCapacitySession(source,value=>snapshot=value,default,query:(_,_)=>{entered.TrySetResult();return release.Task;});
        var query=session.RefreshAsync();await entered.Task;session.Invalidate();release.SetResult(new("scope",100,1));await query;
        Assert.Contains("重新核实",snapshot!.Error);Assert.Null(snapshot.LastSuccessUtc);Assert.Null(snapshot.LastAttemptUtc);
    }
    private static bool Quota(HttpTestServer.Request request)=>request.Method=="PROPFIND"&&Encoding.UTF8.GetString(request.Body).Contains("quota-",StringComparison.Ordinal);
    private static string QuotaXml()
    {
        XNamespace d="DAV:";return new XElement(d+"multistatus",new XElement(d+"response",new XElement(d+"href","/dav/"),new XElement(d+"propstat",
            new XElement(d+"prop",new XElement(d+"quota-available-bytes",1073741824),new XElement(d+"quota-used-bytes",65536)),new XElement(d+"status","HTTP/1.1 200 OK")))).ToString();
    }
}
