using System.Text;
using System.Xml.Linq;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>只读 HTTP 与隔离索引验证挂载级降级；模拟结果不代表真实服务认证。</summary>
public sealed class RemoteFallbackTests
{
    [Theory]
    [InlineData("xml")][InlineData("token-missing")][InlineData("token-duplicate")][InlineData("token-empty")]
    [InlineData("href-missing")][InlineData("href-duplicate")][InlineData("cross-host")][InlineData("double-slash")][InlineData("query")]
    [InlineData("type-missing")][InlineData("length-missing")][InlineData("duplicate")][InlineData("property-404")]
    public async Task InvalidInitialReportImmediatelyUsesWholeMountFullAndKeepsFallback(string kind)
    {
        await using var fixture=new SyncDavFixture();fixture.Report=(path,token)=>Bad(fixture,kind);
        await using var source=fixture.Dav.Source();var repo=fixture.Repository();var id=Guid.NewGuid();var state=new RemoteChangeSessionState();
        var engine=new RemoteSyncEngine(id,"scope",source,source,repo,state);await engine.PollAsync(false,TimeSpan.Zero,default);
        Assert.Equal(RemoteChangeSessionState.FallbackMode,engine.Mode);Assert.NotNull(state.Fallback);
        Assert.Equal(fixture.Dav.Objects.Count-1,repo.RemoteShadow(id,"scope").Count);Assert.All(repo.RemoteCollections(id,"scope"),x=>Assert.Equal("Full",x.Mode));
        Assert.Single(fixture.Dav.Server.Requests,x=>x.Method=="REPORT");
        var evidence=state.Fallback;var pending=repo.RemotePending(id,"scope").Count;
        await new RemoteSyncEngine(id,"scope",source,source,repo,state).PollAsync(true,TimeSpan.Zero,default);
        Assert.Same(evidence,state.Fallback);Assert.Single(fixture.Dav.Server.Requests,x=>x.Method=="REPORT");Assert.Equal(pending,repo.RemotePending(id,"scope").Count);
        Assert.All(fixture.Dav.Server.Requests,x=>Assert.Contains(x.Method,new[]{"PROPFIND","REPORT"}));
    }
    [Fact]
    public async Task ChildFailureRestartsAtRootAndNewCollectionsNeverReport()
    {
        await using var fixture=new SyncDavFixture();fixture.Dav.Put("空目录/child.bin",[9]);
        fixture.Report=(path,token)=>path.Length==0?fixture.DefaultReport(path,token):Bad(fixture,"xml");
        await using var source=fixture.Dav.Source();var repo=fixture.Repository();var id=Guid.NewGuid();var engine=new RemoteSyncEngine(id,"scope",source,source,repo);
        await engine.PollAsync(false,TimeSpan.Zero,default);Assert.Equal(RemoteChangeSessionState.FallbackMode,engine.Mode);
        var requests=fixture.Dav.Server.Requests.ToArray();var broken=Array.FindLastIndex(requests,x=>x.Method=="REPORT");
        Assert.Equal("/dav/",requests.Skip(broken+1).First(x=>x.Method=="PROPFIND").Target);
        fixture.Dav.Objects["new-directory"]=new([],"\"dir\"",true);fixture.Dav.Put("new-directory/new.bin",[8]);
        await engine.PollAsync(true,TimeSpan.Zero,default);Assert.Equal(2,fixture.Dav.Server.Requests.Count(x=>x.Method=="REPORT"));
        Assert.Contains(repo.RemoteShadow(id,"scope"),x=>x.Entry.Path=="new-directory/new.bin");
    }
    [Theory]
    [InlineData(401)][InlineData(403)][InlineData(404)][InlineData(503)]
    public async Task HttpFailuresNeverDisableRfc6578(int status)
    {
        await using var fixture=new SyncDavFixture();fixture.Report=(_,_)=>HttpTestServer.Response.Text(status,"<broken");
        await using var source=fixture.Dav.Source();var state=new RemoteChangeSessionState();
        await Assert.ThrowsAnyAsync<IOException>(()=>new RemoteSyncEngine(Guid.NewGuid(),"scope",source,source,fixture.Repository(),state).PollAsync(false,TimeSpan.Zero,default));
        Assert.Null(state.Fallback);Assert.DoesNotContain(fixture.Dav.Server.Requests,x=>x.Method=="PROPFIND"&&x.Headers.GetValueOrDefault("Depth")=="1");
    }
    [Fact]
    public async Task FailedFullAfterInvalidIncrementalPreservesTokenAndConfirmedState()
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();var repo=fixture.Repository();var id=Guid.NewGuid();var state=new RemoteChangeSessionState();
        var engine=new RemoteSyncEngine(id,"scope",source,source,repo,state);await engine.PollAsync(false,TimeSpan.Zero,default);
        var old=repo.RemoteCollections(id,"scope").Single(x=>x.Path=="");var pending=repo.RemotePending(id,"scope").ToArray();fixture.Report=(_,_)=>Bad(fixture,"xml");
        var handler=fixture.Dav.Server.Handler;fixture.Dav.Server.Handler=request=>request.Method=="PROPFIND"&&request.Headers.GetValueOrDefault("Depth")=="1"?Task.FromResult(new HttpTestServer.Response(403,[])):handler(request);
        await Assert.ThrowsAnyAsync<IOException>(()=>engine.PollAsync(false,TimeSpan.Zero,default));
        Assert.NotNull(state.Fallback);Assert.Equal(old,repo.RemoteCollections(id,"scope").Single(x=>x.Path==""));Assert.Equal(pending,repo.RemotePending(id,"scope"));
        var reports=fixture.Dav.Server.Requests.Count(x=>x.Method=="REPORT");
        await Assert.ThrowsAnyAsync<IOException>(()=>engine.PollAsync(true,TimeSpan.Zero,default));Assert.Equal(reports,fixture.Dav.Server.Requests.Count(x=>x.Method=="REPORT"));
        fixture.Dav.Server.Handler=handler;await engine.PollAsync(true,TimeSpan.Zero,default);Assert.Equal(RemoteChangeSessionState.FallbackMode,engine.Mode);
        // 独立挂载不继承降级；新会话也不能让已持久 Full 永久阻止探测。
        fixture.Report=null;var emptyTokens=new List<string?>();fixture.Report=(path,token)=>{emptyTokens.Add(token);return fixture.DefaultReport(path,token);};
        await new RemoteSyncEngine(id,"scope",source,source,repo).PollAsync(false,TimeSpan.Zero,default);
        Assert.All(emptyTokens,Assert.Null);Assert.All(repo.RemoteCollections(id,"scope"),x=>Assert.Equal("RFC6578",x.Mode));
        var other=new RemoteSyncEngine(Guid.NewGuid(),"other",source,source,repo);await other.PollAsync(false,TimeSpan.Zero,default);Assert.Contains("增量",other.Mode);
    }
    [Fact]
    public async Task CyclicContinuationDiscardsFailedPageThenFullReconciles()
    {
        await using var fixture=new SyncDavFixture();var calls=0;fixture.Report=(_,_)=>SyncDavFixture.Multi(++calls%2==1?"urn:loop:a":"urn:loop:b",[SyncDavFixture.Resource("/dav/",507)]);
        await using var source=fixture.Dav.Source();var engine=new RemoteSyncEngine(Guid.NewGuid(),"scope",source,source,fixture.Repository());
        await engine.PollAsync(false,TimeSpan.Zero,default);Assert.Equal(3,calls);Assert.Equal(RemoteChangeSessionState.FallbackMode,engine.Mode);
    }
    [Fact(Timeout=60000)]
    public async Task ManagerIntervalAndRefreshKeepFallbackButReopenReconnectAndRestartReprobe()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var sync=new SyncDavFixture(test.Dav);
        sync.Report=(_,_)=>Bad(sync,"xml");await test.Manager.DisconnectAsync(test.Id);await test.Manager.ConnectAsync(test.Id);
        Assert.Equal(RemoteChangeSessionState.FallbackMode,test.Manager.Mounts.Single().Detection!.Mode);
        var reports=test.Dav.Server.Requests.Count(x=>x.Method=="REPORT");
        await test.Manager.SetRemoteDetectionAsync(test.Id,true,79,602);await test.Manager.DetectRemoteForTestsAsync(test.Id);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));Assert.Equal(reports,test.Dav.Server.Requests.Count(x=>x.Method=="REPORT"));
        var diagnostic=await test.Manager.ReadDiagnosticsAsync(test.Id);Assert.Contains("降级时间：",diagnostic);
        await test.Manager.SetRemoteDetectionAsync(test.Id,false,79,602);sync.Report=null;
        await test.Manager.SetRemoteDetectionAsync(test.Id,true,79,602);await test.Manager.DetectRemoteForTestsAsync(test.Id);Assert.Contains("增量",test.Manager.Mounts.Single().Detection!.Mode);
        sync.Report=(_,_)=>Bad(sync,"xml");await test.Manager.DetectRemoteForTestsAsync(test.Id);Assert.Equal(RemoteChangeSessionState.FallbackMode,test.Manager.Mounts.Single().Detection!.Mode);
        sync.Report=null;await test.Manager.DisconnectAsync(test.Id);await test.Manager.ConnectAsync(test.Id);Assert.Contains("增量",test.Manager.Mounts.Single().Detection!.Mode);
        sync.Report=(_,_)=>Bad(sync,"xml");await test.Manager.DetectRemoteForTestsAsync(test.Id);await test.Manager.DisposeAsync();sync.Report=null;
        test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);Assert.Contains("增量",test.Manager.Mounts.Single().Detection!.Mode);
    }
    [Theory(Timeout=10000)]
    [InlineData(false)][InlineData(true)]
    public async Task ReportBodyTimeoutAndCancelDoNotDisableOrCommit(bool callerCancel)
    {
        await using var fixture=new SyncDavFixture();var headers=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:timeout",[]) with{BeforeBody=new TaskCompletionSource().Task,HeadersSent=headers};
        await using var source=fixture.Dav.Source();source.MetadataDeadline=TimeSpan.FromMilliseconds(callerCancel?5000:120);var state=new RemoteChangeSessionState();var repo=fixture.Repository();var id=Guid.NewGuid();
        using var cancel=new CancellationTokenSource();var poll=new RemoteSyncEngine(id,"scope",source,source,repo,state).PollAsync(false,TimeSpan.Zero,cancel.Token);await headers.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if(callerCancel){cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>poll);}else await Assert.ThrowsAnyAsync<IOException>(()=>poll);
        Assert.Null(state.Fallback);Assert.Empty(repo.RemoteCollections(id,"scope"));Assert.Empty(repo.RemotePending(id,"scope"));
    }
    [Fact]
    public async Task FailedFallbackRetriesFromRootDespiteRecentPersistedFullTimestamp()
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();var repo=fixture.Repository();var id=Guid.NewGuid();var state=new RemoteChangeSessionState();
        repo.CommitRemoteBatch(id,"scope",new("",Mode:"Full",LastFullUtc:DateTimeOffset.UtcNow),[],[]);fixture.Report=(_,_)=>Bad(fixture,"xml");
        var handler=fixture.Dav.Server.Handler;var blocked=true;fixture.Dav.Server.Handler=request=>blocked&&request.Method=="PROPFIND"&&request.Headers.GetValueOrDefault("Depth")=="1"?Task.FromResult(new HttpTestServer.Response(403,[])):handler(request);
        var engine=new RemoteSyncEngine(id,"scope",source,source,repo,state);await Assert.ThrowsAnyAsync<IOException>(()=>engine.PollAsync(false,TimeSpan.FromHours(1),default));
        var failure=state.Fallback;Assert.NotNull(failure);
        await Assert.ThrowsAnyAsync<IOException>(()=>new RemoteSyncEngine(id,"scope",source,source,repo,state).PollAsync(false,TimeSpan.FromHours(1),default));
        blocked=false;await engine.PollAsync(false,TimeSpan.FromHours(1),default);Assert.Equal(fixture.Dav.Objects.Count-1,repo.RemoteShadow(id,"scope").Count);
        Assert.Same(failure,state.Fallback);Assert.Single(fixture.Dav.Server.Requests,x=>x.Method=="REPORT");
        var requests=fixture.Dav.Server.Requests.Count;await engine.PollAsync(false,TimeSpan.FromHours(1),default);Assert.Equal(requests,fixture.Dav.Server.Requests.Count);
    }
    private static HttpTestServer.Response Bad(SyncDavFixture fixture,string kind)
    {
        if(kind=="xml")return HttpTestServer.Response.Text(207,"<d:multistatus xmlns:d='DAV:'><broken");
        var item=fixture.Item("文件.bin");var prop=item.Element(SyncDavFixture.D+"propstat")!.Element(SyncDavFixture.D+"prop")!;
        switch(kind)
        {
            case "href-missing":item.Element(SyncDavFixture.D+"href")!.Remove();break;
            case "href-duplicate":item.Add(new XElement(SyncDavFixture.D+"href","/dav/duplicate"));break;
            case "cross-host":item.Element(SyncDavFixture.D+"href")!.Value="https://other.invalid/dav/file";break;
            case "double-slash":item.Element(SyncDavFixture.D+"href")!.Value="//file.bin";break;
            case "query":item.Element(SyncDavFixture.D+"href")!.Value="/dav/file?query=1";break;
            case "type-missing":prop.Element(SyncDavFixture.D+"resourcetype")!.Remove();break;
            case "length-missing":prop.Element(SyncDavFixture.D+"getcontentlength")!.Remove();break;
            case "property-404":item.Element(SyncDavFixture.D+"propstat")!.Element(SyncDavFixture.D+"status")!.Value="HTTP/1.1 404 Not Found";break;
        }
        var root=new XElement(SyncDavFixture.D+"multistatus",item,new XElement(SyncDavFixture.D+"sync-token","urn:bad:1"));
        if(kind=="duplicate")root.Add(new XElement(item));
        if(kind=="token-missing")root.Element(SyncDavFixture.D+"sync-token")!.Remove();
        if(kind=="token-duplicate")root.Add(new XElement(SyncDavFixture.D+"sync-token","urn:bad:2"));
        if(kind=="token-empty")root.Element(SyncDavFixture.D+"sync-token")!.Value="";
        return HttpTestServer.Response.Text(207,root.ToString());
    }
}
