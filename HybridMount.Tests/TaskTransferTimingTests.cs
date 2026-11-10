using System.Text.Json;
using HybridMount.Models.Mounting;
namespace HybridMount.Tests;

/// <summary>隔离回环服务与真实CFAPI验证完成后的平均速度及历史往返，不访问正式挂载。</summary>
[Collection("Native IPC")]
public sealed class TaskTransferTimingTests
{
    [Fact(Timeout=90000)]
    public async Task UploadAndDownloadKeepMeasuredAverageAfterCompletionAndHistoryRoundTrip()
    {
        await using var test=await UploadFlowTests.Fixture.Create(legacyPreviews:false);var path=Path.Combine(test.Local,"timed.bin");var bytes=new byte[16384];Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(path,bytes);var uploaded=await test.Wait("timed.bin",bytes:bytes);
        Assert.True(uploaded.TransferDurationSeconds>0);Assert.True(uploaded.ToSnapshot().HasAverageSpeed);Assert.Contains("平均",uploaded.ToSnapshot().CardState);
        var saved=JsonSerializer.Deserialize<LocalUpload>(JsonSerializer.Serialize(uploaded))!;Assert.Equal(uploaded.TransferDurationSeconds,saved.TransferDurationSeconds);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"freeSpace",[path]));
        var downloaded=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));
        Assert.Equal("Completed",downloaded.State);Assert.True(downloaded.DurationSeconds>0);Assert.Equal(bytes.Length,downloaded.TotalBytes);Assert.Equal(downloaded.TotalBytes,downloaded.BytesTransferred);
        Assert.True(downloaded.HasAverageSpeed);Assert.Contains("平均",downloaded.CardState);
        var history=test.Repository.LoadOperations().Single(item=>item.Id==downloaded.Id);Assert.Equal(downloaded.DurationSeconds,history.DurationSeconds);Assert.Equal(downloaded.CardState,history.CardState);
    }
    [Fact]
    public void LegacyCompletedRowsDoNotInventDurationAndDirectoriesHaveNoAverage()
    {
        var row=new MountOperationSnapshot("x","upload","Completed",1,1,[],null,TotalBytes:1000,IsDirectory:false);Assert.False(row.HasAverageSpeed);
        Assert.True((row with{DurationSeconds=2}).HasAverageSpeed);Assert.False((row with{DurationSeconds=2,IsDirectory=true}).HasAverageSpeed);
        Assert.False((row with{DurationSeconds=2,State="Failed"}).HasAverageSpeed);
        var old=JsonSerializer.Deserialize<MountOperationSnapshot>(JsonSerializer.Serialize(row))!;Assert.Equal(0,old.DurationSeconds);
    }
}
