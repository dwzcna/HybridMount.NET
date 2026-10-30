using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Xml.Linq;
using HybridMount.Services.Mounting;
using HybridMount.Models.Mounting;

namespace HybridMount.Tests;

/// <summary>验证上传的条件保护、锁排他和结果核对，不借助真实账号。</summary>
public sealed class UploadProtocolTests
{
    [Fact]
    public async Task DavUploadDoesNotRequireConditionalDeleteOrMove()
    {
        await using var fixture=new DavLockFixture();fixture.IgnoreDeleteMove=true;
        await using var source=fixture.Dav.Source();
        var entry=await source.UploadAsync("upload.bin",new MemoryStream([1,2,3]),null);
        Assert.Equal(3,entry.Size);Assert.Equal(CapabilitySupport.Supported,source.WriteCapabilities.CreateOnly);
        Assert.DoesNotContain(fixture.Dav.Server.Requests,r=>r.Method=="MOVE");
        Assert.DoesNotContain(fixture.Dav.Server.Requests,r=>r.Method=="DELETE"&&r.Headers.ContainsKey("If-Match"));
    }
    [Fact]
    public async Task DavBareTagsUseVerifiedLockAndOriginalDigest()
    {
        await using var fixture=new DavLockFixture();fixture.RawTags=true;fixture.BareLockHeader=true;
        await using var source=fixture.Dav.Source();var before=await source.GetMetadataAsync("文件.bin");
        SourceContentBaseline? baseline=null;source.ContentBaseline+=value=>baseline=value;
        var buffer=new byte[5];await source.ReadRangeAsync(before,0,buffer);
        Assert.NotNull(baseline);Assert.Equal(Convert.ToHexString(SHA256.HashData(fixture.Dav.Bytes)),baseline.Sha256);
        var uploaded=await source.UploadWithPreconditionAsync("文件.bin",new MemoryStream([7,8,9]),new(before.Version,baseline.Sha256));
        Assert.Equal(3,uploaded.Size);Assert.Equal(new byte[]{7,8,9},fixture.Dav.Objects["文件.bin"].Bytes);
        Assert.Equal(CapabilitySupport.Supported,source.WriteCapabilities.ExclusiveLock);Assert.Empty(fixture.Locks);
        Assert.Contains(fixture.Dav.Server.Requests,r=>r.Method=="PUT"&&r.Target.Contains("%E6%96%87%E4%BB%B6")&&r.Headers.ContainsKey("If"));
    }
    [Fact]
    public async Task DavNoReliableBaselineOrChangedRemoteNeverOverwrites()
    {
        await using var fixture=new DavLockFixture();fixture.RawTags=true;
        await using var source=fixture.Dav.Source();var before=await source.GetMetadataAsync("文件.bin");
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("文件.bin",new MemoryStream([99]),before.Version));
        fixture.Dav.Put("文件.bin",[4,5]);
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadWithPreconditionAsync("文件.bin",new MemoryStream([99]),
            new(before.Version,Convert.ToHexString(SHA256.HashData(fixture.Dav.Bytes)))));
        Assert.Equal(new byte[]{4,5},fixture.Dav.Objects["文件.bin"].Bytes);Assert.Empty(fixture.Locks);
    }
    [Fact]
    public async Task DavIgnoredConditionsCanCreateOnlyThroughVerifiedLockCreation()
    {
        await using var fixture=new DavLockFixture();fixture.Dav.IgnoreConditions=true;fixture.RawTags=true;
        await using var source=fixture.Dav.Source();
        await source.UploadAsync("new.bin",new MemoryStream([1,2]),null);
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("new.bin",new MemoryStream([9]),null));
        Assert.Equal(new byte[]{1,2},fixture.Dav.Objects["new.bin"].Bytes);Assert.Empty(fixture.Locks);
    }
    [Fact]
    public async Task DavUnenforcedLocksDoNotAllowUserWrites()
    {
        await using var fixture=new DavLockFixture();fixture.RawTags=true;fixture.IgnoreExclusive=true;
        await using var source=fixture.Dav.Source();var before=await source.GetMetadataAsync("文件.bin");
        await Assert.ThrowsAsync<NotSupportedException>(()=>source.UploadWithPreconditionAsync("文件.bin",new MemoryStream([9]),
            new(before.Version,Convert.ToHexString(SHA256.HashData(fixture.Dav.Bytes)))));
        Assert.Equal(fixture.Dav.Bytes,fixture.Dav.Objects["文件.bin"].Bytes);
    }
    [Fact]
    public async Task S3UploadNeedsNeitherDeletePermissionNorCopyPermission()
    {
        await using var fixture=new S3Fixture();var handler=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="DELETE"||request.Headers.ContainsKey("x-amz-copy-source")
            ?Task.FromResult(HttpTestServer.Response.Text(403,"<Error><Code>AccessDenied</Code></Error>")):handler(request);
        await using var source=fixture.Source();var warnings=new List<string>();source.WriteWarning+=warnings.Add;
        await source.UploadAsync("new.bin",new MemoryStream([3,4]),null);
        Assert.Equal(new byte[]{3,4},fixture.Objects["remote/new.bin"].Bytes);Assert.NotEmpty(warnings);
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Headers.ContainsKey("x-amz-copy-source"));
    }
    [Fact]
    public async Task S3PutOnlyVersionIdDoesNotOverrideReadableETagBaseline()
    {
        await using var fixture=new S3Fixture();var handler=fixture.Server.Handler;
        fixture.Server.Handler=async request=>{var response=await handler(request);if(request.Method=="HEAD")response.Headers?.Remove("x-amz-version-id");return response;};
        await using var source=fixture.Source();var entry=await source.UploadAsync("new.bin",new MemoryStream([3,4]),null);
        Assert.Contains("\"VersionId\":null",entry.Version);Assert.Equal(2,entry.Size);
        var updated=await source.UploadAsync("new.bin",new MemoryStream([5,6,7]),entry.Version);Assert.Equal(3,updated.Size);
        Assert.DoesNotContain(fixture.Server.Requests,x=>x.Target.Contains("versionId=",StringComparison.Ordinal));
    }
    [Fact]
    public async Task S3DifferentVersionAfterSuccessfulPutIsNotMarkedSynced()
    {
        await using var fixture=new S3Fixture();var handler=fixture.Server.Handler;
        fixture.Server.Handler=async request=>
        {
            var response=await handler(request);
            if(request.Method=="PUT"&&request.Target.EndsWith("/new.bin",StringComparison.Ordinal))fixture.Put("remote/new.bin",[99]);
            return response;
        };
        await using var source=fixture.Source();
        await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.UploadAsync("new.bin",new MemoryStream([3,4]),null));
        Assert.Equal(new byte[]{99},fixture.Objects["remote/new.bin"].Bytes);
    }
}

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
            new XElement(d+"timeout","Second-300"),new XElement(d+"locktoken",new XElement(d+"href",token)))));
        return Task.FromResult(HttpTestServer.Response.Text(status,xml.ToString(),new(){["Lock-Token"]=BareLockHeader?token:"<"+token+">"}));
    }
    public ValueTask DisposeAsync()=>Dav.DisposeAsync();
}
