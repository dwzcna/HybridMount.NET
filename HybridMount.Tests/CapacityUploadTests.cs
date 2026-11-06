using System.Text;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>真实 NTFS/CFAPI 与受控 DAV/S3 的文件系统触发容量检查；所有根均属于本轮夹具。</summary>
public sealed class CapacityUploadTests
{
    [Fact(Timeout=120000)]
    public async Task Exact_limit_blocks_growth_but_zero_and_other_tasks_continue()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);
        await test.Manager.SetCapacityAsync(test.Id,false,test.Dav.Bytes.Length+4);
        await File.WriteAllBytesAsync(Path.Combine(test.Local,"a-exact.bin"),[1,2,3,4]);await File.WriteAllBytesAsync(Path.Combine(test.Local,"b-denied.bin"),[1]);await File.WriteAllBytesAsync(Path.Combine(test.Local,"c-zero.bin"),[]);
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        await test.Wait("a-exact.bin");var denied=await test.Wait("b-denied.bin","WaitingCapacity");await test.Wait("c-zero.bin");
        Assert.Contains("容量不足",denied.Error);Assert.False(test.Dav.Objects.ContainsKey("b-denied.bin"));
        Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Target.EndsWith("b-denied.bin",StringComparison.Ordinal)&&r.Method is "PUT" or "MKCOL");
        await test.Manager.SetCapacityAsync(test.Id,false,test.Dav.Bytes.Length+5);Assert.True(test.Manager.RetryUpload(denied.Id));await test.Wait("b-denied.bin");
    }
    [Fact(Timeout=120000)]
    public async Task Over_limit_shrink_is_allowed_and_next_growth_waits()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);
        await test.Manager.SetCapacityAsync(test.Id,false,1);await File.WriteAllBytesAsync(path,[1,2,3,4]);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Wait("文件.bin",bytes:[1,2,3,4]);
        await File.WriteAllBytesAsync(path,[1,2,3,4,5]);await test.Wait("文件.bin","WaitingCapacity");Assert.Equal(4,test.Dav.Objects["文件.bin"].Bytes.Length);
        await File.WriteAllBytesAsync(path,[9]);await test.Wait("文件.bin",bytes:[9]);
    }
    [Fact(Timeout=120000)]
    public async Task Unknown_automatic_quota_waits_and_manual_switch_resumes_without_quota_requests()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);Assert.False(test.Manager.Mounts.Single().Definition.CapacityAutoDetectEnabled);
        bool Quota(HttpTestServer.Request request)=>request.Method=="PROPFIND"&&Encoding.UTF8.GetString(request.Body).Contains("quota-",StringComparison.Ordinal);
        Assert.DoesNotContain(test.Dav.Server.Requests,Quota);
        await test.Manager.SetCapacityAsync(test.Id,true,null);await File.WriteAllBytesAsync(Path.Combine(test.Local,"quota.bin"),[1,2]);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        var waiting=await test.Wait("quota.bin","WaitingCapacity");Assert.False(test.Dav.Objects.ContainsKey("quota.bin"));Assert.Contains(test.Dav.Server.Requests,Quota);
        await test.Manager.SetCapacityAsync(test.Id,false,test.Dav.Bytes.Length+2);var count=test.Dav.Server.Requests.Count(Quota);Assert.True(test.Manager.RetryUpload(waiting.Id));await test.Wait("quota.bin");Assert.Equal(count,test.Dav.Server.Requests.Count(Quota));
    }
    [Fact(Timeout=120000)]
    public async Task Canceled_capacity_wait_remains_canceled_across_restart_and_retry_checks_again()
    {
        await using var test=await UploadFlowTests.Fixture.Create();await test.Manager.SetCapacityAsync(test.Id,false,1);
        await File.WriteAllBytesAsync(Path.Combine(test.Local,"cancel.bin"),[1,2]);var waiting=await test.Wait("cancel.bin","WaitingCapacity");Assert.True(test.Manager.Cancel(waiting.Id));
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await Task.Delay(2300);
        Assert.Equal("Canceled",test.Repository.Upload(waiting.Id)!.State);Assert.False(test.Dav.Objects.ContainsKey("cancel.bin"));
        Assert.True(test.Manager.RetryUpload(waiting.Id));await test.Wait("cancel.bin","WaitingCapacity");await test.Manager.SetCapacityAsync(test.Id,false,null);Assert.True(test.Manager.RetryUpload(waiting.Id));await test.Wait("cancel.bin");
    }
    [Fact(Timeout=120000)]
    public async Task Unknown_write_keeps_capacity_reserved_until_readonly_reconciliation()
    {
        await using var test=await UploadFlowTests.Fixture.Create();await test.Manager.SetCapacityAsync(test.Id,false,test.Dav.Bytes.Length+10);
        var handler=test.Dav.Server.Handler;var dropped=false;test.Dav.Server.Handler=async request=>
        {var response=await handler(request);if(!dropped&&request.Method=="PUT"&&request.Target.EndsWith("a-lost.bin",StringComparison.Ordinal)){dropped=true;throw new IOException("response lost");}return response;};
        await File.WriteAllBytesAsync(Path.Combine(test.Local,"a-lost.bin"),[1,2,3]);var unknown=await test.Wait("a-lost.bin","OutcomeUnknown");
        await File.WriteAllBytesAsync(Path.Combine(test.Local,"b-wait.bin"),[4,5,6]);var waiting=await test.Wait("b-wait.bin","WaitingCapacity");Assert.Contains("结果待核对",waiting.Error);
        Assert.True(test.Manager.RetryUpload(unknown.Id));await test.Wait("a-lost.bin");Assert.True(test.Manager.RetryUpload(waiting.Id));await test.Wait("b-wait.bin");
        Assert.Single(test.Dav.Server.Requests,r=>r.Method=="PUT"&&r.Target.EndsWith("a-lost.bin",StringComparison.Ordinal));
    }
    [Fact(Timeout=120000)]
    public async Task Metadata_failure_waits_without_sending_or_losing_snapshot()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetCapacityAsync(test.Id,false,test.Dav.Bytes.Length+10);
        var handler=test.Dav.Server.Handler;test.Dav.Server.Handler=request=>request.Method=="PROPFIND"&&request.Target.EndsWith("failure.bin",StringComparison.Ordinal)?Task.FromResult(new HttpTestServer.Response(403,[])):handler(request);
        await File.WriteAllBytesAsync(Path.Combine(test.Local,"failure.bin"),[1,2]);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var waiting=await test.Wait("failure.bin","WaitingCapacity");
        Assert.True(File.Exists(waiting.SnapshotPath));Assert.False(test.Dav.Objects.ContainsKey("failure.bin"));Assert.Equal("等待容量",waiting.ToSnapshot().DisplayState);
        test.Dav.Server.Handler=handler;Assert.True(test.Manager.RetryUpload(waiting.Id));await test.Wait("failure.bin");
    }
    [Fact(Timeout=120000)]
    public async Task Changed_limit_before_send_is_rechecked_and_does_not_write_target()
    {
        await using var test=await UploadFlowTests.Fixture.Create();await test.Manager.SetCapacityAsync(test.Id,false,test.Dav.Bytes.Length+10);var changed=false;
        test.Manager.UploadCheckpointForTests=async(stage,item)=>{if(stage=="BeforeSend"&&item.LocalPath=="changed.bin"&&!changed){changed=true;await test.Manager.SetCapacityAsync(test.Id,false,1);}};
        await File.WriteAllBytesAsync(Path.Combine(test.Local,"changed.bin"),[1,2]);await test.Wait("changed.bin","WaitingCapacity");Assert.True(changed);Assert.False(test.Dav.Objects.ContainsKey("changed.bin"));
        Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method=="PUT"&&r.Target.EndsWith("changed.bin",StringComparison.Ordinal));test.Manager.UploadCheckpointForTests=null;
    }
    [Fact(Timeout=30000)]
    public async Task Blocked_quota_body_disconnects_within_five_seconds_and_reconnects()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var handler=test.Dav.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var armed=true;
        test.Dav.Server.Handler=async request=>
        {
            var response=await handler(request);
            if(armed&&request.Method=="PROPFIND"&&Encoding.UTF8.GetString(request.Body).Contains("quota-",StringComparison.Ordinal)){entered.TrySetResult();return response with{BeforeBody=release.Task};}
            return response;
        };
        try
        {
            await test.Manager.SetCapacityAsync(test.Id,true,null);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var watch=System.Diagnostics.Stopwatch.StartNew();await test.Manager.DisconnectAsync(test.Id).WaitAsync(TimeSpan.FromSeconds(5));Assert.True(watch.Elapsed<TimeSpan.FromSeconds(5));
            Assert.False(test.Manager.Mounts.Single().IsConnected);Assert.NotNull(MountPathGuard.ReadRoot(test.Local));Assert.DoesNotContain(test.Repository.Uploads(),x=>x.State=="Failed");
            armed=false;release.TrySetResult();await test.Manager.ConnectAsync(test.Id);Assert.True(test.Manager.Mounts.Single().IsConnected);
        }
        finally{armed=false;release.TrySetResult();}
    }
    [Fact(Timeout=120000)]
    public async Task Whole_bucket_limits_two_mounts_with_different_prefixes()
    {
        await using var dav=new S3Fixture();var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");var repository=new MountRepository(Path.Combine(dav.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(dav.Area,"log.db"));
        await using var manager=new MountManager(repository,logger);var first=Path.Combine(dav.Area,"view-a");var second=Path.Combine(dav.Area,"view-b");Directory.CreateDirectory(first);Directory.CreateDirectory(second);
        try
        {
            var a=await manager.AddAsync("容量 A",first,false,source:dav.Options,credentials:S3Fixture.Credentials);var b=await manager.AddAsync("容量 B",second,false,source:dav.Options with{Prefix="second"},credentials:S3Fixture.Credentials);
            var used=dav.Objects.Values.Sum(x=>(long)x.Bytes.Length);await manager.SetCapacityAsync(a.Id,false,used+3);await manager.SetCapacityAsync(b.Id,false,used+3);
            await File.WriteAllBytesAsync(Path.Combine(first,"limit.bin"),[1,2,3]);await File.WriteAllBytesAsync(Path.Combine(second,"limit.bin"),[4,5,6]);
            var deadline=DateTime.UtcNow.AddSeconds(40);
            while(DateTime.UtcNow<deadline)
            {var rows=repository.Uploads().Where(x=>x.LocalPath=="limit.bin").ToArray();if(rows.Length==2&&rows.Count(x=>x.State=="Completed")==1&&rows.Count(x=>x.State=="WaitingCapacity")==1)break;await Task.Delay(100);}
            var records=repository.Uploads().Where(x=>x.LocalPath=="limit.bin").ToArray();Assert.Single(records,x=>x.State=="Completed");Assert.Single(records,x=>x.State=="WaitingCapacity");
            Assert.Equal(used+3,dav.Objects.Values.Sum(x=>(long)x.Bytes.Length));
        }
        finally
        {
            foreach(var mount in manager.Mounts.ToArray())await WebDavTests.Finished(manager,manager.Queue(mount.Definition.Id,"clearAndRemove"));
            await manager.DisposeAsync();Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }
}
