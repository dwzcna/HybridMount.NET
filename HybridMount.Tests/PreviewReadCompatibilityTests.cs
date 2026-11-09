using System.Text.Json.Nodes;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>只读GET候选不能升级写能力；回环验证真实响应版本、预算和正常不可用状态。</summary>
[Collection("Native IPC")]
public sealed class PreviewReadCompatibilityTests
{
    private static SourceEntry Bare(SourceEntry entry){var version=JsonNode.Parse(entry.Version)!;version["ETag"]=version["ETag"]!.GetValue<string>().Trim('"');return entry with{Version=version.ToJsonString()};}
    [Fact]
    public async Task BareMetadataAcceptsOnlyActualMatchingStrongHttpVersionAndCannotAuthorizeDelete()
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();var original=await source.GetMetadataAsync("文件.bin");var entry=Bare(original);var buffer=new byte[64];
        Assert.True((await source.TryReadPreviewRangeAsync(entry,0,buffer)).Supported);Assert.Equal(fixture.Bytes[..64],buffer);Assert.False(source.IsSameVersion(original.Version,entry.Version));
        var get=Assert.Single(fixture.Server.Requests,x=>x.Method=="GET");var originalTag=JsonNode.Parse(original.Version)!["ETag"]!.GetValue<string>();Assert.Equal(originalTag,get.Headers["If-Match"]);Assert.Equal("bytes=0-63",get.Headers["Range"]);
        Assert.DoesNotContain(fixture.Server.Requests,x=>x.Method is "PUT" or "MOVE" or "DELETE");await Assert.ThrowsAsync<NotSupportedException>(()=>source.DeleteAsync(entry.Path,entry.Version));Assert.DoesNotContain(fixture.Server.Requests,x=>Uri.UnescapeDataString(x.Target).EndsWith("/文件.bin",StringComparison.Ordinal)&&x.Method is "PUT" or "MOVE" or "DELETE");
    }
    [Theory][InlineData("bare")][InlineData("weak")][InlineData("missing")][InlineData("different")]
    public async Task ResponseMustIndependentlyProveStrongVersion(string shape)
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();var entry=Bare(await source.GetMetadataAsync("文件.bin"));var original=fixture.Server.Handler;
        fixture.Server.Handler=async request=>{var response=await original(request);if(request.Method!="GET")return response;var headers=new Dictionary<string,string>(response.Headers??[]);var tag=headers["ETag"];if(shape=="missing")headers.Remove("ETag");else headers["ETag"]=shape=="bare"?tag.Trim('"'):shape=="weak"?"W/"+tag:"\"different\"";return response with{Headers=headers};};
        try{Assert.False((await source.TryReadPreviewRangeAsync(entry,0,new byte[64])).Supported);}catch(SourceConflictException){/* 库拒绝畸形验证头或真实版本变化同样不能发布。 */}
        Assert.Single(fixture.Server.Requests,x=>x.Method=="GET");Assert.DoesNotContain(fixture.Server.Requests,x=>x.Method is "PUT" or "MOVE" or "DELETE");
    }
    [Theory][InlineData("W/weak")][InlineData("*")][InlineData("bad value")][InlineData("bad\r\nHeader")]
    public async Task UnsafeCandidateDoesNotSendAnyRequest(string tag)
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");var version=JsonNode.Parse(entry.Version)!;version["ETag"]=tag;entry=entry with{Version=version.ToJsonString()};
        Assert.False((await source.TryReadPreviewRangeAsync(entry,0,new byte[64])).Supported);Assert.DoesNotContain(fixture.Server.Requests,x=>x.Method=="GET");
    }
    [Fact]
    public async Task ExpectedUnsupportedDoesNotThrowOrStartWorkerButTruncatedResponseStillFails()
    {
        var thrown=0;void Observe(object? sender,System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args){if(args.Exception is IOException)Interlocked.Increment(ref thrown);}
        var budget=new PreviewRangeBudget(100,(_,_,_)=>ValueTask.FromResult(new PreviewRangeResult(false,0)));AppDomain.CurrentDomain.FirstChanceException+=Observe;
        try{Assert.Null(await new PreviewWorkerClient(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".missing.exe")).DecodeAsync(".png",100,budget));Assert.Equal(0,thrown);Assert.NotNull(budget.UnavailableReason);}
        finally{AppDomain.CurrentDomain.FirstChanceException-=Observe;}
        var truncated=new PreviewRangeBudget(100,(_,buffer,_)=>ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length-1)));await Assert.ThrowsAsync<IOException>(()=>truncated.TryReadAsync(0,1,default));
    }
    [Fact]
    public async Task BudgetExhaustionIsNormalStatusWithoutExtraRead()
    {
        var reads=0;var budget=new PreviewRangeBudget(16L*1024*1024,(_,buffer,_)=>{reads++;return ValueTask.FromResult(new PreviewRangeResult(true,buffer.Length));});
        for(var i=0;i<32;i++)Assert.NotNull(await budget.TryReadAsync(i*(long)PreviewRangeBudget.WindowBytes,1,default));Assert.Null(await budget.TryReadAsync(32L*PreviewRangeBudget.WindowBytes,1,default));Assert.Equal(32,reads);Assert.Equal(PreviewRangeBudget.MaximumBytes,budget.Bytes);Assert.Contains("预算",budget.UnavailableReason);
    }
}
