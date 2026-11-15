using HybridMount.Tests.Fixtures.Protocols;
using HybridMount.Infrastructure.Protocols.WebDAV;
using HybridMount.Models.Mounting;
using System.Security.Cryptography;
namespace HybridMount.Tests.Integration.Protocols;

/// <summary>真实本机HTTP验证表示摘要，不访问生产目录或注册同步根。</summary>
public sealed class WebDavDigestTests
{
    private static string Digest(byte[] bytes,bool sha512=false)=>(sha512?"sha-512":"sha-256")+"=:"+Convert.ToBase64String(sha512?SHA512.HashData(bytes):SHA256.HashData(bytes))+":";
    private static void Enable(DavFixture fixture,bool sha512=false,bool tail=false,bool corrupt=false)
    {
        var next=fixture.Server.Handler;
        fixture.Server.Handler=async request=>
        {
            var path=Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath[5..]).TrimEnd('/');
            fixture.Objects.TryGetValue(path,out var item);
            if(request.Method=="HEAD")return item is null?new(404,[]):new(200,[],new(){["Content-Length"]=item.Bytes.Length.ToString(),["ETag"]="W/"+item.ETag,["Last-Modified"]="Thu, 29 Oct 2026 12:00:00 GMT",["Repr-Digest"]=Digest(item.Bytes,sha512)});
            var response=await next(request);
            if(request.Method!="GET"||response.Status!=200)return response;
            var digest=Digest(corrupt?[99,98]:response.Body,sha512);
            if(tail)return response with{Trailers=new(){["Repr-Digest"]=digest}};
            var headers=new Dictionary<string,string>(response.Headers??[]){["Repr-Digest"]=digest};return response with{Headers=headers};
        };
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Structured_dictionary_supports_algorithms_and_parameters(bool sha512)
    {
        var value=WebDavReprDigest.Parse(["future=:AA==:;p=\"a,b\";v=?1",Digest([1,2,3],sha512)+";ok"],new("https://example.test/dav/a"));
        Assert.NotNull(value);Assert.Equal(sha512?"sha-512":"sha-256",value.Algorithm);
        Assert.True(value.Matches(Convert.ToHexString(SHA256.HashData(new byte[]{1,2,3})),Convert.ToHexString(SHA512.HashData(new byte[]{1,2,3}))));
        Assert.Null(WebDavReprDigest.Parse(["unknown=:AA==:"],new("https://example.test/")));
        Assert.Equal("sha-256",WebDavReprDigest.Parse([Digest([1],true),Digest([1])],new("https://example.test/"))?.Algorithm);
    }
    [Theory]
    [InlineData("sha-256=:AA==:")][InlineData("sha-512=:AA==:")][InlineData("sha-256=\"text\"")]
    [InlineData("future=:AA==:,future=:AA==:")][InlineData("future=:AA==:,")][InlineData("future=:??:")]
    [InlineData("future=:AA==:;a=\"unterminated")][InlineData("future=:AA==:;a;a")]
    public void Malformed_or_duplicate_members_are_rejected(string field)=>Assert.ThrowsAny<IOException>(()=>WebDavReprDigest.Parse([field],new("https://example.test/")));
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Head_detects_same_size_and_time_change_without_authorizing_ranges(bool sha512)
    {
        await using var fixture=new DavFixture{WeakEtag=true};Enable(fixture,sha512);
        await using var source=fixture.Source();var before=await source.GetMetadataAsync("文件.bin");
        fixture.Objects["文件.bin"]=fixture.Objects["文件.bin"] with{Bytes=Enumerable.Repeat((byte)44,fixture.Bytes.Length).ToArray()};
        var after=await source.GetMetadataAsync("文件.bin");Assert.False(source.IsSameVersionHint(before.Version,after.Version));Assert.True(source.HasReliableVersion(after));
        await source.ReadRangeAsync(after,0,new byte[4]);Assert.DoesNotContain(fixture.Server.Requests,request=>request.Headers.ContainsKey("Range")||request.Headers.ContainsKey("If-Match"));
        Assert.All(fixture.Server.Requests.Where(request=>request.Method is "GET" or "HEAD"),request=>{Assert.Contains("sha-256",request.Headers["Want-Repr-Digest"]);Assert.Equal("identity",request.Headers["Accept-Encoding"]);});
    }
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task Full_get_validates_header_and_trailer_before_cache(bool tail,bool corrupt)
    {
        await using var fixture=new DavFixture{WeakEtag=true};Enable(fixture,tail:tail,corrupt:corrupt);
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");var buffer=new byte[4];
        if(corrupt)
        {
            await Assert.ThrowsAnyAsync<IOException>(async()=>await source.ReadRangeAsync(entry,0,buffer));
            await Assert.ThrowsAnyAsync<IOException>(async()=>await source.ReadRangeAsync(entry,0,buffer));
            Assert.Empty(Directory.Exists(Path.Combine(fixture.Area,"cache"))?Directory.EnumerateFiles(Path.Combine(fixture.Area,"cache"),"*.cache"):[]);
            Assert.Equal(2,fixture.Server.Requests.Count(request=>request.Method=="GET"));
        }
        else{await source.ReadRangeAsync(entry,0,buffer);Assert.Equal(fixture.Bytes[..4],buffer);}
    }
    [Fact]
    public async Task Persistent_head_listing_disagreement_has_one_recheck()
    {
        await using var fixture=new DavFixture{WeakEtag=true};Enable(fixture);var next=fixture.Server.Handler;
        fixture.Server.Handler=async request=>
        {
            var response=await next(request);if(request.Method!="HEAD")return response;
            return response with{Headers=new(response.Headers!){["Content-Length"]="2"}};
        };
        await using var source=fixture.Source();await Assert.ThrowsAsync<SourceConflictException>(()=>source.GetMetadataAsync("文件.bin"));
        Assert.Equal(2,fixture.Server.Requests.Count(request=>request.Method=="PROPFIND"));Assert.Equal(2,fixture.Server.Requests.Count(request=>request.Method=="HEAD"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Upload_uses_head_digest_without_full_readback(bool sha512)
    {
        await using var fixture=new DavFixture{WeakEtag=true};Enable(fixture,sha512);await using var source=fixture.Source();
        using var bytes=new MemoryStream([12,34,56]);var uploaded=await source.UploadAsync("new.bin",bytes,null);
        Assert.Equal(3,uploaded.Size);Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="GET"&&request.Target=="/dav/new.bin");
        Assert.Contains(fixture.Server.Requests,request=>request.Method=="HEAD"&&request.Target=="/dav/new.bin");
    }
}
