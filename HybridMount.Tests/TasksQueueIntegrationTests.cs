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
    public async Task UnrelatedDownloadContinuesDuringPutAndPageExitDoesNotStopEitherWorkflow()
    {
        await using var test=await UploadFlowTests.Fixture.Create(legacyPreviews:false);
        var handler=test.Dav.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Dav.Server.Handler=async request=>{if(request.Method=="PUT"&&request.Target.Contains("parallel-upload.bin",StringComparison.Ordinal)){entered.TrySetResult();await release.Task;}return await handler(request);};
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(test.Local,"parallel-upload.bin"),[4,5,6]);await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var upload=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="parallel-upload.bin");
            var reading=Task.Run(()=>File.ReadAllBytes(Path.Combine(test.Local,"文件.bin")));
            Assert.Equal(test.Dav.Bytes,await reading.WaitAsync(TimeSpan.FromSeconds(15)));Assert.False(release.Task.IsCompleted);
            using(var vm=new TasksViewModel(test.Manager.TasksQueue,new InlineDispatcher()))
            {await vm.SnapshotIdle;vm.SelectedJobId=upload.Id;Assert.Equal(upload.Id,vm.Selected!.Id);}
            release.TrySetResult();await test.Wait("parallel-upload.bin");
            using var reopened=new TasksViewModel(test.Manager.TasksQueue,new InlineDispatcher());await reopened.SnapshotIdle;
            Assert.Contains(reopened.Operations,x=>x.Id==upload.Id&&x.State=="Completed");
        }
        finally{release.TrySetResult();}
    }
}
