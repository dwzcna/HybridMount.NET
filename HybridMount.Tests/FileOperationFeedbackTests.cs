using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using HybridMount.Services;
using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>从任务页入口验证真实CFAPI取消和释放；所有文件、管道及注册均来自测试隔离根。</summary>
public sealed class FileOperationFeedbackTests
{
    private sealed class InlineDispatcher:IUiDispatcher {public bool TryEnqueue(Action action){action();return true;}}
    [DllImport("CldApi.dll",EntryPoint="CfSetPinState")]private static extern int SetNativePin(SafeFileHandle file,uint state,uint flags,nint overlap);
    private static void Pin(string path,uint state)
    {using var file=CloudFilesNative.CreateFile(path,0x180,7,0,3,0x02200000,0);Assert.False(file.IsInvalid);CloudFilesNative.Check(SetNativePin(file,state,0,0));}
    private static async Task Until(Func<bool> condition)
    {for(var i=0;i<150&&!condition();i++)await Task.Delay(40);Assert.True(condition());}
    [Fact]
    public void LocalFileEvidenceDoesNotExtendThePublicIpcSnapshot()
    {
        var local=new MountOperationSnapshot("id","freeSpace","Deferred",0,1,[],null){OperationEntryId="entry",OperationVersion="version",OperationFileId="file",AttemptId="attempt",FailureStage="release",FailureHResult=unchecked((int)0x80070020)};
        var json=System.Text.Json.JsonSerializer.Serialize(local.WithoutLocalEvidence());Assert.DoesNotContain("OperationEntryId",json,StringComparison.Ordinal);Assert.DoesNotContain("AttemptId",json,StringComparison.Ordinal);
        Assert.Contains("OperationEntryId",System.Text.Json.JsonSerializer.Serialize(local),StringComparison.Ordinal);
    }
    [Fact(Timeout=90000)]
    public async Task PageCancelStopsNativePinAndRemovesOnlyItsPinWithoutDehydration()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);
        var path=Path.Combine(test.Local,"文件.bin");var before=LocalFileState.Read(path);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var headers=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler=test.Dav.Server.Handler;test.Dav.Server.Handler=async request=>{var response=await handler(request);return request.Method=="GET"?response with{BeforeBody=release.Task,HeadersSent=headers}:response;};
        try
        {
            Pin(path,1);await headers.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Until(()=>test.Manager.TasksQueue.Operations.Any(row=>row.Id.StartsWith("pin:",StringComparison.Ordinal)&&row.State=="Running"));
            using var vm=new TasksViewModel(test.Manager.TasksQueue,new InlineDispatcher());await vm.SnapshotIdle;
            var row=vm.Operations.First(row=>row.Id.StartsWith("pin:",StringComparison.Ordinal));vm.SelectedJobId=row.Id;
            await vm.CancelSelectedAsync();await Until(()=>test.Manager.GetOperation(row.Id)?.State is "Canceled" or "Failed");
            var result=test.Manager.GetOperation(row.Id)!;Assert.True(result.State=="Canceled",result.Error);
            Assert.Equal(0u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);
            var gets=test.Dav.Server.Requests.Count(request=>request.Method=="GET");await Task.Delay(400);Assert.Equal(gets,test.Dav.Server.Requests.Count(request=>request.Method=="GET"));
            Assert.DoesNotContain(test.Dav.Server.Requests,request=>request.Method=="PUT");
        }
        finally{release.TrySetResult();}
    }
    [Fact(Timeout=90000)]
    public async Task PageCancelOfExplicitKeepAlsoClearsThePinAndFinishedRowsCanBeCleared()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);
        var path=Path.Combine(test.Local,"文件.bin");var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var headers=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler=test.Dav.Server.Handler;test.Dav.Server.Handler=async request=>{var response=await handler(request);return request.Method=="GET"?response with{BeforeBody=release.Task,HeadersSent=headers}:response;};
        try
        {
            var id=test.Manager.Queue(test.Id,"keepOnDevice",[path]);await headers.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var vm=new TasksViewModel(test.Manager.TasksQueue,new InlineDispatcher());await vm.SnapshotIdle;vm.SelectedJobId=id;Assert.True(vm.CanCancelSelected);
            await vm.CancelSelectedAsync();await Until(()=>test.Manager.GetOperation(id)?.State is "Canceled" or "Failed");
            var result=test.Manager.GetOperation(id)!;Assert.True(result.State=="Canceled",result.Error);Assert.Equal(0u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
            await test.Manager.ClearOperationHistoryAsync(TaskHistoryClearMode.All);Assert.DoesNotContain(test.Manager.Operations,row=>row.Id==id);
        }
        finally{release.TrySetResult();}
    }
    [Fact(Timeout=90000)]
    public async Task LockedReleaseRemainsOneDeferredTaskThenCompletesWithoutSourceRead()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Manager.CacheLimitBytes=0;await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);
        var path=Path.Combine(test.Local,"文件.bin");_ = await File.ReadAllBytesAsync(path);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
        var identity=LocalFileState.Read(path).Identity;var requests=test.Dav.Server.Requests.Count;Pin(path,2);string id,attempt;
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            await test.Manager.EnforceCacheLimitAsync();var row=Assert.Single(test.Manager.Operations,row=>row.Operation=="freeSpace"&&row.OperationEntryId is not null);
            Assert.Equal("Deferred",row.State);Assert.NotNull(row.FailureHResult);Assert.Equal(unchecked((int)0x80070020),row.FailureHResult);id=row.Id;attempt=row.AttemptId!;
            await test.Manager.EnforceCacheLimitAsync();Assert.Single(test.Manager.Operations,row=>row.Id==id);Assert.Equal(attempt,test.Manager.GetOperation(id)!.AttemptId);
            Assert.Single(await test.Logger.ReadAsync(),row=>row.Source=="文件任务"&&row.Message.Contains(id,StringComparison.Ordinal));
        }
        await test.Manager.EnforceCacheLimitAsync();Assert.Equal("Completed",test.Manager.GetOperation(id)!.State);Assert.Equal(identity,LocalFileState.Read(path).Identity);
        Assert.Equal(0,CloudFilesNative.Inspect(path)!.Value.Info.OnDiskSize);Assert.Equal(requests,test.Dav.Server.Requests.Count);
    }
    [Fact(Timeout=90000)]
    public async Task ExplicitReleaseBatchFollowsDeferredChildToItsPersistedCompletion()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Manager.CacheLimitBytes=0;await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);
        var path=Path.Combine(test.Local,"文件.bin");Pin(path,1);
        await Until(()=>test.Manager.Operations.Any(row=>row.Operation=="keepOnDevice"&&row.OperationEntryId is not null&&row.State=="Completed"));
        await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);string batch;
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            batch=test.Manager.Queue(test.Id,"freeSpace",[path]);await Until(()=>test.Manager.GetOperation(batch)?.State=="Waiting");
            Assert.Contains(test.Manager.GetOperation(batch)!.Results,row=>row.Status=="Waiting");
        }
        await test.Manager.EnforceCacheLimitAsync();Assert.Equal("Completed",test.Manager.GetOperation(batch)!.State);
        Assert.All(test.Manager.GetOperation(batch)!.Results,row=>Assert.Equal("Completed",row.Status));
    }
    [Theory(Timeout=90000)]
    [InlineData(false)][InlineData(true)]
    public async Task RestartRechecksDeferredIdentityAndVersionBeforeContinuing(bool changedVersion)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Manager.CacheLimitBytes=0;await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);
        var path=Path.Combine(test.Local,"文件.bin");_ = await File.ReadAllBytesAsync(path);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);Pin(path,2);string id,attempt;
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            await test.Manager.EnforceCacheLimitAsync();var row=Assert.Single(test.Manager.Operations,row=>row.Operation=="freeSpace"&&row.OperationEntryId is not null);id=row.Id;attempt=row.AttemptId!;
            await test.Manager.DisposeAsync();
            if(changedVersion){var entry=test.Repository.Entries(test.Id).Single(entry=>entry.LocalPath=="文件.bin");test.Repository.SaveEntry(test.Id,entry with{Version=entry.Version+"changed"});}
            test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);test.Manager.CacheLimitBytes=0;
            await test.Manager.EnforceCacheLimitAsync();Assert.Equal(attempt,test.Manager.GetOperation(id)!.AttemptId);
            Assert.Equal(changedVersion?"Failed":"Deferred",test.Manager.GetOperation(id)!.State);
        }
        await test.Manager.EnforceCacheLimitAsync();Assert.Equal(changedVersion?"Failed":"Completed",test.Manager.GetOperation(id)!.State);
        if(changedVersion)Assert.Equal("releaseVersion",test.Manager.GetOperation(id)!.FailureStage);
    }
    [Fact(Timeout=90000)]
    public async Task LatePageCancellationCannotCancelANewPinAttempt()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetThumbnailsEnabledAsync(test.Id,false);
        var path=Path.Combine(test.Local,"文件.bin");var firstHeaders=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler=test.Dav.Server.Handler;test.Dav.Server.Handler=async request=>{var response=await handler(request);return request.Method=="GET"?response with{BeforeBody=release.Task,HeadersSent=firstHeaders}:response;};
        try
        {
            Pin(path,1);await firstHeaders.Task.WaitAsync(TimeSpan.FromSeconds(10));await Until(()=>test.Manager.TasksQueue.Operations.Any(row=>row.Id.StartsWith("pin:",StringComparison.Ordinal)&&row.State=="Running"));
            var old=test.Manager.TasksQueue.Operations.First(row=>row.Id.StartsWith("pin:",StringComparison.Ordinal));Assert.True(test.Manager.TasksQueue.CancelSelected(old));
            await Until(()=>test.Manager.GetOperation(old.Id)?.State=="Canceled");Pin(path,1);
            await Until(()=>test.Manager.GetOperation(old.Id) is {State:"Running"} current&&current.AttemptId!=old.AttemptId);
            Assert.False(test.Manager.TasksQueue.CancelSelected(old));release.TrySetResult();await Until(()=>test.Manager.GetOperation(old.Id)?.State=="Completed");Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
        }
        finally{release.TrySetResult();}
    }

}
