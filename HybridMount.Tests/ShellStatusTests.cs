using System.Text;
using HybridMount.Services.Mounting;
using Xunit.Abstractions;

namespace HybridMount.Tests;

/// <summary>验证真实NTFS提交边界、缩略图关闭时的通知及不水合的Shell只读查询。</summary>
public sealed class ShellStatusTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10u,512u,false)]
    [InlineData(2u,0u,false)]
    [InlineData(3u,8u,true)]
    public void IncompleteAndPausedHaveDistinctSdkMeanings(uint state,uint transfer,bool paused)
    {
        var status=new ShellFileStatus(state,transfer,14);
        Assert.Equal(paused,status.TransferName.Contains("已暂停",StringComparison.Ordinal));
        if(transfer==512){Assert.Equal("待处理（未指定）",status.ProviderName);Assert.Contains("未完成",status.TransferName);}
    }
    [Fact(Timeout=60000)]
    public async Task SuccessfulUploadNotifiesAfterTransactionHandleAndGateReleaseWithThumbnailsOff()
    {
        await using var test=await UploadFlowTests.Fixture.Create();Assert.False(test.Repository.LoadDefinitions().Single().ThumbnailsEnabled);
        var path=Path.Combine(test.Local,"notify-committed.txt");var notified=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.ShellNotificationForTests=changed=>
        {
            if(changed!=path)return;
            try
            {
                Assert.Equal("Completed",test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="notify-committed.txt").State);
                Assert.False(test.Repository.Entries(test.Id).Single(item=>item.LocalPath=="notify-committed.txt").Protected);
                using(var exclusive=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))Assert.Equal(9,exclusive.Length);
                Assert.True(test.Manager.ScanUploadChangesAsync(test.Id).Wait(TimeSpan.FromSeconds(2)),"Shell通知时根锁仍被持有。");
                CloudFilesNative.NotifyItemUpdated(path);notified.TrySetResult();
            }
            catch(Exception error){notified.TrySetException(error);}
        };
        await File.WriteAllTextAsync(path,"committed");
        try{await notified.Task.WaitAsync(TimeSpan.FromSeconds(30));await test.Wait("notify-committed.txt");}
        catch{output.WriteLine("Root="+test.Local);output.WriteLine(System.Text.Json.JsonSerializer.Serialize(test.Repository.Uploads(test.Id)));output.WriteLine(System.Text.Json.JsonSerializer.Serialize(test.Manager.Operations));output.WriteLine(await test.Manager.ReadDiagnosticsAsync(test.Id));throw;}
    }
    [Fact(Timeout=60000)]
    public async Task EditingAfterRemoteReceiptCannotPublishCompletedNotification()
    {
        await using var test=await UploadFlowTests.Fixture.Create();var path=Path.Combine(test.Local,"notify-dirty.txt");var notifications=0;
        var committed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.ShellNotificationForTests=changed=>{if(changed==path)Interlocked.Increment(ref notifications);};
        test.Manager.UploadCheckpointForTests=async(stage,item)=>
        {
            if(item.LocalPath!="notify-dirty.txt")return;
            if(stage=="RemoteCommitted"){await test.Manager.SetSchedulingForTestsAsync(test.Id,false);await File.WriteAllTextAsync(path,"local B");}
            if(stage=="Committed")committed.TrySetResult();
        };
        await File.WriteAllTextAsync(path,"remote A");await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.Equal(0,notifications);Assert.NotEqual("Completed",test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="notify-dirty.txt").State);
        Assert.Equal("local B",await File.ReadAllTextAsync(path));Assert.Equal("remote A",Encoding.UTF8.GetString(test.Dav.Objects["notify-dirty.txt"].Bytes));
    }
    [Fact(Timeout=60000)]
    public async Task ReadOnlyShellDiagnosticsDoNotHydrateModifyOrRegisterAndRejectAfterStop()
    {
        await using var test=await UploadFlowTests.Fixture.Create();var path=Path.Combine(test.Local,"文件.bin");
        var before=CloudFilesNative.Inspect(path)!.Value;var requests=test.Dav.Server.Requests.Count;var root=MountPathGuard.ReadRoot(test.Local);
        var status=CloudShellState.Read(path);output.WriteLine(status.ToString());Assert.Null(status.Error);
        var detail=await test.Manager.ReadDiagnosticsAsync(test.Id);output.WriteLine(detail);
        Assert.Contains("运行版本：",detail);Assert.Contains("运行包身份：",detail);Assert.Contains("Shell：",detail);Assert.DoesNotContain("app-password",detail);
        var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(before.Info.OnDiskSize,after.Info.OnDiskSize);Assert.Equal(before.Info.ModifiedSize,after.Info.ModifiedSize);Assert.Equal(before.Identity,after.Identity);
        Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal(root,MountPathGuard.ReadRoot(test.Local));
        await test.Manager.DisposeAsync();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>test.Manager.ReadDiagnosticsAsync(test.Id));
        test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);
    }
    [Fact(Timeout=60000)]
    public async Task NotificationFailureDoesNotRepeatCompletedUpload()
    {
        await using var test=await UploadFlowTests.Fixture.Create();var path=Path.Combine(test.Local,"notify-failure.txt");var attempted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.ShellNotificationForTests=changed=>{if(changed==path){attempted.TrySetResult();throw new IOException("isolated notification failure");}};
        await File.WriteAllTextAsync(path,"keep result");await attempted.Task.WaitAsync(TimeSpan.FromSeconds(30));await test.Wait("notify-failure.txt");
        await test.Manager.DisconnectAsync(test.Id);await test.Manager.ConnectAsync(test.Id);await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.Single(test.Dav.Server.Requests,r=>r.Method=="PUT"&&r.Target.EndsWith("/notify-failure.txt",StringComparison.Ordinal));
    }
}
