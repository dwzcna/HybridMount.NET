using System.Net;
using HybridMount.Models.Mounting;
using HybridMount.Models.Runtime;
namespace HybridMount.Tests;

/// <summary>纯内存调度及受控响应回归；不访问正式设置、挂载、Explorer或远端服务。</summary>
public sealed class CloudConcurrencyTests
{
    private static MountingTask Work(MountQueueBinding binding,string id,CloudOperationChannel channel,string? path=null,string? version=null)=>
        new(id,binding.MountId,binding.Generation,MountingPriority.User,DateTimeOffset.UtcNow,"test",path,Version:version){Channel=channel,FlowId=Guid.NewGuid()};
    [Theory(Timeout=15000)]
    [InlineData(1)][InlineData(4)][InlineData(128)]
    public async Task GlobalLimitIncludesResponseOwnersAndQueuesTheNextMount(int limit)
    {
        await using var queue=new TasksQueueService(limit);var leases=new List<TasksQueueService.NetworkLease>();
        try
        {
            for(var i=0;i<limit;i++){var binding=queue.StartMount(Guid.NewGuid());leases.Add(await queue.AcquireAsync(binding,Work(binding,"active"+i,CloudOperationChannel.Metadata),default));}
            var nextBinding=queue.StartMount(Guid.NewGuid());var next=queue.AcquireAsync(nextBinding,Work(nextBinding,"next",CloudOperationChannel.Metadata),default);
            Assert.False(next.IsCompleted);Assert.Equal(limit,queue.SchedulingDiagnostics.ActiveRequests);
            leases[0].Dispose();using var last=await next;Assert.Equal(limit,queue.SchedulingDiagnostics.ActiveRequests);
        }
        finally{foreach(var lease in leases)lease.Dispose();}
    }
    [Fact(Timeout=15000)]
    public async Task FourMountChannelsRunTogetherButEachSerializesItsOwnRequests()
    {
        await using var queue=new TasksQueueService(4);var binding=queue.StartMount(Guid.NewGuid());var leases=new List<TasksQueueService.NetworkLease>();
        try
        {
            foreach(var channel in Enum.GetValues<CloudOperationChannel>())leases.Add(await queue.AcquireAsync(binding,Work(binding,channel.ToString(),channel,channel.ToString()),default));
            var waiting=queue.AcquireAsync(binding,Work(binding,"second-download",CloudOperationChannel.Download,"another"),default);Assert.False(waiting.IsCompleted);
            leases.Single(x=>x.Task.Channel==CloudOperationChannel.Download).Dispose();using var second=await waiting;
            Assert.Equal(4,queue.SchedulingDiagnostics.ActiveRequests);
        }
        finally{foreach(var lease in leases)lease.Dispose();}
    }
    [Fact(Timeout=15000)]
    public async Task DynamicIncreaseAdmitsAndDecreaseWaitsWithoutCancelingOwners()
    {
        await using var queue=new TasksQueueService(1);var a=queue.StartMount(Guid.NewGuid());var b=queue.StartMount(Guid.NewGuid());var c=queue.StartMount(Guid.NewGuid());
        using var first=await queue.AcquireAsync(a,Work(a,"a",CloudOperationChannel.Metadata),default);
        var second=queue.AcquireAsync(b,Work(b,"b",CloudOperationChannel.Download),default);Assert.False(second.IsCompleted);
        queue.SetConcurrency(4);using var running=await second;queue.SetConcurrency(1);
        var third=queue.AcquireAsync(c,Work(c,"c",CloudOperationChannel.Upload),default);Assert.False(third.IsCompleted);
        first.Dispose();Assert.False(third.IsCompleted);Assert.False(running.StopToken.IsCancellationRequested);
        running.Dispose();using var final=await third;Assert.Equal(1,queue.Concurrency);
    }
    [Fact(Timeout=15000)]
    public async Task FileWorkflowOwnsNoCloudSlotAndWaitsForItsRealBodyChild()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());var task=Work(binding,"file",CloudOperationChannel.Upload,"a");
        using var flow=await queue.AcquireWorkflowAsync(binding,task,default);Assert.Equal(0,queue.SchedulingDiagnostics.ActiveRequests);
        using(var unrelated=await queue.AcquireAsync(binding,Work(binding,"meta",CloudOperationChannel.Metadata,"b"),default))Assert.Equal(1,queue.SchedulingDiagnostics.ActiveRequests);
        using var body=await queue.AcquireAsync(binding,task,default);flow.Dispose();Assert.False(flow.Finished.Task.IsCompleted);
        var next=queue.AcquireWorkflowAsync(binding,Work(binding,"later",CloudOperationChannel.Upload,"c"),default);Assert.False(next.IsCompleted);
        body.Dispose();await flow.Finished.Task;using var after=await next;
    }
    [Fact(Timeout=15000)]
    public async Task FutureWorkflowBackpressureCannotBlockItsCurrentOwnersRequest()
    {
        await using var queue=new TasksQueueService(1,1);var binding=queue.StartMount(Guid.NewGuid());using var cancel=new CancellationTokenSource();
        var owner=Work(binding,"owner",CloudOperationChannel.Upload,"a");using var flow=await queue.AcquireWorkflowAsync(binding,owner,default);
        var queued=queue.AcquireWorkflowAsync(binding,Work(binding,"queued",CloudOperationChannel.Upload,"b"),cancel.Token);
        var capacity=queue.AcquireWorkflowAsync(binding,Work(binding,"capacity",CloudOperationChannel.Upload,"c"),cancel.Token);
        var foreign=queue.AcquireAsync(binding,Work(binding,"foreign",CloudOperationChannel.Upload,"d"),cancel.Token);
        using var body=await queue.AcquireAsync(binding,owner,default).WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>queued);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>capacity);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>foreign);
    }
    [Fact(Timeout=15000)]
    public async Task SameVersionConsumersShareFileOwnershipAndDifferentVersionsWait()
    {
        await using var queue=new TasksQueueService(4);var binding=queue.StartMount(Guid.NewGuid());
        var original=Work(binding,"owner",CloudOperationChannel.Download,"same","v1");using var first=await queue.AcquireWorkflowAsync(binding,original,default);
        var consumer=Work(binding,"consumer",CloudOperationChannel.Download,"same","v1");using var second=await queue.AcquireWorkflowAsync(binding,consumer,default);
        Assert.Equal(1,queue.SchedulingDiagnostics.ActiveWorkflows);first.Dispose();Assert.False(first.Finished.Task.IsCompleted);
        using var actual=await queue.AcquireAsync(binding,consumer,default);Assert.Equal(1,queue.SchedulingDiagnostics.ActiveRequests);
        var different=queue.AcquireWorkflowAsync(binding,Work(binding,"changed",CloudOperationChannel.Download,"same","v2"),default);Assert.False(different.IsCompleted);
        second.Dispose();Assert.False(different.IsCompleted);actual.Dispose();using var next=await different;
    }
    [Fact(Timeout=15000)]
    public async Task SharedProducerResourceRetainsItsFlowBeforeTheWorkerEvenStarts()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());var task=Work(binding,"owner",CloudOperationChannel.Upload,"a");
        using var flow=await queue.AcquireWorkflowAsync(binding,task,default);using var resource=queue.ShareResource(task,"shared-auth");flow.Dispose();
        var later=queue.AcquireWorkflowAsync(binding,Work(binding,"next",CloudOperationChannel.Upload,"b"),default);Assert.False(later.IsCompleted);
        using(var body=await queue.AcquireAsync(binding,task,default))Assert.False(later.IsCompleted);
        resource.Dispose();using var next=await later;
    }
    [Fact(Timeout=15000)]
    public async Task SubtreeBarrierBlocksDescendantsButAllowsUnrelatedReadyWork()
    {
        await using var queue=new TasksQueueService(4);var binding=queue.StartMount(Guid.NewGuid());
        using var deleting=await queue.AcquireWorkflowAsync(binding,Work(binding,"delete",CloudOperationChannel.Upload,"folder") with{ExclusivePath=true,Subtree=true},default);
        var child=queue.AcquireWorkflowAsync(binding,Work(binding,"child",CloudOperationChannel.Download,"folder/a"),default);Assert.False(child.IsCompleted);
        using var other=await queue.AcquireWorkflowAsync(binding,Work(binding,"other",CloudOperationChannel.Download,"elsewhere/b"),default);
        using var metadata=await queue.AcquireAsync(binding,Work(binding,"meta",CloudOperationChannel.Metadata,"elsewhere"),default);
        deleting.Dispose();Assert.False(child.IsCompleted);other.Dispose();using var ready=await child;
    }
    [Fact(Timeout=15000)]
    public async Task EqualPriorityMountsRotateInsteadOfDrainingOneMountsBacklog()
    {
        await using var queue=new TasksQueueService(1);var a=queue.StartMount(Guid.NewGuid());var b=queue.StartMount(Guid.NewGuid());var c=queue.StartMount(Guid.NewGuid());
        using var held=await queue.AcquireAsync(a,Work(a,"held",CloudOperationChannel.Metadata),default);
        var a1=queue.AcquireAsync(a,Work(a,"a1",CloudOperationChannel.Metadata),default);var a2=queue.AcquireAsync(a,Work(a,"a2",CloudOperationChannel.Metadata),default);
        var b1=queue.AcquireAsync(b,Work(b,"b1",CloudOperationChannel.Metadata),default);var c1=queue.AcquireAsync(c,Work(c,"c1",CloudOperationChannel.Metadata),default);
        held.Dispose();using(var one=await b1){Assert.False(c1.IsCompleted);Assert.False(a1.IsCompleted);}
        using(var two=await c1)Assert.False(a1.IsCompleted);using(var three=await a1)Assert.False(a2.IsCompleted);using var four=await a2;
    }
    [Fact(Timeout=15000)]
    public async Task RetirementCancelsEveryChannelAndWaitsForWorkflowAndBodyOwners()
    {
        await using var queue=new TasksQueueService(4);var binding=queue.StartMount(Guid.NewGuid());var task=Work(binding,"file",CloudOperationChannel.Download,"a","v1");
        using var flow=await queue.AcquireWorkflowAsync(binding,task,default);using var body=await queue.AcquireAsync(binding,task,default);
        using var metadata=await queue.AcquireAsync(binding,Work(binding,"meta",CloudOperationChannel.Metadata),default);
        var queued=queue.AcquireAsync(binding,Work(binding,"next",CloudOperationChannel.Download,"b"),default);
        var retired=queue.RetireMountAsync(binding);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>queued);
        Assert.True(body.StopToken.IsCancellationRequested);Assert.True(metadata.StopToken.IsCancellationRequested);Assert.False(retired.IsCompleted);
        Assert.Throws<OperationCanceledException>(body.Validate);Assert.Throws<InvalidOperationException>(()=>queue.StartMount(binding.MountId));
        body.Dispose();metadata.Dispose();Assert.False(retired.IsCompleted);flow.Dispose();await retired;
        var current=queue.StartMount(binding.MountId);Assert.True(queue.IsCurrent(current));Assert.False(queue.IsCurrent(binding));
    }
    [Fact(Timeout=15000)]
    public async Task MetadataInsideMutationKeepsItsTransactionLaneWithoutNestedGlobalLease()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());
        using var outer=queue.EnterTask(binding.MountId,"write",MountingPriority.User,"upload","a");using var flow=await queue.AcquireCurrentWorkflowAsync(default);
        using var validation=queue.EnterTask(binding.MountId,"write",MountingPriority.User,"capacity");
        Assert.Equal(CloudOperationChannel.Upload,TasksQueueService.CurrentTask!.Channel);
        using var source=MountingHttpScheduler.EnterSourceOperation(binding,CloudOperationChannel.Metadata);
        using var request=await MountingHttpScheduler.AcquireAsync(binding,MountingPriority.Observation,default);Assert.NotNull(request);Assert.Equal(1,queue.SchedulingDiagnostics.ActiveRequests);
        request.Dispose();using var after=await MountingHttpScheduler.AcquireAsync(binding,MountingPriority.Observation,default);Assert.NotNull(after);
    }
    private sealed class WaitingBody:MemoryStream
    {
        internal TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {Entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return 0;}
    }
    [Fact(Timeout=15000)]
    public async Task RetirementInterruptsAnAlreadyWaitingBodyReadWithoutAcceptingLateBytes()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());using var context=queue.EnterTask(binding.MountId,"body",MountingPriority.User,"download","file");
        var body=new WaitingBody();using var response=await MountingHttpScheduler.SendAsync(binding,MountingPriority.User,_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(body)}),default);
        await using var stream=await response.Content.ReadAsStreamAsync();var reading=stream.ReadAsync(new byte[1]).AsTask();await body.Entered.Task;
        var retired=queue.RetireMountAsync(binding);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reading);await retired;
        Assert.Equal(0,queue.SchedulingDiagnostics.ActiveRequests);
    }
    [Fact]
    public void SettingsAreValidatedAndSavedBeforeApplyingTheNewLimit()
    {
        var writes=0;var value=4;var fail=false;var settings=new CloudOperationSettings(()=>null,next=>{writes++;if(fail)throw new IOException("save");value=next;});
        Assert.Equal(4,settings.Current);Assert.Equal(0,writes);Assert.InRange(CloudOperationSettings.Recommendation,1,128);
        Assert.False(settings.TrySave(0));Assert.False(settings.TrySave(129));Assert.Equal(0,writes);
        Assert.True(settings.TrySave(128));Assert.Equal(128,value);Assert.Equal(128,settings.Current);
        fail=true;Assert.False(settings.TrySave(1));Assert.Equal(128,settings.Current);
        var broken=new CloudOperationSettings(()=>throw new IOException("read"),_=>throw new Exception("must not write"));Assert.True(broken.ReadFailed);Assert.Equal(4,broken.Current);
        var invalid=new CloudOperationSettings(()=>"4",_=>throw new Exception("must not write"));Assert.True(invalid.ReadFailed);Assert.Equal(4,invalid.Current);
        Assert.NotEqual(ApplicationPaths.RegistryPathFor(true),ApplicationPaths.RegistryPathFor(false));
    }
}
