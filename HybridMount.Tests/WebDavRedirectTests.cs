using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using HybridMount.Models.Mounting.WebDAV;

namespace HybridMount.Tests;

/// <summary>只监听本机随机端口，验证重定向中的凭据／正文／条件／URI及整链预算；不访问用户服务器。</summary>
public sealed class WebDavRedirectTests
{
    private static void Redirect(DavFixture fixture,int status=301,bool digest=false)
    {
        var next=fixture.Server.Handler;
        fixture.Server.Handler=async request=>
        {
            var path=new Uri(fixture.Server.Address,request.Target).AbsolutePath;
            if(!path.StartsWith("/dav/canonical/",StringComparison.Ordinal))return new(status,[],new(){["Location"]="/dav/canonical/"+path[5..]});
            var logical="/dav/"+path[15..];var local=Uri.UnescapeDataString(logical[5..]).TrimEnd('/');
            HttpTestServer.Response response;
            if(digest&&request.Method=="HEAD"&&fixture.Objects.TryGetValue(local,out var item))
                response=new(200,[],new(){["Content-Length"]=item.Bytes.Length.ToString(),["ETag"]="W/"+item.ETag,["Last-Modified"]="Thu, 29 Oct 2026 12:00:00 GMT",["Repr-Digest"]="sha-256=:"+Convert.ToBase64String(SHA256.HashData(item.Bytes))+":"});
            else response=await next(request with{Target=logical});
            if(request.Method is "PROPFIND" or "REPORT" or "LOCK"&&response.Body.Length>0)
            {
                try
                {
                    var xml=XDocument.Parse(Encoding.UTF8.GetString(response.Body));XNamespace dav="DAV:";
                    foreach(var href in xml.Descendants(dav+"href"))if(href.Value.StartsWith("/dav/",StringComparison.Ordinal))href.Value="/dav/canonical/"+href.Value[5..];
                    response=response with{Body=Encoding.UTF8.GetBytes(xml.ToString())};
                }
                catch(System.Xml.XmlException){}
            }
            if(digest&&request.Method=="GET"&&response.Status==200)
                response=response with{Headers=new(response.Headers??[]){["Repr-Digest"]="sha-256=:"+Convert.ToBase64String(SHA256.HashData(response.Body))+":"}};
            return response;
        };
    }
    [Theory(Timeout=30000)]
    [InlineData(301)] [InlineData(308)]
    public async Task Read_write_methods_bodies_and_conditions_survive_same_origin_chain(int status)
    {
        await using var fixture=new DavFixture();Redirect(fixture,status);await using var source=fixture.Source();
        await source.ProbeAsync();Assert.Equal(2,(await WebDavTests.All(source.ListAsync(""))).Count);
        var entry=await source.GetMetadataAsync("文件.bin");var buffer=new byte[5];await source.ReadRangeAsync(entry,0,buffer);Assert.Equal(fixture.Bytes[..5],buffer);
        using var content=new MemoryStream([9,8,7,6]);var uploaded=await source.UploadAsync("new.bin",content,null);Assert.Equal("new.bin",uploaded.Path);
        var moved=await source.MoveAsync("new.bin","moved.bin",uploaded.Version);await source.DeleteAsync(moved.Path,moved.Version);Assert.False(fixture.Objects.ContainsKey("moved.bin"));
        await source.CreateDirectoryAsync("created");Assert.True(fixture.Objects["created"].Directory);
        Assert.Contains("/dav/canonical/",source.LastRedirectUri);
        await source.DrainTemporaryCleanupAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var sent=fixture.Server.Requests.ToArray();
        (string Method,string Path,string Body) Key(HttpTestServer.Request request)=>(request.Method,
            request.Target.StartsWith("/dav/canonical/",StringComparison.Ordinal)?"/dav/"+request.Target[15..]:request.Target,Convert.ToHexString(SHA256.HashData(request.Body)));
        var finals=sent.Where(request=>request.Target.StartsWith("/dav/canonical/",StringComparison.Ordinal)).ToLookup(Key);
        foreach(var group in sent.Where(request=>!request.Target.StartsWith("/dav/canonical/",StringComparison.Ordinal)).GroupBy(Key))
        {
            var original=group.ToArray();var followed=finals[group.Key].ToArray();Assert.Equal(original.Length,followed.Length);
            for(var index=0;index<original.Length;index++)foreach(var name in new[]{"Authorization","Range","If-Match","If-None-Match","Overwrite","Destination"})
                Assert.Equal(original[index].Headers.GetValueOrDefault(name),followed[index].Headers.GetValueOrDefault(name));
        }

    }
    [Fact]
    public async Task Digest_binding_and_report_hrefs_use_effective_uri_but_keep_logical_paths()
    {
        await using var fixture=new SyncDavFixture();fixture.Dav.WeakEtag=true;Redirect(fixture.Dav,digest:true);
        var next=fixture.Dav.Server.Handler;
        fixture.Dav.Server.Handler=async request=>
        {
            var response=await next(request);
            if(request.Method is "PROPFIND" or "REPORT"&&response.Status==207)
            {
                using var output=new MemoryStream();using(var gzip=new System.IO.Compression.GZipStream(output,System.IO.Compression.CompressionLevel.Fastest,true))gzip.Write(response.Body);
                return response with{Body=output.ToArray(),Headers=new(){["Content-Encoding"]="gzip"}};
            }
            return response;
        };
        await using var source=fixture.Dav.Source();var entry=await source.GetMetadataAsync("文件.bin");var bytes=new byte[4];await source.ReadRangeAsync(entry,0,bytes);Assert.Equal(fixture.Dav.Bytes[..4],bytes);
        Assert.Contains("canonical",entry.Version);Assert.Equal("文件.bin",entry.Path);
        var batch=await source.GetCollectionChangesAsync("",null,default);Assert.Contains(batch.Entries,item=>item.Path=="文件.bin");Assert.DoesNotContain(batch.Entries,item=>item.Path.StartsWith("canonical/"));
    }
    private sealed class NonSeek(Stream inner):Stream
    {
        private bool _disposed;
        protected override void Dispose(bool disposing){if(disposing)_disposed=true;base.Dispose(disposing);}
        public override bool CanRead=>!_disposed;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count)=>inner.Read(buffer,offset,count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)=>inner.ReadAsync(buffer,token);
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    [Fact]
    public async Task Nonseek_upload_uses_disk_snapshot_without_owning_callers_stream()
    {
        await using var fixture=new DavFixture();Redirect(fixture,308);await using var source=fixture.Source();
        using var memory=new MemoryStream(Enumerable.Range(0,200000).Select(index=>(byte)index).ToArray());using var input=new NonSeek(memory);
        await source.UploadAsync("nonseek.bin",input,null);Assert.Equal(memory.ToArray(),fixture.Objects["nonseek.bin"].Bytes);Assert.True(memory.CanRead);Assert.True(input.CanRead);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Area,"cache","dav-replay")));
    }
    [Theory]
    [InlineData("https://other.test/dav/a")][InlineData("http://example.test/dav/a")][InlineData("/outside/a")]
    [InlineData("/dav/a?q=1")][InlineData("/dav/a#x")][InlineData("https://user:password@example.test/dav/a")]
    [InlineData("/dav/%2e%2e/a")][InlineData("/dav/a%2fb")][InlineData("/dav/%25252fescape")][InlineData("/dav/%00a")]
    public void Policy_rejects_unsafe_locations_before_sending_credentials(string location)
    {
        var root=new Uri("https://example.test/dav/");Assert.Throws<WebDavRedirectException>(()=>WebDavRedirectPolicy.Follow(root,new(root,"a"),[location],new HashSet<string>(),0));
    }
    [Fact]
    public void Normalized_origin_relative_target_loop_hop_limit_and_tagged_conditions()
    {
        var root=new Uri("https://example.test/dav/");var current=new Uri(root,"a");var visited=new HashSet<string>{current.AbsoluteUri};
        var target=WebDavRedirectPolicy.Follow(root,current,["https://EXAMPLE.test:443/dav/b"],visited,4);Assert.Equal("/dav/b",target.AbsolutePath);
        Assert.Throws<WebDavRedirectException>(()=>WebDavRedirectPolicy.Follow(root,current,["b"],visited,0));
        Assert.Throws<WebDavRedirectException>(()=>WebDavRedirectPolicy.Follow(root,current,["c"],visited,5));
        Assert.Equal("<https://example.test/dav/b> (<urn:private-lock>)",WebDavRedirectPolicy.RebindIf("<https://example.test/dav/a> (<urn:private-lock>)",current,target,null));
        Assert.Throws<WebDavRedirectException>(()=>WebDavRedirectPolicy.RebindIf("<https://example.test/dav/unrelated> (<urn:private-lock>)",current,target,null));
    }
    [Fact]
    public async Task Cross_origin_is_not_contacted_and_redirect_loop_is_bounded()
    {
        await using var fixture=new DavFixture();await using var other=new HttpTestServer();fixture.Server.Handler=_=>Task.FromResult(new HttpTestServer.Response(301,[],new(){["Location"]=new Uri(other.Address,"dav/").AbsoluteUri}));
        await using var source=fixture.Source();await Assert.ThrowsAnyAsync<IOException>(()=>source.ProbeAsync());Assert.Empty(other.Requests);
        fixture.Server.Handler=request=>Task.FromResult(new HttpTestServer.Response(308,[],new(){["Location"]=request.Target+"next/"}));
        await Assert.ThrowsAnyAsync<IOException>(()=>source.ProbeAsync());Assert.Equal(7,fixture.Server.Requests.Count);
    }
    [Theory]
    [InlineData(302)] [InlineData(303)] [InlineData(307)]
    public async Task Other_redirect_statuses_are_not_followed(int status)
    {
        await using var fixture=new DavFixture();fixture.Server.Handler=_=>Task.FromResult(new HttpTestServer.Response(status,[],new(){["Location"]="/dav/new/"}));
        await using var source=fixture.Source();await Assert.ThrowsAnyAsync<IOException>(()=>source.ProbeAsync());Assert.Single(fixture.Server.Requests);
    }
    [Fact]
    public async Task Applied_mutation_then_reset_keeps_unknown_outcome_without_replay()
    {
        await using var fixture=new DavFixture();Redirect(fixture);var next=fixture.Server.Handler;
        fixture.Server.Handler=async request=>{var response=await next(request);if(request.Method=="MKCOL"&&request.Target=="/dav/canonical/applied/")throw new IOException("response lost");return response;};
        await using var source=fixture.Source();await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.CreateDirectoryAsync("applied"));
        Assert.True(fixture.Objects.ContainsKey("applied"));Assert.Single(fixture.Server.Requests,request=>request.Method=="MKCOL"&&request.Target=="/dav/canonical/applied/");
    }
    private sealed class BudgetHandler(Uri endpoint):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false})
    {
        internal int RootAttempts,FinalAttempts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.AbsolutePath=="/dav/"&&++RootAttempts>1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently){Headers={Location=new Uri(endpoint,"final/")}});
            if(request.RequestUri.AbsolutePath!="/dav/")FinalAttempts++;
            throw new HttpRequestException(HttpRequestError.ConnectionError,"injected",new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset));
        }
    }
    [Fact]
    public async Task Network_retry_budget_is_not_reset_after_a_redirect()
    {
        var endpoint=new Uri("https://example.test/dav/");var handler=new BudgetHandler(endpoint);
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,endpoint.AbsoluteUri),new(),new WholeFileCache("unused"),handler){ReadRetryDelay=TimeSpan.Zero};
        var error=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync());
        Assert.Equal(3,error.Diagnostic.Attempts);Assert.Equal(2,handler.RootAttempts);Assert.Equal(2,handler.FinalAttempts);
    }
    [Fact(Timeout=10000)]
    public async Task All_hops_share_one_metadata_deadline()
    {
        await using var fixture=new DavFixture();fixture.Server.Handler=async request=>{await Task.Delay(30);return new(308,[],new(){["Location"]=request.Target+"next/"});};
        await using var source=fixture.Source();source.MetadataDeadline=TimeSpan.FromMilliseconds(70);
        var error=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync());Assert.Equal(ConnectionFailureKind.Timeout,error.Diagnostic.Kind);Assert.InRange(fixture.Server.Requests.Count,1,3);
    }
}
