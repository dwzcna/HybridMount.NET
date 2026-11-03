using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>大 PE 的头部／资源尾部读取与协议版本边界；走实际 HTTP，不执行测试 EXE。</summary>
public sealed class LargeProtocolReadTests
{
    [Theory(Timeout=30000)]
    [InlineData(false,false)][InlineData(true,false)][InlineData(false,true)]
    public async Task LargeDavHeaderAndTailPreserveRangeOrOneWholeSnapshot(bool ignoreRange,bool weak)
    {
        await using var fixture=new DavFixture{IgnoreRange=ignoreRange,WeakEtag=weak};var bytes=PreviewTests.LargeExecutable();fixture.Put("large.exe",bytes);
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("large.exe");var header=new byte[256];var tail=new byte[256];
        await Task.WhenAll(source.ReadRangeAsync(entry,0,header).AsTask(),source.ReadRangeAsync(entry,bytes.Length-256,tail).AsTask());
        Assert.Equal(bytes[..256],header);Assert.Equal(bytes[^256..],tail);
        Assert.Equal(weak?1:2,fixture.Server.Requests.Count(request=>request.Method=="GET"));
        Assert.Equal(ignoreRange||weak?CapabilitySupport.Unsupported:CapabilitySupport.Supported,source.Capabilities.RangeRead);
    }
    [Fact(Timeout=30000)]
    public async Task LargeS3RangesStayBoundToObjectVersionAndDoNotDownloadWholeObject()
    {
        await using var fixture=new S3Fixture();var bytes=PreviewTests.LargeExecutable();fixture.Put("remote/large.exe",bytes);
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("large.exe");var header=new byte[256];var tail=new byte[256];
        fixture.Put("remote/large.exe",[1,2,3]);
        await Task.WhenAll(source.ReadRangeAsync(entry,0,header).AsTask(),source.ReadRangeAsync(entry,bytes.Length-256,tail).AsTask());
        Assert.Equal(bytes[..256],header);Assert.Equal(bytes[^256..],tail);
        var gets=fixture.Server.Requests.Where(request=>request.Method=="GET").ToArray();Assert.Equal(2,gets.Length);
        Assert.All(gets,request=>{Assert.True(request.Headers.ContainsKey("Range"));Assert.Contains("versionId=",request.Target);S3Fixture.VerifySignature(request);});
    }
}
