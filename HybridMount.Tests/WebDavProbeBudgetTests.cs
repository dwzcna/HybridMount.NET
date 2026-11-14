using System.Net;
using HybridMount.Models.Mounting.WebDAV;

namespace HybridMount.Tests;

/// <summary>独立传输夹具验证可选OPTIONS与共享期限，不访问云端、不创建挂载或写入资源。</summary>
public sealed class WebDavProbeBudgetTests
{
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);
    }
    private static HttpResponseMessage Root()=>new((HttpStatusCode)207){Content=new StringContent("""
        <d:multistatus xmlns:d="DAV:"><d:response><d:href>/dav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
        """)};
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(405)] [InlineData(501)] [InlineData(0)]
    public async Task Optional_options_does_not_invalidate_verified_collection(int status)
    {
        var methods=new List<string>();
        var handler=new Handler(async (request,token)=>
        {
            methods.Add(request.Method.Method);
            if(request.Method.Method=="PROPFIND")return Root();
            Assert.Equal("OPTIONS",request.Method.Method);
            if(status==0)await Task.Delay(Timeout.InfiniteTimeSpan,token);
            return new((HttpStatusCode)status);
        });
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),new WholeFileCache("unused"),handler)
            {OptionsDeadline=TimeSpan.FromMilliseconds(30),ReadRetryDelay=TimeSpan.Zero};
        await source.ProbeAsync();Assert.Equal(new[]{"PROPFIND","OPTIONS"},methods);
    }
    [Fact]
    public async Task Total_budget_cannot_be_reset_or_swallowed_by_optional_request()
    {
        var methods=new List<string>();
        var handler=new Handler(async(request,token)=>
        {
            methods.Add(request.Method.Method);
            if(request.Method.Method=="PROPFIND"){await Task.Delay(50,token);return Root();}
            await Task.Delay(Timeout.InfiniteTimeSpan,token);return new(HttpStatusCode.OK);
        });
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),new WholeFileCache("unused"),handler)
            {ProbeDeadline=TimeSpan.FromMilliseconds(100),OptionsDeadline=TimeSpan.FromSeconds(5)};
        Assert.Equal(ConnectionFailureKind.Timeout,(await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync())).Diagnostic.Kind);
        Assert.Equal(new[]{"PROPFIND","OPTIONS"},methods);
    }
    [Theory]
    [InlineData(401)] [InlineData(403)]
    public async Task Required_propfind_denial_is_not_hidden_by_options(int status)
    {
        var methods=new List<string>();
        var handler=new Handler((request,token)=>{methods.Add(request.Method.Method);return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));});
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),new WholeFileCache("unused"),handler);
        await Assert.ThrowsAsync<SourceRequestRejectedException>(()=>source.ProbeAsync());Assert.Equal(new[]{"PROPFIND"},methods);
    }
}
