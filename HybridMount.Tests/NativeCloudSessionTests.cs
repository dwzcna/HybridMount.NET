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
                await runtime.InvokeAsync("finishFetch",NativeRuntimeArguments.Root(mount,new{sessionId,fetchId=fetch,status=0}));
                await runtime.InvokeAsync("finishFetch",NativeRuntimeArguments.Root(mount,new{sessionId,fetchId=fetch,status=unchecked((int)0xc000cf00)}));
            }));
        };
        try
        {
            await runtime.InvokeAsync("registerRoot",NativeRuntimeArguments.Root(mount));registered=true;
            var marker=MountPathGuard.ReadRoot(area);Assert.NotNull(marker);Assert.Equal(id,marker.MountId);
            await runtime.InvokeAsync("connectRoot",NativeRuntimeArguments.Root(mount,new{sessionId,blockHydration=false}));connected=true;
            await runtime.InvokeAsync("createPlaceholder",NativeRuntimeArguments.Entry(mount,entry,new{size=(long)bytes.Length,time}));var path=Path.Combine(area,"payload.bin");var before=LocalFileState.Read(path);Assert.False(CloudFilesNative.HasFullContent(path,bytes.Length));
            Assert.Equal(bytes,await File.ReadAllBytesAsync(path).WaitAsync(TimeSpan.FromSeconds(20)));await Task.WhenAll(jobs.ToArray());Assert.True(fetchCount>0);Assert.True(CloudFilesNative.HasFullContent(path,bytes.Length));Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);
            var stale=NativeRuntimeArguments.Entry(mount,entry,new{sessionId=Guid.NewGuid().ToString("N"),propertyId=2,text="must-not-write",icon=""});await Assert.ThrowsAnyAsync<Exception>(()=>runtime.InvokeAsync("setProperty",stale));Assert.Empty(CloudCustomStateReader.Read(path).Ids);
            var lease=Guid.NewGuid();var stamp=LocalFileState.Read(path);await runtime.InvokeAsync("openLocalRead",NativeRuntimeArguments.Entry(mount,entry,new{leaseId=lease,expectedStamp=stamp.Key}));
            var block=await runtime.ReadLocalRangeAsync(id,lease,17,NativeRuntimeProtocol.BlockBytes,CancellationToken.None);Assert.Equal(bytes.AsSpan(17,block.Length).ToArray(),block);Assert.Equal(stamp,LocalFileState.Read(path));
            var wrong=await Assert.ThrowsAnyAsync<Exception>(()=>runtime.ReadLocalRangeAsync(Guid.NewGuid(),lease,0,1,CancellationToken.None));Assert.NotNull(wrong);Assert.True(runtime.IsAvailable);
            await runtime.InvokeAsync("closeLocalRead",new{mountId=id,leaseId=lease});
            var released=await runtime.InvokeAsync("releaseFile",NativeRuntimeArguments.Entry(mount,entry,new{sessionId}));Assert.True(released.GetProperty("changed").GetBoolean());Assert.False(CloudFilesNative.HasFullContent(path,bytes.Length));Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);
            await runtime.InvokeAsync("disconnectRoot",NativeRuntimeArguments.Root(mount,new{sessionId}));connected=false;
        }
        finally
        {
            if(connected)await runtime.InvokeAsync("disconnectRoot",NativeRuntimeArguments.Root(mount,new{sessionId}));
            if(registered)await runtime.InvokeAsync("unregisterRoot",NativeRuntimeArguments.Root(mount));
            Assert.Null(CloudFilesNative.RootIdentity(area,true));Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()),Path.GetDirectoryName(area),ignoreCase:true);Assert.StartsWith("HybridMount.NativeRoot.",Path.GetFileName(area));Directory.Delete(area,true);
        }
    }
}
