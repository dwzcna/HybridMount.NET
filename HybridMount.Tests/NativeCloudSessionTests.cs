using System.Collections.Concurrent;
using System.Text;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>全新NTFS根的原生端到端链路；托管端只提供内容字节，不注册回调或执行CfExecute。</summary>
[Collection("Native IPC")]
public sealed class NativeCloudSessionTests
{
    [Fact(Timeout=90000)]
    public async Task NativeRootFetchAndReleasePreserveIdentityAndUseSeparateContentChannel()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.NativeRoot."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        var id=Guid.NewGuid();var mount=new MountDefinition(id,"Native session verification",area,RootIdentity:MountPathGuard.FileIdentity(area));var sessionId=Guid.NewGuid().ToString("N");
        var bytes=Encoding.UTF8.GetBytes(new string('n',700000));var entry=new MountEntry("native-entry","payload.bin","payload.bin","native-version",false,false,false,false);var time=DateTimeOffset.UtcNow.ToFileTime();
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var connected=false;var registered=false;var jobs=new ConcurrentBag<Task>();var fetchCount=0;
        runtime.EventReceived+=value=>
        {
            if(value.SessionId!=sessionId||value.Event!="fetch")return;
            var data=value.Data.Clone();jobs.Add(Task.Run(async()=>
            {
                Interlocked.Increment(ref fetchCount);var fetch=Guid.Parse(data.GetProperty("fetchId").GetString()!);var offset=NativeRuntimeProtocol.Read<long>(data.GetProperty("offset"));var length=NativeRuntimeProtocol.Read<long>(data.GetProperty("length"));var start=offset/4096*4096;var end=Math.Min(bytes.LongLength,(offset+length+4095)/4096*4096);
                for(var position=start;position<end;){var count=(int)Math.Min(NativeRuntimeProtocol.BlockBytes,end-position);await runtime.TransferAsync(fetch,position,bytes.AsMemory((int)position,count),CancellationToken.None);position+=count;}
                var attempts=0;await CloudMountSession.FinishFetchWithRetryAsync(async()=>{attempts++;try{await runtime.InvokeAsync("finishFetch",NativeRuntimeArguments.Root(mount,new{sessionId,fetchId=fetch,status=0}));}catch(System.Runtime.InteropServices.COMException error)when(error.HResult==unchecked((int)0x800700aa)){Assert.True((await runtime.InvokeAsync("activity",NativeRuntimeArguments.Entry(mount,entry,new{sessionId}))).GetProperty("active").GetBoolean());throw;}},()=>runtime.IsAvailable);Assert.InRange(attempts,1,2);
                await runtime.InvokeAsync("finishFetch",NativeRuntimeArguments.Root(mount,new{sessionId,fetchId=fetch,status=unchecked((int)0xc000cf00)}));
            }));
        };
        try
        {
            await runtime.InvokeAsync("registerRoot",NativeRuntimeArguments.Root(mount));registered=true;
            var marker=MountPathGuard.ReadRoot(area);Assert.NotNull(marker);Assert.Equal(id,marker.MountId);
            await runtime.InvokeAsync("connectRoot",NativeRuntimeArguments.Root(mount,new{sessionId,blockHydration=false}));connected=true;await runtime.InvokeAsync("testCloudFaults",NativeRuntimeArguments.Root(mount,new{sessionId,finish=1}));
            await runtime.InvokeAsync("createPlaceholder",NativeRuntimeArguments.Entry(mount,entry,new{size=(long)bytes.Length,time}));var path=Path.Combine(area,"payload.bin");var before=LocalFileState.Read(path);Assert.False(CloudFilesNative.HasFullContent(path,bytes.Length));
            Assert.Equal(bytes,await File.ReadAllBytesAsync(path).WaitAsync(TimeSpan.FromSeconds(20)));await Task.WhenAll(jobs.ToArray());Assert.True(fetchCount>0);Assert.True(CloudFilesNative.HasFullContent(path,bytes.Length));Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);
            var stale=NativeRuntimeArguments.Entry(mount,entry,new{sessionId=Guid.NewGuid().ToString("N"),propertyId=2,text="must-not-write",icon=""});await Assert.ThrowsAnyAsync<Exception>(()=>runtime.InvokeAsync("setProperty",stale));Assert.Empty(CloudCustomStateReader.Read(path).Ids);
            var lease=Guid.NewGuid();var stamp=LocalFileState.Read(path);await runtime.InvokeAsync("openLocalRead",NativeRuntimeArguments.Entry(mount,entry,new{leaseId=lease,expectedStamp=stamp.Key}));
            var block=await runtime.ReadLocalRangeAsync(id,lease,17,NativeRuntimeProtocol.BlockBytes,CancellationToken.None);Assert.Equal(bytes.AsSpan(17,block.Length).ToArray(),block);Assert.Equal(stamp,LocalFileState.Read(path));
            var wrong=await Assert.ThrowsAnyAsync<Exception>(()=>runtime.ReadLocalRangeAsync(Guid.NewGuid(),lease,0,1,CancellationToken.None));Assert.NotNull(wrong);Assert.True(runtime.IsAvailable);
            await runtime.InvokeAsync("closeLocalRead",new{mountId=id,leaseId=lease});
            var released=await runtime.InvokeAsync("releaseFile",NativeRuntimeArguments.Entry(mount,entry,new{sessionId}));Assert.True(released.GetProperty("changed").GetBoolean());Assert.False(CloudFilesNative.HasFullContent(path,bytes.Length));Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);
            await runtime.InvokeAsync("testCloudFaults",NativeRuntimeArguments.Root(mount,new{sessionId,disconnect=1}));var disconnect=await Assert.ThrowsAsync<System.Runtime.InteropServices.COMException>(()=>runtime.InvokeAsync("disconnectRoot",NativeRuntimeArguments.Root(mount,new{sessionId})));Assert.Equal(unchecked((int)0x800700aa),disconnect.HResult);Assert.True(runtime.IsAvailable);Assert.NotNull(CloudFilesNative.RootIdentity(area,true));
            await runtime.InvokeAsync("disconnectRoot",NativeRuntimeArguments.Root(mount,new{sessionId}));connected=false;
        }
        finally
        {
            if(connected)await runtime.InvokeAsync("disconnectRoot",NativeRuntimeArguments.Root(mount,new{sessionId}));
            if(registered)await runtime.InvokeAsync("unregisterRoot",NativeRuntimeArguments.Root(mount));
            Assert.Null(CloudFilesNative.RootIdentity(area,true));Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()),Path.GetDirectoryName(area),ignoreCase:true);Assert.StartsWith("HybridMount.NativeRoot.",Path.GetFileName(area));Directory.Delete(area,true);
        }
    }
    /// <summary>确定性保留一个使用真实回调票据的测试回调；断开必须等待归还且控制通道仍可释放它。</summary>
    [Fact(Timeout=30000)]
    public async Task DisconnectDrainsEnteredCallbackBeforeRetiringContext()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var area=Path.Combine(Path.GetTempPath(),"HybridMount.NativeRoot."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        var mount=new MountDefinition(Guid.NewGuid(),"回调排空",area,false,RootIdentity:LocalFileState.Read(area).Identity);var sessionId=Guid.NewGuid().ToString("N");var args=NativeRuntimeArguments.Root(mount,new{sessionId});var registered=false;var connected=false;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);runtime.EventReceived+=message=>{if(message.Event=="testCallback")entered.TrySetResult();};
        try
        {
            await runtime.InvokeAsync("registerRoot",NativeRuntimeArguments.Root(mount));registered=true;await runtime.InvokeAsync("connectRoot",NativeRuntimeArguments.Root(mount,new{sessionId,blockHydration=false}));connected=true;
            var callback=runtime.InvokeAsync("testHoldCallback",args);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var disconnect=runtime.InvokeAsync("disconnectRoot",args);await Task.Delay(150);Assert.False(disconnect.IsCompleted);
            await runtime.InvokeAsync("testReleaseCallback",args);await Task.WhenAll(callback,disconnect).WaitAsync(TimeSpan.FromSeconds(5));connected=false;
            Assert.True(runtime.IsAvailable);Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
        }
        finally{if(connected&&runtime.IsAvailable){await runtime.InvokeAsync("testReleaseCallback",args);await runtime.InvokeAsync("disconnectRoot",args);}if(registered)await runtime.InvokeAsync("unregisterRoot",NativeRuntimeArguments.Root(mount));Directory.Delete(area,true);}
    }
}
