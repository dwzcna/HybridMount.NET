using System.Collections.Concurrent;
using System.Net;
using HybridMount.Common;
using HybridMount.Models.Mounting;
using HybridMount.Services;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>确定性交错验证串行、优先级、正文所有权和退出，不使用正式挂载或外部服务。</summary>
public sealed class TasksQueueTests
{
    private static MountingTask TaskFor(MountQueueBinding binding,string id,MountingPriority priority=MountingPriority.Transaction,DateTimeOffset? time=null)
        =>new(id,binding.MountId,binding.Generation,priority,time??DateTimeOffset.UtcNow,"test");
    private sealed class Step(Func<CancellationToken,Task<bool>> run):IMountingTaskStep {public Task<bool> ExecuteStepAsync(CancellationToken token)=>run(token);}
    [Fact(Timeout=15000)]
    public async Task SameMountSerialDifferentMountsParallelAndGlobalLimitTwo()
    {
        await using var queue=new TasksQueueService();var a=queue.StartMount(Guid.NewGuid());var b=queue.StartMount(Guid.NewGuid());var c=queue.StartMount(Guid.NewGuid());
        using var first=await queue.AcquireAsync(a,TaskFor(a,"first"),default);
        var same=queue.AcquireAsync(a,TaskFor(a,"same",MountingPriority.Demand),default);
        using var second=await queue.AcquireAsync(b,TaskFor(b,"second"),default);
        var third=queue.AcquireAsync(c,TaskFor(c,"third"),default);
        Assert.False(same.IsCompleted);Assert.False(third.IsCompleted);Assert.Equal(2,queue.QueueSnapshots.Count(x=>x.WaitingReason is null));
        first.Dispose();using var ready=await same;Assert.False(third.IsCompleted);
        second.Dispose();using var last=await third;
    }
    [Fact(Timeout=15000)]
    public async Task PriorityThenSubmissionTimeThenStableSequenceWithoutAging()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());
        using var held=await queue.AcquireAsync(binding,TaskFor(binding,"held"),default);
        var time=DateTimeOffset.UtcNow;
        var oldLow=queue.AcquireAsync(binding,TaskFor(binding,"old-low",MountingPriority.Maintenance,time.AddYears(-1)),default);
        var late=queue.AcquireAsync(binding,TaskFor(binding,"late",MountingPriority.User,time.AddSeconds(1)),default);
        var firstTie=queue.AcquireAsync(binding,TaskFor(binding,"first-tie",MountingPriority.User,time),default);
        var secondTie=queue.AcquireAsync(binding,TaskFor(binding,"second-tie",MountingPriority.User,time),default);
        held.Dispose();using(var one=await firstTie){Assert.False(secondTie.IsCompleted);Assert.False(late.IsCompleted);}
        using(var two=await secondTie)Assert.False(late.IsCompleted);
        using(var three=await late)Assert.False(oldLow.IsCompleted);
        using var four=await oldLow;
    }
    [Fact(Timeout=15000)]
    public async Task QueuedCancellationDoesNotReleaseActiveTransfer()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());using var cancel=new CancellationTokenSource();
        using var held=await queue.AcquireAsync(binding,TaskFor(binding,"held"),default);
        var canceled=queue.AcquireAsync(binding,TaskFor(binding,"cancel"),cancel.Token);cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>canceled);
        var after=queue.AcquireAsync(binding,TaskFor(binding,"after"),default);Assert.False(after.IsCompleted);
        held.Dispose();using var final=await after;Assert.DoesNotContain(queue.QueueSnapshots,x=>x.Task.Id=="cancel");
    }
    [Fact(Timeout=15000)]
    public async Task BackpressureAndDisposeCancelWaitingButAwaitActualBodyOwner()
    {
        var queue=new TasksQueueService(1,1);var binding=queue.StartMount(Guid.NewGuid());
        using var held=await queue.AcquireAsync(binding,TaskFor(binding,"held"),default);
        var pending=queue.AcquireAsync(binding,TaskFor(binding,"pending"),default);
        var backpressure=queue.AcquireAsync(binding,TaskFor(binding,"backpressure"),default);
        Assert.Equal(2,queue.QueueSnapshots.Count);var stopping=queue.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>backpressure);
        Assert.False(stopping.IsCompleted);held.Dispose();await stopping;
    }
    [Fact(Timeout=15000)]
    public async Task FullBackgroundBacklogCannotConsumeDemandAdmission()
    {
        await using var queue=new TasksQueueService(1,1);var binding=queue.StartMount(Guid.NewGuid());using var cancel=new CancellationTokenSource();
        using var held=await queue.AcquireAsync(binding,TaskFor(binding,"held"),default);
        var low=queue.AcquireAsync(binding,TaskFor(binding,"low",MountingPriority.Maintenance),default);
        var overflow=queue.AcquireAsync(binding,TaskFor(binding,"overflow",MountingPriority.Maintenance),cancel.Token);
        var high=queue.AcquireAsync(binding,TaskFor(binding,"high",MountingPriority.Demand),default);
        held.Dispose();using(var demand=await high)Assert.False(low.IsCompleted);
        using var background=await low;cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>overflow);
    }
    [Fact(Timeout=15000)]
    public async Task RetireRejectsOldGenerationAndWaitsForRealTransferCompletion()
    {
        await using var queue=new TasksQueueService();var binding=queue.StartMount(Guid.NewGuid());
        using var held=await queue.AcquireAsync(binding,TaskFor(binding,"held"),default);
        var pending=queue.AcquireAsync(binding,TaskFor(binding,"pending"),default);var retiring=queue.RetireMountAsync(binding);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.False(retiring.IsCompleted);
        Assert.Throws<InvalidOperationException>(()=>queue.StartMount(binding.MountId));held.Dispose();await retiring;
        var current=queue.StartMount(binding.MountId);Assert.True(current.Generation>binding.Generation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>queue.AcquireAsync(binding,TaskFor(binding,"late"),default));
        using var valid=await queue.AcquireAsync(current,TaskFor(current,"valid"),default);
    }
    [Fact(Timeout=15000)]
    public async Task NecessaryDependencyInheritsPriorityWhileUnrelatedWorkContinues()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());var order=new List<string>();
        using var held=await queue.AcquireAsync(binding,TaskFor(binding,"held"),default);
        var high=queue.RunAsync(TaskFor(binding,"high",MountingPriority.Demand) with{Dependencies=["dependency"]},new Step(_=>{order.Add("high");return Task.FromResult(true);}));
        var middle=queue.RunAsync(TaskFor(binding,"middle",MountingPriority.User),new Step(_=>{order.Add("middle");return Task.FromResult(true);}));
        var dependency=queue.RunAsync(TaskFor(binding,"dependency",MountingPriority.Maintenance),new Step(_=>{order.Add("dependency");return Task.FromResult(true);}));
        Assert.Equal(MountingPriority.Demand,queue.QueueSnapshots.Single(x=>x.Task.Id=="dependency").EffectivePriority);
        held.Dispose();await Task.WhenAll(high,middle,dependency);Assert.Equal(new[]{"dependency","high","middle"},order);
        using var cancel=new CancellationTokenSource();var blocked=queue.RunAsync(TaskFor(binding,"blocked") with{Dependencies=["user-conflict"]},new Step(_=>throw new Exception("不可运行")),cancel.Token);
        var unrelated=queue.RunAsync(TaskFor(binding,"unrelated"),new Step(_=>Task.FromResult(true)));await unrelated;
        Assert.Equal("user-conflict",queue.QueueSnapshots.Single().BlockingTaskId);cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>blocked);
    }
    [Fact(Timeout=15000)]
    public async Task SafeStepYieldsToHigherPriorityWithoutCancelingLowTask()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());var order=new List<string>();var count=0;Task? high=null;
        var low=queue.RunAsync(TaskFor(binding,"low",MountingPriority.Thumbnail),new Step(token=>
        {
            Assert.False(token.IsCancellationRequested);order.Add("low"+(++count));
            if(count==1){high=queue.RunAsync(TaskFor(binding,"high",MountingPriority.Demand),new Step(_=>{order.Add("high");return Task.FromResult(true);}));return Task.FromResult(false);}
            return Task.FromResult(true);
        }));
        await low;await high!;Assert.Equal(new[]{"low1","high","low2"},order);
    }
    [Fact(Timeout=15000)]
    public async Task DependencyCycleRejectedAndFailureDoesNotHoldNetworkSlot()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());using var cancel=new CancellationTokenSource();
        var one=queue.RunAsync(TaskFor(binding,"a") with{Dependencies=["b"]},new Step(_=>Task.FromResult(true)),cancel.Token);
        await Assert.ThrowsAsync<ArgumentException>(()=>queue.RunAsync(TaskFor(binding,"b") with{Dependencies=["a"]},new Step(_=>Task.FromResult(true))));
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>one);
        await Assert.ThrowsAsync<IOException>(()=>queue.RunAsync(TaskFor(binding,"failure"),new Step(_=>throw new IOException("真实失败"))));
        using var final=await queue.AcquireAsync(binding,TaskFor(binding,"after"),default);
    }
    [Fact(Timeout=15000)]
    public async Task ObserverOwnsNoStorageSlotAndServiceStopsIt()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observing=queue.RunObserverAsync(new MountingObserver("observer",binding.MountId,async token=>{entered.SetResult();await Task.Delay(Timeout.Infinite,token);}),default);
        await entered.Task;using var network=await queue.AcquireAsync(binding,TaskFor(binding,"network"),default);
        queue.RequestStop();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>observing);Assert.Single(queue.QueueSnapshots);
    }
    [Fact(Timeout=15000)]
    public async Task SharedFileDependencyInheritsDemandButOtherVersionDoesNot()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());
        using var held=await queue.AcquireAsync(binding,TaskFor(binding,"held"),default);
        var sharedTask=TaskFor(binding,"shared",MountingPriority.Thumbnail) with{Path="file.jpg",EntryId="entry",Version="v1"};
        var shared=queue.AcquireAsync(binding,sharedTask,default);
        using(var scope=queue.EnterTask(binding.MountId,"demand",MountingPriority.Demand,"download","file.jpg","entry","v1"))
            Assert.Equal(MountingPriority.Demand,queue.QueueSnapshots.Single(x=>x.Task.Id=="shared").EffectivePriority);
        using(var scope=queue.EnterTask(binding.MountId,"new-version",MountingPriority.Demand,"download","file.jpg","entry","v2"))
            Assert.Equal(MountingPriority.Thumbnail,queue.QueueSnapshots.Single(x=>x.Task.Id=="shared").EffectivePriority);
        held.Dispose();using var final=await shared;
    }
    [Fact(Timeout=15000)]
    public async Task ServiceCancellationUsesOriginalOwnerAndDoesNotCancelUnrelatedWork()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task=queue.RunAsync(TaskFor(binding,"cancel-me"),new Step(async token=>{entered.SetResult();await Task.Delay(Timeout.Infinite,token);return true;}));
        await entered.Task;Assert.True(queue.Cancel("cancel-me"));await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);
        using var next=await queue.AcquireAsync(binding,TaskFor(binding,"unrelated"),default);Assert.False(queue.Cancel("cancel-me"));
    }
    [Fact(Timeout=15000)]
    public async Task ReadBufferGateUsesStrictPriorityAtEachBlockBoundary()
    {
        var buffers=new PriorityReadQueue(1);using var held=await buffers.EnterStrictAsync(MountingPriority.Thumbnail,default);
        var low=buffers.EnterStrictAsync(MountingPriority.User,default);var high=buffers.EnterStrictAsync(MountingPriority.Demand,default);
        held.Dispose();using(var demand=await high)Assert.False(low.IsCompleted);using var user=await low;
    }
    [Fact(Timeout=15000)]
    public async Task SharedCapacityAcrossMountsInheritsPriorityAndAdmitsUserBeforeMaintenance()
    {
        await using var queue=new TasksQueueService(1);using var capacities=new CapacityCoordinator();
        var a=queue.StartMount(Guid.NewGuid());var b=queue.StartMount(Guid.NewGuid());var c=queue.StartMount(Guid.NewGuid());
        using var ownerScope=queue.EnterTask(a.MountId,"capacity-a",MountingPriority.Maintenance,"capacity");
        using var owner=await capacities.EnterAsync("account",default);Task<CapacityCoordinator.Lease> low,high;
        using(var scope=queue.EnterTask(b.MountId,"capacity-b",MountingPriority.Maintenance,"capacity"))low=capacities.EnterAsync("account",default);
        using(var scope=queue.EnterTask(c.MountId,"upload-c",MountingPriority.User,"upload"))high=capacities.EnterAsync("account",default);
        using(var network=await queue.AcquireAsync(a,TaskFor(a,"capacity-a",MountingPriority.Maintenance),default))
            Assert.Equal(MountingPriority.User,queue.QueueSnapshots.Single(x=>x.Task.Id=="capacity-a").EffectivePriority);
        owner.Dispose();using(var user=await high)Assert.False(low.IsCompleted);using var maintenance=await low;
    }
    private sealed class Handler(MountQueueBinding binding):DelegatingHandler(new SocketsHttpHandler())
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>MountingHttpScheduler.SendAsync(binding,MountingPriority.Observation,cancel=>base.SendAsync(request,cancel),token);}
    [Theory(Timeout=15000)]
    [InlineData(false)][InlineData(true)]
    public async Task HeadersNeverReleaseBodySlotAndEarlyCloseOrEofReleasesIt(bool earlyClose)
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());await using var server=new HttpTestServer();
        var headers=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Handler=_=>Task.FromResult(new HttpTestServer.Response(200,[1,2,3],BeforeBody:release.Task,HeadersSent:headers));
        using var client=new HttpClient(new Handler(binding));using var response=await client.GetAsync(server.Address,HttpCompletionOption.ResponseHeadersRead);await headers.Task;
        var after=queue.AcquireAsync(binding,TaskFor(binding,"after",MountingPriority.Demand),default);Assert.False(after.IsCompleted);
        if(earlyClose)response.Dispose();else{release.TrySetResult();Assert.Equal(new byte[]{1,2,3},await response.Content.ReadAsByteArrayAsync());}
        using var next=await after;release.TrySetResult();
    }
    [Fact(Timeout=15000)]
    public async Task BodyReadCancellationClosesBodyAndReleasesSlot()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());await using var server=new HttpTestServer();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Handler=_=>Task.FromResult(new HttpTestServer.Response(200,[1],BeforeBody:release.Task));
        using var client=new HttpClient(new Handler(binding));using var response=await client.GetAsync(server.Address,HttpCompletionOption.ResponseHeadersRead);
        await using var stream=await response.Content.ReadAsStreamAsync();using var cancel=new CancellationTokenSource();var reading=stream.ReadAsync(new byte[1],cancel.Token).AsTask();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reading);using var next=await queue.AcquireAsync(binding,TaskFor(binding,"after"),default);release.TrySetResult();
    }
    [Fact(Timeout=15000)]
    public async Task RegisteredExecutorUsesSameSerialTransferAndKeepsUnawaitedChildOwnership()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());await using var server=new HttpTestServer();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Handler=_=>Task.FromResult(new HttpTestServer.Response(200,[1,2],BeforeBody:release.Task));using var client=new HttpClient(new Handler(binding));HttpResponseMessage? response=null;
        var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution=queue.RunAsync(TaskFor(binding,"executor"),new Step(async token=>{response=await client.GetAsync(server.Address,HttpCompletionOption.ResponseHeadersRead,token);received.SetResult();return true;}));
        await received.Task;Assert.False(execution.IsCompleted);
        var after=queue.AcquireAsync(binding,TaskFor(binding,"after"),default);Assert.False(after.IsCompleted);
        response!.Dispose();await execution;using var next=await after;release.TrySetResult();
    }
    [Theory(Timeout=30000)]
    [InlineData(false)][InlineData(true)]
    public async Task MountedProtocolRangesYieldBetweenBlocksAndDoNotMixBytes(bool s3)
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());await using var dav=new DavFixture();await using var aws=new S3Fixture();
        var bytes=Enumerable.Range(0,3*262144+17).Select(i=>(byte)(i*23)).ToArray();dav.Put("large.bin",bytes);aws.Put("remote/large.bin",bytes);
        await using IFileSource source=s3?aws.Source():dav.Source();await source.ProbeAsync();var entry=await source.GetMetadataAsync("large.bin");((IMountNetworkSource)source).BindQueue(binding);
        using var scope=queue.EnterTask(binding.MountId,"background",MountingPriority.Transaction,"remoteUpdate","large.bin");using var output=new MemoryStream();var blocks=0;
        await source.ReadRangeSequentialAsync(entry,0,entry.Size,async(block,token)=>
        {
            await output.WriteAsync(block,token);
            if(++blocks==1)
            {
                // 正文消费结束后才调用sink，此时高优先级读取可取得同挂载执行位。
                await Task.Run(async()=>{using var demand=queue.EnterTask(binding.MountId,"foreground",MountingPriority.Demand,"download");await source.GetMetadataAsync("large.bin",token);});
            }
        });
        Assert.Equal(bytes,output.ToArray());Assert.Equal(4,blocks);scope.Dispose();Assert.Empty(queue.QueueSnapshots);
    }
    [Fact(Timeout=15000)]
    public async Task S3InactivityTimerStartsAfterQueueWait()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());await using var fixture=new S3Fixture();await using var source=fixture.Source();
        await source.ProbeAsync();source.InactivityTimeout=TimeSpan.FromMilliseconds(200);((IMountNetworkSource)source).BindQueue(binding);
        using var held=await queue.AcquireAsync(binding,TaskFor(binding,"write"),default);var metadata=source.GetMetadataAsync("文件.bin");await Task.Delay(350);
        Assert.False(metadata.IsCompleted);held.Dispose();Assert.Equal("文件.bin",(await metadata).Path);
    }
}
