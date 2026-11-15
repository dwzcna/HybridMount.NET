using HybridMount.Infrastructure.Protocols.WebDAV;
using HybridMount.Models.Mounting;
using HybridMount.Tests.Fixtures.Protocols;
using HybridMount.Tests.Integration.Mounting;
using System.Xml.Linq;
namespace HybridMount.Tests.Integration.Protocols;

/// <summary>有效权限与内容版本分离；夹具只监听本机随机端口，不接触真实挂载。</summary>
public sealed class WebDavAccessTests
{
    private static readonly XNamespace Dav="DAV:";
    private static XElement Rights(params string[] names)=>new(Dav+"current-user-privilege-set",names.Select(name=>new XElement(Dav+"privilege",new XElement(Dav+name))));
    [Theory]
    [InlineData("all",true,true,true,true)] [InlineData("write",false,true,true,true)]
    [InlineData("read",true,false,false,false)] [InlineData("bind",false,false,true,false)]
    [InlineData("unbind",false,false,false,true)] [InlineData("write-content",false,true,false,false)]
    public void Effective_set_distinguishes_each_privilege(string grant,bool read,bool write,bool bind,bool unbind)
    {
        var value=WebDavFileSource.ParseAccess(Guid.NewGuid(),"folder",Rights(grant));
        Assert.Equal(read?SourceAccessDecision.Allowed:SourceAccessDecision.Denied,value.Read);
        Assert.Equal(write?SourceAccessDecision.Allowed:SourceAccessDecision.Denied,value.WriteContent);
        Assert.Equal(bind?SourceAccessDecision.Allowed:SourceAccessDecision.Denied,value.Bind);
        Assert.Equal(unbind?SourceAccessDecision.Allowed:SourceAccessDecision.Denied,value.Unbind);
    }
    [Fact]
    public void Empty_verified_set_denies_but_missing_restricted_and_malformed_stay_unknown()
    {
        Assert.Equal(SourceAccessDecision.Denied,WebDavFileSource.ParseAccess(Guid.NewGuid(),"",Rights()).Bind);
        foreach(var reason in new[]{"Missing","Permission","Uncertain"})
            Assert.Equal(SourceAccessDecision.Unknown,WebDavFileSource.ParseAccess(Guid.NewGuid(),"",null,reason).WriteContent);
        Assert.Equal(SourceAccessDecision.Unknown,WebDavFileSource.ParseAccess(Guid.NewGuid(),"",Rights("vendor-custom")).WriteContent);
    }
    private static void Grant(DavFixture fixture,Func<string,XElement> rights)
    {
        var next=fixture.Server.Handler;
        fixture.Server.Handler=async request=>
        {
            var response=await next(request);if(request.Method!="PROPFIND"||response.Status!=207)return response;
            var document=XDocument.Parse(System.Text.Encoding.UTF8.GetString(response.Body));
            foreach(var entry in document.Root!.Elements(Dav+"response"))
            {
                var path=Uri.UnescapeDataString(entry.Element(Dav+"href")!.Value[5..]).TrimEnd('/');
                entry.Element(Dav+"propstat")!.Element(Dav+"prop")!.Add(rights(path));
            }
            return HttpTestServer.Response.Text(207,document.ToString());
        };
    }
    [Fact]
    public async Task Denied_update_never_creates_write_probes_or_changes_content_version()
    {
        await using var fixture=new DavFixture();Grant(fixture,_=>Rights("read"));await using var source=fixture.Source();
        var entry=await source.GetMetadataAsync("文件.bin");Assert.Equal(SourceAccessDecision.Denied,source.AccessAt("文件.bin")?.WriteContent);
        var blocked=await Assert.ThrowsAsync<SourcePermissionException>(()=>source.PrepareUploadAsync(entry.Path,new(entry.Version),3));
        Assert.False(blocked.Pending);Assert.Equal(entry.Version,(await source.GetMetadataAsync(entry.Path)).Version);
        Assert.DoesNotContain(fixture.Server.Requests,item=>item.Method is "PUT" or "DELETE" or "MKCOL" or "MOVE" or "LOCK");
    }
    [Fact]
    public async Task Create_checks_parent_and_retry_invalidates_only_in_memory()
    {
        await using var fixture=new DavFixture();var allowed=false;Grant(fixture,_=>allowed?Rights("all"):Rights("read"));await using var source=fixture.Source();
        await Assert.ThrowsAsync<SourcePermissionException>(()=>source.CreateDirectoryAsync("new"));
        Assert.False(fixture.Objects.ContainsKey("new"));var count=fixture.Server.Requests.Count;
        source.InvalidateAccess();Assert.Equal(count,fixture.Server.Requests.Count);allowed=true;
        await source.CreateDirectoryAsync("new");Assert.True(fixture.Objects["new"].Directory);
        Assert.Contains(fixture.Server.Requests,request=>request.Method=="PROPFIND"&&System.Text.Encoding.UTF8.GetString(request.Body).Contains("current-user-privilege-set"));
    }
    [Fact]
    public async Task Delete_uses_parent_unbind_and_never_probes_when_denied()
    {
        await using var fixture=new DavFixture();Grant(fixture,path=>path==""?Rights("read","bind"):Rights("all"));await using var source=fixture.Source();
        var entry=await source.GetMetadataAsync("文件.bin");await Assert.ThrowsAsync<SourcePermissionException>(()=>source.DeleteAsync(entry.Path,entry.Version));
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method is "PUT" or "DELETE");Assert.True(fixture.Objects.ContainsKey(entry.Path));
    }
    [Fact]
    public async Task Move_requires_both_source_unbind_and_destination_bind()
    {
        await using var fixture=new DavFixture();fixture.Objects["a"]=new([],"\"a\"",true);fixture.Objects["b"]=new([],"\"b\"",true);fixture.Put("a/file.bin",[1,2,3]);
        Grant(fixture,path=>path=="b"?Rights("read"):Rights("all"));await using var source=fixture.Source();
        var entry=await source.GetMetadataAsync("a/file.bin");await Assert.ThrowsAsync<SourcePermissionException>(()=>source.MoveAsync(entry.Path,"b/file.bin",entry.Version));
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method is "PUT" or "MOVE" or "DELETE");Assert.True(fixture.Objects.ContainsKey(entry.Path));
    }
    [Fact]
    public async Task Permission_query_denial_is_pending_and_does_not_claim_write_denied()
    {
        await using var fixture=new DavFixture();var next=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="PROPFIND"&&!System.Text.Encoding.UTF8.GetString(request.Body).Contains("resourcetype")?
            Task.FromResult(new HttpTestServer.Response(403,[])):next(request);
        await using var source=fixture.Source();var error=await Assert.ThrowsAsync<SourcePermissionException>(()=>source.PrepareUploadAsync("new.bin",new(null),3));
        Assert.True(error.Pending);Assert.Equal(SourceAccessDecision.Unknown,error.Access.Bind);
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method is "PUT" or "DELETE" or "LOCK");
    }
    [Fact(Timeout=90000)]
    public async Task Task_persists_permission_block_and_snapshot_across_restart_then_retries_same_id()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var allowed=false;
        Grant(test.Dav,_=>allowed?Rights("all"):Rights("read"));
        var path=Path.Combine(test.Local,"permission.txt");await File.WriteAllTextAsync(path,"keep local contents");
        await test.Manager.ScanUploadChangesAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        var blocked=await test.Wait("permission.txt",state:"PermissionBlocked");Assert.NotNull(blocked.SnapshotPath);Assert.True(File.Exists(blocked.SnapshotPath));
        Assert.Equal("keep local contents",await File.ReadAllTextAsync(path));Assert.NotNull(blocked.Access);Assert.NotNull(blocked.Attention);
        Assert.DoesNotContain(test.Dav.Server.Requests,request=>request.Method is "PUT" or "DELETE" or "LOCK");
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);
        Assert.Equal("PermissionBlocked",test.Repository.Upload(blocked.Id)?.State);Assert.True(File.Exists(blocked.SnapshotPath));
        allowed=true;Assert.True(test.Manager.RetryUpload(blocked.Id));var done=await test.Wait("permission.txt");
        Assert.Equal(blocked.Id,done.Id);Assert.Equal("keep local contents",System.Text.Encoding.UTF8.GetString(test.Dav.Objects["permission.txt"].Bytes));
    }
    [Fact]
    public void Permission_states_keep_snapshot_baseline_and_attention_through_manual_retry()
    {
        var item=new LocalUpload("task",Guid.NewGuid(),"a","a",false,"stamp",1,"PermissionBlocked",DateTimeOffset.UtcNow,
            ExpectedVersion:"base",BaselineSha256:"hash",SnapshotPath:"snapshot",SnapshotSha256:"upload",Error:"Denied");
        var normalized=UploadAttentionRules.Normalize(item);Assert.True(UploadRules.CanRetry(normalized));Assert.NotNull(normalized.Attention);
        Assert.True(UploadRules.Unfinished(normalized));Assert.Equal("Queued",UploadRules.RetryState(normalized));Assert.Equal("snapshot",normalized.SnapshotPath);Assert.Equal("hash",normalized.BaselineSha256);
        var pending=normalized with{State="PermissionPending",NextAttemptUtc=DateTimeOffset.UtcNow.AddMinutes(1)};
        Assert.True(UploadRules.AttemptUtc(pending)>DateTimeOffset.UtcNow.AddSeconds(50));
    }
}
