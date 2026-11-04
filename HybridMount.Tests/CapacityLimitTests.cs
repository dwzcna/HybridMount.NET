using System.Globalization;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>容量逻辑、完整只读用量及共享范围租约的受控回归；不连接用户服务器。</summary>
public sealed class CapacityLimitTests
{
    [Fact]
    public void Defaults_and_exact_byte_arithmetic()
    {
        var definition=new MountDefinition(Guid.NewGuid(),"容量",@"D:\capacity");Assert.False(CapacityRules.Enabled(definition));
        Assert.Null(CapacityRules.ParseMaximum("","GB"));
        Assert.Equal(1_000_000_000L,CapacityRules.ParseMaximum("1","GB"));Assert.Equal(1_000_000_000_000L,CapacityRules.ParseMaximum("1","TB"));
        Assert.Equal(0,CapacityRules.Growth(9,8));Assert.Equal(1,CapacityRules.Growth(8,9));
        Assert.Equal(100,new RemoteCapacity("手动",null,null,100).TotalBytes);
        Assert.Throws<ArgumentException>(()=>CapacityRules.Validate(FileSourceKind.S3,true,null));
        Assert.Throws<ArgumentException>(()=>CapacityRules.Validate(FileSourceKind.WebDav,false,0));
    }
    [Theory]
    [InlineData("0")][InlineData("-1")][InlineData("1e3")][InlineData("NaN")][InlineData("9223372036854775807")]
    public void Invalid_maximum_does_not_become_unlimited(string text)=>Assert.Throws<ArgumentException>(()=>CapacityRules.ParseMaximum(text,"TB"));
    [Fact]
    public async Task Dav_manual_usage_reads_every_directory_without_body_or_quota()
    {
        await using var fixture=new DavFixture();fixture.Objects["中文"]=new([],"\"dir2\"",true);fixture.Put("中文/a.bin",[1,2,3]);fixture.Put("zero",[]);
        await using var source=fixture.Source();var value=await source.GetRemoteUsageAsync();
        Assert.True(value.IsComplete);Assert.Equal(fixture.Bytes.Length+3,value.UsedBytes);
        Assert.All(fixture.Server.Requests,request=>{Assert.Equal("PROPFIND",request.Method);Assert.Equal("1",request.Headers["Depth"]);Assert.DoesNotContain("quota-",System.Text.Encoding.UTF8.GetString(request.Body));});
    }
    [Fact]
    public async Task Dav_failed_child_does_not_publish_partial_sum()
    {
        await using var fixture=new DavFixture();var handler=fixture.Server.Handler;fixture.Server.Handler=request=>request.Target.Contains("%E7%A9%BA",StringComparison.OrdinalIgnoreCase)?Task.FromResult(new HttpTestServer.Response(403,[])):handler(request);
        await using var source=fixture.Source();await Assert.ThrowsAnyAsync<IOException>(()=>source.GetRemoteUsageAsync());
    }
    [Fact]
    public async Task S3_whole_bucket_includes_outside_prefix_and_only_lists()
    {
        await using var fixture=new S3Fixture();fixture.Put("outside/文件",[8,7,6,5]);fixture.Put("other/",[1]);
        await using var source=fixture.Source();var value=await source.GetRemoteUsageAsync();
        Assert.Equal(fixture.Objects.Values.Sum(x=>(long)x.Bytes.Length),value.UsedBytes);Assert.True(value.IsComplete);
        Assert.True(fixture.Server.Requests.Count>=3);Assert.All(fixture.Server.Requests,request=>
        {Assert.Equal("GET",request.Method);Assert.Contains("list-type=2",request.Target);Assert.DoesNotContain("prefix=remote",request.Target);Assert.DoesNotContain("delimiter=",request.Target);});
    }
    [Fact]
    public async Task S3_repeated_pagination_cannot_be_used_as_capacity()
    {
        await using var fixture=new S3Fixture();fixture.RepeatToken=true;await using var source=fixture.Source();
        await Assert.ThrowsAsync<IOException>(()=>source.GetRemoteUsageAsync());
    }
    [Fact]
    public async Task Scope_serializes_writers_and_keeps_original_cache_expiry()
    {
        var now=DateTimeOffset.UtcNow;using var owner=new CapacityCoordinator(()=>now);var calls=0;
        Task<RemoteCapacity> Query(CancellationToken token){calls++;return Task.FromResult(new RemoteCapacity("手动",90,10,100));}
        using(var first=await owner.EnterAsync("bucket",default))
        {
            Assert.Equal(90,(await first.ReadAsync("same",Query,false,default)).AvailableBytes);
            var next=owner.EnterAsync("bucket",default);Assert.False(next.IsCompleted);first.Committed("same",20);first.Dispose();
            using var second=await next;Assert.Equal(70,(await second.ReadAsync("same",Query,false,default)).AvailableBytes);Assert.Equal(1,calls);
            now+=TimeSpan.FromMinutes(6);await second.ReadAsync("same",Query,false,default);Assert.Equal(2,calls);
        }
    }
    [Fact]
    public async Task Failed_refresh_invalidates_cached_permission_and_rate_limits_retry()
    {
        var now=DateTimeOffset.UtcNow;using var owner=new CapacityCoordinator(()=>now);using var lease=await owner.EnterAsync("dav",default);var attempts=0;
        Task<RemoteCapacity> Fails(CancellationToken token){attempts++;throw new IOException("secret-response");}
        await lease.ReadAsync("a",_=>Task.FromResult(new RemoteCapacity("容量",10,20)),false,default);
        await Assert.ThrowsAsync<IOException>(()=>lease.ReadAsync("a",Fails,true,default));
        await Assert.ThrowsAsync<IOException>(()=>lease.ReadAsync("a",Fails,false,default));Assert.Equal(1,attempts);
        now+=TimeSpan.FromMinutes(1);await Assert.ThrowsAsync<IOException>(()=>lease.ReadAsync("a",Fails,false,default));Assert.Equal(2,attempts);
    }
}
