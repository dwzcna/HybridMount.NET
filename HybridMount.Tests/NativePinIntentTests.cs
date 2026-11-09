using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>在隔离NTFS根只设置Windows原生固定意图，不打开正文或提交本程序下载任务。</summary>
public sealed class NativePinIntentTests
{
    [DllImport("CldApi.dll",EntryPoint="CfSetPinState")]
    private static extern int SetNativePin(SafeFileHandle handle,uint state,uint flags,nint overlap);
    private static void SetPin(string path,uint pin,uint flags=0)
    {
        // 原生菜单只改属性；不能用带正文访问的打开动作把待验证的下载隐式完成。
        using var handle=CloudFilesNative.CreateFile(path,0x180,7,0,3,0x02200000,0);
        Assert.False(handle.IsInvalid);CloudFilesNative.Check(SetNativePin(handle,pin,flags,0));
    }
    private static async Task Eventually(Func<bool> predicate,string message,int milliseconds=5000)
    {var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds){if(predicate())return;await Task.Delay(40);}Assert.True(predicate(),message);}

    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task NativePinDownloadsWithoutOpeningAndUnpinCompletesPromptly(bool thumbnails)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Manager.CacheLimitBytes=0;
        await test.Manager.SetThumbnailsEnabledAsync(test.Id,thumbnails);
        var path=Path.Combine(test.Local,"文件.bin");var identity=LocalFileState.Read(path).Identity;var marker=CloudFilesNative.Inspect(path)!.Value.Identity;
        Assert.False(CloudFilesNative.HasFullContent(path,test.Dav.Objects["文件.bin"].Bytes.Length));
        var puts=test.Dav.Server.Requests.Count(r=>r.Method=="PUT");SetPin(path,1);
        await Eventually(()=>CloudFilesNative.HasFullContent(path,test.Dav.Objects["文件.bin"].Bytes.Length),"只设置PINNED后没有主动下载。");
        Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Equal(3u,CloudShellState.Read(path).ProviderState);
        Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(marker,CloudFilesNative.Inspect(path)!.Value.Identity);
        SetPin(path,2);
        await Eventually(()=>CloudFilesNative.Inspect(path)!.Value.Info.OnDiskSize==0&&CloudShellState.Read(path).ProviderState==1,"UNPINNED仍依赖30秒维护。");
        Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);Assert.Equal(0,CloudFilesNative.Inspect(path)!.Value.Info.ModifiedSize);
        Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(puts,test.Dav.Server.Requests.Count(r=>r.Method=="PUT"));Assert.Empty(test.Repository.Uploads(test.Id));
    }

    [Fact(Timeout=60000)]
    public async Task ReconnectConsumesPersistedNativePinIntent()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");
        await test.Manager.DisconnectAsync(test.Id);SetPin(path,1);await test.Manager.ConnectAsync(test.Id);
        await Eventually(()=>CloudFilesNative.HasFullContent(path,test.Dav.Objects["文件.bin"].Bytes.Length),"重连没有恢复既有固定意图。");
        Assert.Equal(3u,CloudShellState.Read(path).ProviderState);Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method=="PUT");
    }
    [Fact(Timeout=60000)]
    public async Task NativeDownloadReleasesRootLockAndSharesExplicitKeep()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var headers=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original=test.Dav.Server.Handler;test.Dav.Server.Handler=async request=>{var response=await original(request);return request.Method=="GET"?response with{BeforeBody=release.Task,HeadersSent=headers}:response;};
        string? operation=null;
        try
        {
            SetPin(path,1);await headers.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await test.Manager.ScanUploadChangesAsync(test.Id).WaitAsync(TimeSpan.FromSeconds(2));
            operation=test.Manager.Queue(test.Id,"keepOnDevice",[path]);await Task.Delay(150);
            await test.Manager.ScanUploadChangesAsync(test.Id).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally{release.TrySetResult();}
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,operation!)).State);
        await Eventually(()=>CloudFilesNative.HasFullContent(path,test.Dav.Objects["文件.bin"].Bytes.Length),"共享下载没有完成。");
        Assert.Single(test.Dav.Server.Requests,r=>r.Method=="GET");Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method=="PUT");
        Assert.Empty(test.Repository.Uploads(test.Id));
    }

    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task UnpinAndDisconnectCancelBlockedBackgroundReadWithoutLateUpload(bool unpinFirst)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");var identity=LocalFileState.Read(path).Identity;
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var headers=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original=test.Dav.Server.Handler;test.Dav.Server.Handler=async request=>{var response=await original(request);return request.Method=="GET"?response with{BeforeBody=release.Task,HeadersSent=headers}:response;};
        try
        {
            SetPin(path,1);await headers.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(unpinFirst){SetPin(path,2);await Eventually(()=>CloudFilesNative.Inspect(path)!.Value.Info.OnDiskSize==0&&CloudShellState.Read(path).ProviderState==1,"取消固定没有停止待下载意图。");}
            await test.Manager.DisconnectAsync(test.Id).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(test.Manager.Mounts.Single().IsConnected);Assert.Equal(identity,LocalFileState.Read(path).Identity);
        }
        finally{release.TrySetResult();}
        Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method=="PUT");Assert.Empty(test.Repository.Uploads(test.Id));
    }

    [Theory(Timeout=60000)]
    [InlineData("protected")][InlineData("remote-pending")][InlineData("deleted")]
    public async Task AutomaticPinPreservesLocalAndRemoteBarriers(string protection)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");var entry=test.Repository.Entries(test.Id).Single(e=>e.LocalPath=="文件.bin");
        if(protection=="protected")test.Repository.SaveEntry(test.Id,entry with{Protected=true});
        if(protection=="deleted")Assert.True(test.Repository.SetRemotePresence(test.Id,entry,RemotePresence.Deleted));
        if(protection=="remote-pending")test.Repository.SaveRemotePending(new(Guid.NewGuid().ToString("N"),test.Id,"pin-safety",entry.SourcePath,null));
        var identity=LocalFileState.Read(path).Identity;var requests=test.Dav.Server.Requests.Count;SetPin(path,1);await Task.Delay(500);
        Assert.False(CloudFilesNative.HasFullContent(path,test.Dav.Objects["文件.bin"].Bytes.Length));Assert.Equal(identity,LocalFileState.Read(path).Identity);
        Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.All(test.Repository.Uploads(test.Id),x=>Assert.Equal("Queued",x.State));
    }

    [Fact(Timeout=60000)]
    public async Task RecursiveDirectoryPinAndZeroLengthFileCompleteWithoutOpening()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Dav.Put("空目录/zero.bin",[]);test.Dav.Put("空目录/child.bin",[1,2,3]);
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"))).State);
        var directory=Path.Combine(test.Local,"空目录");SetPin(directory,1,1);
        foreach(var name in new[]{"zero.bin","child.bin"})
        {
            var path=Path.Combine(directory,name);await Eventually(()=>CloudShellState.Read(path).ProviderState==3,"目录内文件未完成原生递归固定。");
            Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.True(CloudFilesNative.HasFullContent(path,test.Dav.Objects["空目录/"+name].Bytes.Length));
        }
        Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method=="PUT");
    }

    [Fact(Timeout=60000)]
    public async Task ResourceReleaseWakesUnpinAfterCancelledWaiterAlreadyReturned()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Manager.CacheLimitBytes=0;var path=Path.Combine(test.Local,"文件.bin");
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.PinResourcesReleasingForTests=_=>{held.TrySetResult();return release.Task;};
        try
        {
            SetPin(path,1);await held.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.True(CloudFilesNative.HasFullContent(path,test.Dav.Objects["文件.bin"].Bytes.Length));
            SetPin(path,2);await Task.Delay(300);Assert.True(CloudFilesNative.Inspect(path)!.Value.Info.OnDiskSize>0);
            release.TrySetResult();
            await Eventually(()=>CloudFilesNative.Inspect(path)!.Value.Info.OnDiskSize==0&&CloudShellState.Read(path).ProviderState==1,"水合资源归还后未立即重新完成释放。");
        }
        finally{release.TrySetResult();}
        Assert.DoesNotContain(test.Dav.Server.Requests,r=>r.Method=="PUT");
    }

    [Fact(Timeout=60000)]
    public async Task CancellingExplicitWaiterKeepsNativePinOwnerAndSingleGet()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var headers=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original=test.Dav.Server.Handler;test.Dav.Server.Handler=async request=>{var response=await original(request);return request.Method=="GET"?response with{BeforeBody=release.Task,HeadersSent=headers}:response;};
        try
        {
            SetPin(path,1);await headers.Task.WaitAsync(TimeSpan.FromSeconds(5));var job=test.Manager.Queue(test.Id,"keepOnDevice",[path]);await Task.Delay(200);
            Assert.True(test.Manager.Cancel(job));Assert.Equal("Canceled",(await WebDavTests.Finished(test.Manager,job)).State);
            Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Single(test.Dav.Server.Requests,r=>r.Method=="GET");
            release.TrySetResult();await Eventually(()=>CloudFilesNative.HasFullContent(path,test.Dav.Objects["文件.bin"].Bytes.Length),"取消单个等待者误停了原生固定意图。");
            Assert.Single(test.Dav.Server.Requests,r=>r.Method=="GET");
        }
        finally{release.TrySetResult();}
    }

}
