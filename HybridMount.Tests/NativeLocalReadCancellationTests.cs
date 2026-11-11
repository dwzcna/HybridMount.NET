using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace HybridMount.Tests;

/// <summary>真实隔离NTFS占位租约加完成延迟注入，验证取消／关闭不释放内核所有者，且不读取远端。</summary>
[Collection("Native IPC")]
public sealed class NativeLocalReadCancellationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task CancelOrCloseReturnsBeforeDelayedCompletionAndKeepsProtectedOwner(bool close)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var file=Path.Combine(test.Local,"文件.bin");var original=await File.ReadAllBytesAsync(file);var before=LocalFileState.Read(file);var cloud=CloudFilesNative.Inspect(file)!.Value;var requests=test.Dav.Server.Requests.Count;
        var runtime=await NativeRuntimeClient.GetAsync();var mount=test.Manager.Mounts.Single().Definition;var entry=test.Repository.EntryBySource(test.Id,"文件.bin")!;var lease=await Open(runtime,mount,entry,new{testCompletionDelayMs=1600});var initial=await Stats(runtime);using var cancel=new CancellationTokenSource();
        var reading=runtime.ReadLocalRangeAsync(test.Id,lease,0,1,cancel.Token);await Until(async()=>(await Stats(runtime)).GetProperty("inFlight").GetInt64()>0);var watch=Stopwatch.StartNew();
        if(close){var error=await Assert.ThrowsAsync<COMException>(()=>runtime.InvokeAsync("closeLocalRead",new{mountId=test.Id,leaseId=lease}));Assert.Equal(unchecked((int)0x800703e4),error.HResult);await Assert.ThrowsAnyAsync<Exception>(()=>reading);}
        else{cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>reading);}
        watch.Stop();Assert.InRange(watch.Elapsed.TotalMilliseconds,0,1100);var retained=await Stats(runtime);Assert.True(retained.GetProperty("inFlight").GetInt64()>0);Assert.True(runtime.IsAvailable);Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
        if(!close){var pending=await Assert.ThrowsAsync<COMException>(()=>runtime.InvokeAsync("closeLocalRead",new{mountId=test.Id,leaseId=lease}));Assert.Equal(unchecked((int)0x800703e4),pending.HResult);}
        var repeated=await Assert.ThrowsAsync<COMException>(()=>runtime.InvokeAsync("closeLocalRead",new{mountId=test.Id,leaseId=lease}));Assert.Equal(unchecked((int)0x800703e4),repeated.HResult);
        await Until(async()=>(await Stats(runtime)).GetProperty("inFlight").GetInt64()==0);Assert.Equal(0,(await Stats(runtime)).GetProperty("leases").GetInt32());Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal(before.Identity,LocalFileState.Read(file).Identity);var after=CloudFilesNative.Inspect(file)!.Value;Assert.Equal(cloud.Identity,after.Identity);Assert.Equal(cloud.Info.PinState,after.Info.PinState);Assert.Equal(cloud.Info.InSyncState,after.Info.InSyncState);Assert.Equal(cloud.Info.OnDiskSize,after.Info.OnDiskSize);Assert.Equal(original,await File.ReadAllBytesAsync(file));
        output.WriteLine(JsonSerializer.Serialize(new{Close=close,ReturnMs=watch.Elapsed.TotalMilliseconds,Before=initial,Retained=retained,Final=await Stats(runtime)}));
        await using var next=await NativeLocalReadLease.OpenAsync(mount,entry,CancellationToken.None);
    }
    [Fact(Timeout=60000)]
    public async Task DeadlineRetainsLateIoAndSameLeaseRejectsParallelRead()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await File.ReadAllBytesAsync(Path.Combine(test.Local,"文件.bin"));var runtime=await NativeRuntimeClient.GetAsync();var mount=test.Manager.Mounts.Single().Definition;var entry=test.Repository.EntryBySource(test.Id,"文件.bin")!;var lease=await Open(runtime,mount,entry,new{testReadBudgetMs=80,testCompletionDelayMs=1200});var baseline=await Stats(runtime);
        var reading=runtime.ReadLocalRangeAsync(test.Id,lease,0,1,default);await Until(async()=>(await Stats(runtime)).GetProperty("inFlight").GetInt64()>0);var parallel=await Assert.ThrowsAsync<COMException>(()=>runtime.ReadLocalRangeAsync(test.Id,lease,0,1,default));Assert.Equal(unchecked((int)0x800700aa),parallel.HResult);
        var timeout=await Assert.ThrowsAsync<COMException>(()=>reading);Assert.Equal(unchecked((int)0x800705b4),timeout.HResult);Assert.True((await Stats(runtime)).GetProperty("retained").GetInt64()>baseline.GetProperty("retained").GetInt64());
        await Until(async()=>(await Stats(runtime)).GetProperty("inFlight").GetInt64()==0);var result=await runtime.InvokeAsync("closeLocalRead",new{mountId=test.Id,leaseId=lease});Assert.True(result.TryGetProperty("stamp",out _));Assert.Equal(result.GetRawText(),(await runtime.InvokeAsync("closeLocalRead",new{mountId=test.Id,leaseId=lease})).GetRawText());
    }
    [Theory(Timeout=60000)]
    [InlineData(false,30)][InlineData(true,38)]
    public async Task ShortReadAndKernelFailureNeverPublishPartialBody(bool shortRead,int error)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);var before=LocalFileState.Read(path);var runtime=await NativeRuntimeClient.GetAsync();var lease=await Open(runtime,test.Manager.Mounts.Single().Definition,test.Repository.EntryBySource(test.Id,"文件.bin")!,new{testShortRead=shortRead,testReadError=shortRead?0:error});
        var failure=await Assert.ThrowsAsync<COMException>(()=>runtime.ReadLocalRangeAsync(test.Id,lease,0,1,default));Assert.Equal(unchecked((int)0x80070000)|error,failure.HResult);Assert.True(runtime.IsAvailable);Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);await runtime.InvokeAsync("closeLocalRead",new{mountId=test.Id,leaseId=lease});Assert.Equal(0,(await Stats(runtime)).GetProperty("inFlight").GetInt64());
    }
    [Fact(Timeout=60000)]
    public async Task CompletedReadsAndCancellationRaceDrainFramesWithoutFailingChannel()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");var bytes=await File.ReadAllBytesAsync(path);var runtime=await NativeRuntimeClient.GetAsync();await using var lease=await NativeLocalReadLease.OpenAsync(test.Manager.Mounts.Single().Definition,test.Repository.EntryBySource(test.Id,"文件.bin")!,default);
        for(var i=0;i<80;i++){using var cancel=new CancellationTokenSource();if(i%3==0)cancel.CancelAfter(TimeSpan.FromMilliseconds(1));var result=new byte[1];try{var read=await lease.ReadAsync(i%bytes.Length,result,cancel.Token);Assert.Equal(1,read.Bytes);Assert.Equal(bytes[i%bytes.Length],result[0]);}catch(OperationCanceledException)when(cancel.IsCancellationRequested){}Assert.True(runtime.IsAvailable);}
        Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());output.WriteLine((await Stats(runtime)).GetRawText());
    }
    [Fact(Timeout=60000)]
    public async Task ExternalWriterCannotChangeProtectedContentAndLeaseReleasesForSubsequentEdit()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");var bytes=await File.ReadAllBytesAsync(path);var runtime=await NativeRuntimeClient.GetAsync();var lease=await NativeLocalReadLease.OpenAsync(test.Manager.Mounts.Single().Definition,test.Repository.EntryBySource(test.Id,"文件.bin")!,default);
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};start.Environment["HM_LOCAL_READ_FILE"]=path;
        const string script="try{$f=[IO.File]::Open($env:HM_LOCAL_READ_FILE,[IO.FileMode]::Open,[IO.FileAccess]::Write,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));$f.Dispose();[Console]::WriteLine('opened')}catch{[Console]::WriteLine('blocked')}";
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(arg);using var process=Process.Start(start)!;
        await Task.Delay(100);try{await lease.DisposeAsync();}catch(COMException error)when(error.HResult==unchecked((int)0x800703ee)){/* 外部打开请求可能已打破oplock，不能将最终状态当作可发布。 */}await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));var result=await process.StandardOutput.ReadToEndAsync();Assert.True(result.Trim() is "opened" or "blocked",result);Assert.Equal(0,process.ExitCode);Assert.Equal(bytes,await File.ReadAllBytesAsync(path));Assert.Equal(0,(await Stats(runtime)).GetProperty("inFlight").GetInt64());
    }
    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task OwnerStopOrProcessCrashDuringReadDoesNotLeaveProtectedHandles(bool crash)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");var bytes=await File.ReadAllBytesAsync(path);var before=LocalFileState.Read(path);await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var lease=await Open(runtime,test.Manager.Mounts.Single().Definition,test.Repository.EntryBySource(test.Id,"文件.bin")!,new{testCompletionDelayMs=2500});
        var reading=runtime.ReadLocalRangeAsync(test.Id,lease,0,1,default);await Until(async()=>(await Stats(runtime)).GetProperty("inFlight").GetInt64()>0);var watch=Stopwatch.StartNew();using var process=Process.GetProcessById(runtime.ProcessId);
        if(crash){process.Kill();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}else await runtime.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(()=>reading);Assert.InRange(watch.Elapsed.TotalMilliseconds,0,5000);Assert.True(process.HasExited);using(var edit=File.Open(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){}Assert.Equal(bytes,await File.ReadAllBytesAsync(path));Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);
    }
    [Fact(Timeout=60000)]
    public async Task RetiredOperationsStillConsumeSixteenItemGlobalLimitUntilCompletion()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);for(var i=0;i<17;i++)test.Dav.Put($"read-bound-{i}.bin",new byte[]{1,2,3,4});await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var runtime=await NativeRuntimeClient.GetAsync();var mount=test.Manager.Mounts.Single().Definition;var entries=Enumerable.Range(0,17).Select(i=>test.Repository.EntryBySource(test.Id,$"read-bound-{i}.bin")!).ToArray();foreach(var entry in entries)await File.ReadAllBytesAsync(Path.Combine(test.Local,entry.LocalPath));
        var requests=test.Dav.Server.Requests.Count;
        try
        {
            for(var i=0;i<16;i++)
            {
                var lease=await Open(runtime,mount,entries[i],new{testCompletionDelayMs=10000});var reading=runtime.ReadLocalRangeAsync(test.Id,lease,0,1,default);var target=i+1;await Until(async()=>(await Stats(runtime)).GetProperty("inFlight").GetInt64()==target);
                var closed=await Assert.ThrowsAsync<COMException>(()=>runtime.InvokeAsync("closeLocalRead",new{mountId=test.Id,leaseId=lease}));Assert.Equal(unchecked((int)0x800703e4),closed.HResult);await Assert.ThrowsAnyAsync<Exception>(()=>reading);
            }
            var last=await Open(runtime,mount,entries[16],new{});try{var rejected=await Assert.ThrowsAsync<COMException>(()=>runtime.ReadLocalRangeAsync(test.Id,last,0,1,default));Assert.Equal(unchecked((int)0x800700aa),rejected.HResult);Assert.Equal(16,(await Stats(runtime)).GetProperty("inFlight").GetInt64());}finally{await runtime.InvokeAsync("closeLocalRead",new{mountId=test.Id,leaseId=last});}
        }
        finally{var until=Environment.TickCount64+16000;while((await Stats(runtime)).GetProperty("inFlight").GetInt64()!=0){Assert.True(Environment.TickCount64<until);await Task.Delay(20);}}
        Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal(16,(await Stats(runtime)).GetProperty("maximumInFlight").GetInt64());Assert.True(runtime.IsAvailable);output.WriteLine((await Stats(runtime)).GetRawText());
    }
    private static async Task<Guid> Open(NativeRuntimeClient runtime,MountDefinition mount,MountEntry entry,object extra)
    {
        var id=Guid.NewGuid();var fields=JsonSerializer.SerializeToElement(extra).EnumerateObject().ToDictionary(x=>x.Name,x=>(object?)x.Value.Clone());fields["leaseId"]=id;fields["expected"]=LocalFileState.Read(Path.Combine(mount.LocalPath,entry.LocalPath));await runtime.InvokeAsync("openLocalRead",NativeRuntimeArguments.Entry(mount,entry,fields));await runtime.InvokeAsync("confirmLocalRead",new{mountId=mount.Id,leaseId=id});return id;
    }
    private static Task<JsonElement> Stats(NativeRuntimeClient runtime)=>runtime.InvokeAsync("testLocalReadStats",new{});
    private static async Task Until(Func<Task<bool>> check){var until=Environment.TickCount64+6000;while(!await check()){if(Environment.TickCount64>until)throw new TimeoutException("在途读取没有到达预期阶段");await Task.Delay(5);}}
}
