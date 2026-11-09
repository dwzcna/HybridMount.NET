using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>真实CFAPI和回环HTTP验证统一队列与任务页；仅使用本测试新建的NTFS根。</summary>
[Collection("Native IPC")]
public sealed class TasksQueueIntegrationTests
{
    private sealed class InlineDispatcher:IUiDispatcher {public bool TryEnqueue(Action action){action();return true;}}
    [Fact(Timeout=90000)]
    public async Task DemandReadWaitsForAtomicPutAndPageExitDoesNotStopIt()
    {
        await using var test=await UploadFlowTests.Fixture.Create(legacyPreviews:false);
        var handler=test.Dav.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Dav.Server.Handler=async request=>{if(request.Method=="PUT"&&request.Target.Contains("serial-upload.bin",StringComparison.Ordinal)){entered.TrySetResult();await release.Task;}return await handler(request);};
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(test.Local,"serial-upload.bin"),[4,5,6]);await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var upload=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="serial-upload.bin");
            var before=test.Dav.Server.Requests.Count(x=>x.Method=="GET");
            var reading=Task.Run(()=>File.ReadAllBytes(Path.Combine(test.Local,"文件.bin")));
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while(!test.Manager.TasksQueue.QueueSnapshots.Any(x=>x.Task.Priority==MountingPriority.Demand&&x.WaitingReason is not null)&&DateTime.UtcNow<deadline)await Task.Delay(20);
            var waiting=Assert.Single(test.Manager.TasksQueue.QueueSnapshots,x=>x.Task.Priority==MountingPriority.Demand&&x.WaitingReason is not null);
            Assert.Equal(upload.Id,waiting.BlockingTaskId);Assert.False(reading.IsCompleted);Assert.Equal(before,test.Dav.Server.Requests.Count(x=>x.Method=="GET"));
            using(var vm=new TasksViewModel(test.Manager.TasksQueue,new InlineDispatcher()))
            {
                vm.SelectedJobId=upload.Id;Assert.Contains(vm.Operations,x=>x.Priority==MountingPriority.Demand&&x.WaitingTaskId==upload.Id);Assert.Equal(upload.Id,vm.Selected!.Id);
            }
            release.TrySetResult();Assert.Equal(test.Dav.Bytes,await reading.WaitAsync(TimeSpan.FromSeconds(30)));await test.Wait("serial-upload.bin");
            using var reopened=new TasksViewModel(test.Manager.TasksQueue,new InlineDispatcher());Assert.Contains(reopened.Operations,x=>x.Id==upload.Id&&x.State=="Completed");
        }
        finally{release.TrySetResult();}
    }
}
