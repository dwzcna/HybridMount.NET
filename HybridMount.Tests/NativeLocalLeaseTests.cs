using System.Runtime.InteropServices;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>真实隔离根验证未确认租约自动归还、确认所有者不按静默过期，以及最终内容戳失败不能假成功。</summary>
[Collection("Native IPC")]
public sealed class NativeLocalLeaseTests
{
    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task UnconfirmedOpenExpiresWithoutAnotherRequestButConfirmedOwnerSurvives(bool confirm)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);
        var runtime=await NativeRuntimeClient.GetAsync();var mount=test.Manager.Mounts.Single().Definition;var entry=test.Repository.Entries(test.Id).Single(x=>x.LocalPath=="文件.bin");var lease=Guid.NewGuid();var before=LocalFileState.Read(path);var cloud=CloudFilesNative.Inspect(path)!.Value;var requests=test.Dav.Server.Requests.Count;
        await runtime.InvokeAsync("openLocalRead",NativeRuntimeArguments.Entry(mount,entry,new{leaseId=lease,expected=before,testConfirmationMs=200}));
        if(confirm)await runtime.InvokeAsync("confirmLocalRead",new{mountId=test.Id,leaseId=lease});
        // 此窗口没有任何命令，计时器必须自主运行，不能由随后查询惰性触发。
        await Task.Delay(900);
        var stats=await runtime.InvokeAsync("testLocalReadStats",new{});Assert.Equal(confirm?1:0,stats.GetProperty("leases").GetInt32());Assert.Equal(0,stats.GetProperty("opening").GetInt32());
        if(confirm){Assert.Equal(test.Dav.Objects[entry.SourcePath].Bytes[..1],await runtime.ReadLocalRangeAsync(test.Id,lease,0,1,CancellationToken.None));await runtime.InvokeAsync("closeLocalRead",new{mountId=test.Id,leaseId=lease});}
        else{var error=await Assert.ThrowsAsync<COMException>(()=>runtime.InvokeAsync("confirmLocalRead",new{mountId=test.Id,leaseId=lease}));Assert.Equal(unchecked((int)0x80070490),error.HResult);}
        Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(cloud.Identity,after.Identity);Assert.Equal(cloud.Info.PinState,after.Info.PinState);Assert.Equal(cloud.Info.InSyncState,after.Info.InSyncState);Assert.Equal(requests,test.Dav.Server.Requests.Count);
        // 已过期或已关闭槽位允许再次打开；C#正常路径必须完成确认及最终戳核验。
        await using var next=await NativeLocalReadLease.OpenAsync(mount,entry,CancellationToken.None);
    }
    [Fact(Timeout=60000)]
    public async Task FinalStampFailureReleasesNativeHandleAndIsNotSwallowed()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);var runtime=await NativeRuntimeClient.GetAsync();var mount=test.Manager.Mounts.Single().Definition;var entry=test.Repository.Entries(test.Id).Single(x=>x.LocalPath=="文件.bin");var before=LocalFileState.Read(path);
        var lease=await NativeLocalReadLease.OpenAsync(mount,entry,CancellationToken.None);
        try{await runtime.InvokeAsync("testSetPin",new{path,expectedIdentity=before.Identity,pin=1u});var error=await Assert.ThrowsAsync<COMException>(()=>lease.DisposeAsync().AsTask());Assert.Equal(unchecked((int)0x800703ee),error.HResult);}
        finally{await lease.DisposeAsync();}
        Assert.Equal(0,(await runtime.InvokeAsync("testLocalReadStats",new{})).GetProperty("leases").GetInt32());Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);
    }
}
