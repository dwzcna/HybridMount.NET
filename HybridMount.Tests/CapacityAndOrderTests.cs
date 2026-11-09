using System.Collections.Concurrent;
using System.Xml.Linq;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>只读配额、共享查询取消及任务展示顺序；受控 HTTP 使用产品 DavClient 解析。</summary>
public sealed class CapacityAndOrderTests
{
    [Theory]
    [InlineData("0",0L)][InlineData(" 123 ",123L)][InlineData("-1",null)]
    [InlineData("unlimited",null)][InlineData("9223372036854775808",null)][InlineData("1.2",null)]
    public void Quota_number_is_not_fabricated(string text,long? expected)=>Assert.Equal(expected,RemoteCapacity.ParseBytes(text));
    [Fact]
    public void Unknown_and_overflow_are_independent_of_known_values()
    {
        Assert.Null(new RemoteCapacity("范围",long.MaxValue,1).TotalBytes);
        Assert.Null(new RemoteCapacity("范围",UsedBytes:5).TotalBytes);
        Assert.Equal(0,new RemoteCapacity("范围",0,0).TotalBytes);
        Assert.Equal(12,new RemoteCapacity("范围",7,5).TotalBytes);
    }
    private static string QuotaXml(string? free,string? used,int usedStatus=200,string href="/dav/")
    {
        XNamespace d="DAV:";
        XElement Props(string name,string value,int status)=>new(d+"propstat",new XElement(d+"status",$"HTTP/1.1 {status} Result"),new XElement(d+"prop",new XElement(d+name,value)));
        return new XElement(d+"multistatus",new XElement(d+"response",new XElement(d+"href",href),
            free is null?null:Props("quota-available-bytes",free,200),used is null?null:Props("quota-used-bytes",used,usedStatus))).ToString();
    }
    [Theory]
    [InlineData("1000","2000",200,1000L,2000L,3000L)]
    [InlineData(null,"2000",200,null,2000L,null)]
    [InlineData("1000","",404,1000L,null,null)]
    [InlineData("infinite","-1",200,null,null,null)]
    public async Task Dav_quota_is_depth_zero_and_has_no_writes(string? free,string? used,int status,long? expectedFree,long? expectedUsed,long? total)
    {
        await using var fixture=new DavFixture();fixture.Server.Handler=_=>Task.FromResult(HttpTestServer.Response.Text(207,QuotaXml(free,used,status)));
        await using var source=fixture.Source();var value=await source.GetRemoteCapacityAsync();
        Assert.Equal(expectedFree,value.AvailableBytes);Assert.Equal(expectedUsed,value.UsedBytes);Assert.Equal(total,value.TotalBytes);
        var request=Assert.Single(fixture.Server.Requests);Assert.Equal("PROPFIND",request.Method);Assert.Equal("0",request.Headers["Depth"]);
        var xml=System.Text.Encoding.UTF8.GetString(request.Body);Assert.Contains("quota-available-bytes",xml);Assert.Contains("quota-used-bytes",xml);
        Assert.DoesNotContain("allprop",xml);Assert.Contains("非物理",value.Scope);
    }
    [Fact]
    public async Task Dav_quota_denial_and_cross_root_are_not_success()
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();
        fixture.Server.Handler=_=>Task.FromResult(HttpTestServer.Response.Text(207,QuotaXml("1","2",403)));
        Assert.Equal(403,(await Assert.ThrowsAsync<SourceRequestRejectedException>(()=>source.GetRemoteCapacityAsync())).HttpStatus);
        fixture.Server.Handler=_=>Task.FromResult(HttpTestServer.Response.Text(207,QuotaXml("1","2",href:"/outside/")));
        await Assert.ThrowsAsync<IOException>(()=>source.GetRemoteCapacityAsync());
    }
    [Fact]
    public async Task Dav_quota_body_deadline_and_caller_cancel_are_distinct()
    {
        await using var fixture=new DavFixture();var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Handler=_=>Task.FromResult(new HttpTestServer.Response(207,System.Text.Encoding.UTF8.GetBytes(QuotaXml("1","2")),BeforeBody:blocked.Task));
        await using var source=fixture.Source();source.MetadataDeadline=TimeSpan.FromMilliseconds(150);
        var error=await Assert.ThrowsAnyAsync<IOException>(()=>source.GetRemoteCapacityAsync());Assert.Contains("超时",error.Message);
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>source.GetRemoteCapacityAsync(cancel.Token));
    }
    private sealed class CapacitySource : IFileSource
    {
        public Func<CancellationToken,Task<RemoteCapacity>> Query=token=>Task.FromResult(new RemoteCapacity("范围",1,2));
        public int Queries;
        public FileSourceCapabilities Capabilities=>new(CapabilitySupport.Unknown,CapabilitySupport.Unknown,CapabilitySupport.Unknown,CapabilitySupport.Unknown,CapabilitySupport.Unknown,SourceCaseSensitivity.Unknown);
        public Task<RemoteCapacity> GetRemoteCapacityAsync(CancellationToken token=default){Interlocked.Increment(ref Queries);return Query(token);}
        public IAsyncEnumerable<SourceEntry> ListAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
        public ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> UploadAsync(string path,Stream content,string? version,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> MoveAsync(string path,string destination,string version,CancellationToken token=default)=>throw new NotSupportedException();
        public Task DeleteAsync(string path,string version,CancellationToken token=default)=>throw new NotSupportedException();
    }
    [Fact]
    public async Task Capacity_cache_merges_waiters_and_preserves_success_on_failure()
    {
        var source=new CapacitySource();var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Query=async token=>{entered.TrySetResult();await gate.Task.WaitAsync(token);return new("范围",10,20);};
        RemoteCapacitySnapshot? value=null;var now=DateTimeOffset.UtcNow;
        await using var cache=new RemoteCapacitySession(source,x=>value=x,default,now:()=>now);
        var first=cache.RefreshAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var cancel=new CancellationTokenSource();var second=cache.RefreshAsync(waiter:cancel.Token);cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>second);Assert.False(first.IsCompleted);
        gate.SetResult();await first;Assert.Equal(1,source.Queries);var success=value!.LastSuccessUtc;
        await cache.RefreshAsync();Assert.Equal(1,source.Queries);
        now+=TimeSpan.FromMinutes(6);source.Query=_=>throw new IOException("DO-NOT-LOG-RESPONSE");await cache.RefreshAsync();
        Assert.Equal(2,source.Queries);Assert.Equal(10,value!.Value.AvailableBytes);Assert.Equal(success,value.LastSuccessUtc);
        Assert.NotNull(value.Error);Assert.DoesNotContain("DO-NOT-LOG",value.Error);
    }
    [Fact]
    public async Task Capacity_stop_waits_and_discards_late_result()
    {
        var gate=new TaskCompletionSource<RemoteCapacity>(TaskCreationOptions.RunContinuationsAsynchronously);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source=new CapacitySource{Query=_=>{entered.TrySetResult();return gate.Task;}};var values=new ConcurrentQueue<RemoteCapacitySnapshot>();
        var cache=new RemoteCapacitySession(source,values.Enqueue,default);var work=cache.RefreshAsync();await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cache.RequestStop();var stop=cache.DisposeAsync().AsTask();Assert.False(stop.IsCompleted);
        gate.SetResult(new("迟到",50,50));await stop;await work;Assert.DoesNotContain(values,x=>x.Value.AvailableBytes==50);
        await cache.DisposeAsync();await cache.RefreshAsync(force:true);Assert.Equal(1,source.Queries);
    }
    [Fact]
    public async Task S3_capacity_uses_no_network_or_local_drive_total()
    {
        await using var fixture=new S3Fixture();await using IFileSource source=fixture.Source();
        var result=await source.GetRemoteCapacityAsync();Assert.Equal(RemoteCapacity.Unknown,result);Assert.Empty(fixture.Server.Requests);
    }
    [Fact]
    public void Display_order_is_stable_and_does_not_use_progress()
    {
        MountOperationSnapshot Job(string id,string state,long order,long bytes=0)=>new(id,"upload",state,0,1,[],null,BytesTransferred:bytes,TotalBytes:100){SubmissionOrder=order};
        var jobs=new[]{Job("done","Completed",1),Job("wait","Queued",2),Job("b","Sending",4),Job("a","Sending",3)};
        Assert.Equal(new[]{"a","b","wait","done"},OperationDisplayOrder.Sort(jobs).Select(x=>x.Id));
        jobs[2]=jobs[2] with{BytesTransferred=99};Assert.Equal(new[]{"a","b","wait","done"},OperationDisplayOrder.Sort(jobs).Select(x=>x.Id));
    }
    [Fact]
    public void Active_history_survives_limit_and_keeps_first_submission_order()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.QuotaTests",Guid.NewGuid().ToString("N"));
        try
        {
            var repository=new MountRepository(area,@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));
            var active=new MountOperationSnapshot("active","upload","Sending",0,1,[],null);repository.SaveOperation(active);
            var first=Assert.Single(repository.LoadOperations()).SubmissionOrder;
            for(var i=0;i<150;i++)repository.SaveOperation(new("history-"+i,"refresh","Completed",1,1,[],null));
            repository.SaveOperation(active with{BytesTransferred=50});var rows=repository.LoadOperations();
            Assert.Equal(101,rows.Count);Assert.Equal(first,Assert.Single(rows,x=>x.Id=="active").SubmissionOrder);
            Assert.Equal("active",OperationDisplayOrder.Sort(rows)[0].Id);
        }
        finally{if(Directory.Exists(area))Directory.Delete(area,true);}
    }
}
