using System.Security.Cryptography;
using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class StagingDiskSpaceTests
{
    [Theory]
    [InlineData(0L)][InlineData(1L)][InlineData(1099511627776L)][InlineData(long.MaxValue)]
    public void SizePlusMarginHasExactBoundaryWithoutOverflow(long size)
    {
        using var fixture=new Files();var required=(ulong)size+StagingDiskSpace.MarginBytes;var error=Assert.Throws<StagingSpaceException>(()=>StagingDiskSpace.Require(fixture.Snapshot,size,_=>required-1));Assert.Contains("磁盘空间不足",error.Message);Assert.Contains(required.ToString("N0"),error.Message);StagingDiskSpace.Require(fixture.Snapshot,size,_=>required);
    }
    [Fact]
    public void ChecksTargetAncestorInsteadOfSourceDriveAndUsesRealVolumeQuery()
    {
        using var fixture=new Files();var nested=Path.Combine(fixture.Area,"missing","deep","file.snapshot");var called=false;
        StagingDiskSpace.Require(nested,1,directory=>{Assert.Equal(fixture.Area,directory);called=true;return ulong.MaxValue;});Assert.True(called);Assert.True(StagingDiskSpace.AvailableBytes(fixture.Area)>0);
    }
    [Fact]
    public void QueryFailureIsClearRetryableAndNegativeLengthIsRejected()
    {
        using var fixture=new Files();var error=Assert.Throws<StagingSpaceException>(()=>StagingDiskSpace.Require(fixture.Snapshot,1,_=>throw new IOException("unavailable")));Assert.Contains("无法读取目标卷",error.Message);Assert.IsType<IOException>(error.InnerException);Assert.Throws<ArgumentOutOfRangeException>(()=>StagingDiskSpace.Require(fixture.Snapshot,-1));
    }
    [Theory]
    [InlineData(0)][InlineData(262145)]
    public async Task SnapshotSpaceCheckPreventsFileCreationAndRetryCopiesCorrectContent(int length)
    {
        using var fixture=new Files();var bytes=new byte[length];File.WriteAllBytes(fixture.Source,bytes);var stamp=LocalFileState.Read(fixture.Source);var copied=false;
        await Assert.ThrowsAsync<StagingSpaceException>(()=>UploadPreparation.CopyAsync(fixture.Source,fixture.Snapshot,stamp,default,()=>{copied=true;return Task.CompletedTask;},_=>0));Assert.False(copied);Assert.False(File.Exists(fixture.Snapshot));Assert.Equal(bytes,File.ReadAllBytes(fixture.Source));
        var digest=await UploadPreparation.CopyAsync(fixture.Source,fixture.Snapshot,stamp,default,availableBytes:_=>StagingDiskSpace.MarginBytes+(ulong)length);Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)),digest);Assert.Equal(bytes,File.ReadAllBytes(fixture.Snapshot));
    }
    [Fact]
    public async Task DiskQueryRunsOutsideGateAndFailedAttemptDoesNotPublish()
    {
        using var fixture=new Files();File.WriteAllBytes(fixture.Source,[1]);using var gate=new SemaphoreSlim(1,1);var published=false;var cleaned=false;
        await Assert.ThrowsAsync<StagingSpaceException>(()=>UploadPreparation.RunAsync<object>(gate,()=>true,async()=>await UploadPreparation.CopyAsync(fixture.Source,fixture.Snapshot,LocalFileState.Read(fixture.Source),default,availableBytes:_=>{Assert.True(gate.Wait(0));gate.Release();return 0;}),()=>{published=true;return new object();},()=>cleaned=true,default));
        Assert.False(published);Assert.True(cleaned);Assert.False(File.Exists(fixture.Snapshot));Assert.True(gate.Wait(0));gate.Release();
    }
    [Theory]
    [InlineData(0)][InlineData(262145)]
    public async Task RemoteStagingChecksBeforeNetworkOrFileAndRetriesWithVersionVerification(int length)
    {
        using var files=new Files();await using var fixture=new S3Fixture();var bytes=new byte[length];fixture.Put("remote/staged",bytes);await using var source=fixture.Source();var metadata=await source.GetMetadataAsync("staged");var count=fixture.Server.Requests.Count;
        await Assert.ThrowsAsync<StagingSpaceException>(()=>RemoteStaging.DownloadAsync(source,metadata,files.Snapshot,default,availableBytes:_=>0));Assert.Equal(count,fixture.Server.Requests.Count);Assert.False(File.Exists(files.Snapshot));
        var digest=await RemoteStaging.DownloadAsync(source,metadata,files.Snapshot,default,availableBytes:_=>StagingDiskSpace.MarginBytes+(ulong)length);Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)),digest);Assert.Equal(bytes,File.ReadAllBytes(files.Snapshot));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task RemoteVersionChangesOrCancellationCannotReturnVerifiedSnapshot(bool cancel)
    {
        using var files=new Files();await using var fixture=new S3Fixture();fixture.Put("remote/staged",[1,2,3]);await using var source=fixture.Source();var metadata=await source.GetMetadataAsync("staged");using var stop=new CancellationTokenSource();
        var task=RemoteStaging.DownloadAsync(source,metadata,files.Snapshot,stop.Token,_=>{if(cancel)stop.Cancel();else fixture.Put("remote/staged",[9]);},_=>ulong.MaxValue);
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);else await Assert.ThrowsAsync<SourceConflictException>(()=>task);
        using var closed=new FileStream(files.Snapshot,FileMode.Open,FileAccess.ReadWrite,FileShare.None);Assert.True(closed.Length>0);
    }
    [Fact]
    public async Task CanceledRemoteStagingNeverQueriesOrCreatesOutput()
    {
        using var files=new Files();await using var fixture=new S3Fixture();await using var source=fixture.Source();using var stop=new CancellationTokenSource();stop.Cancel();var metadata=new SourceEntry("","file",false,1,DateTimeOffset.UtcNow,"version");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>RemoteStaging.DownloadAsync(source,metadata,files.Snapshot,stop.Token,availableBytes:_=>throw new InvalidOperationException()));Assert.False(File.Exists(files.Snapshot));Assert.Empty(fixture.Server.Requests);
    }
    [Fact]
    public void UploadWaitingBacksOffAcrossSerializationAndAllowsExplicitRetry()
    {
        var now=DateTimeOffset.UtcNow;var original=Item();var waiting=StagingDiskSpace.Wait(original,new("磁盘空间不足"),now);waiting=JsonSerializer.Deserialize<LocalUpload>(JsonSerializer.Serialize(waiting))!;
        Assert.Equal("Waiting",waiting.State);Assert.Equal(original.SnapshotPath,waiting.SnapshotPath);Assert.False(StagingDiskSpace.CanRecheck(waiting,now));Assert.False(StagingDiskSpace.CanRecheck(waiting,now.AddSeconds(59)));Assert.True(StagingDiskSpace.CanRecheck(waiting,now.AddMinutes(1)));Assert.True(StagingDiskSpace.CanRecheck(waiting with{LastCheckedUtc=null},now));Assert.True(StagingDiskSpace.CanRecheck(waiting with{State="Queued"},now));Assert.True(StagingDiskSpace.CanRecheck(waiting with{Error="文件占用"},now));Assert.Equal("Canceled",StagingDiskSpace.Wait(original with{CancelRequested=true},new("不足"),now).State);
    }
    [Fact]
    public void RemoteWaitingCoalescesWakesButDoesNotDelayOtherVersionsOrExplicitRetry()
    {
        var now=DateTimeOffset.UtcNow;var retries=new StagingRetrySchedule();var pending=new RemotePendingChange("one",Guid.NewGuid(),"scope","file",null,State:"Waiting",Error:StagingDiskSpace.ErrorPrefix+"磁盘空间不足");retries.Defer(pending.Id,now);
        for(var i=0;i<100;i++)Assert.True(retries.ShouldWait(pending,now.AddMilliseconds(i)));Assert.False(retries.ShouldWait(pending,now.AddMinutes(1)));Assert.False(retries.ShouldWait(pending with{Id="other-version"},now));Assert.False(retries.ShouldWait(pending with{State="Pending",Error=null},now));Assert.False(retries.ShouldWait(pending,now));retries.Defer(pending.Id,now);retries.Reset(pending.Id);Assert.False(retries.ShouldWait(pending,now));
    }
    [Fact]
    public async Task RemoteStagingPreservesSingleSequentialTransfer()
    {
        using var files=new Files();await using var fixture=new S3Fixture();fixture.Put("remote/staged",new byte[1024*1024]);await using var source=fixture.Source();var metadata=await source.GetMetadataAsync("staged");
        await RemoteStaging.DownloadAsync(source,metadata,files.Snapshot,default,availableBytes:_=>ulong.MaxValue);
        Assert.Single(fixture.Server.Requests,request=>request.Method=="GET");Assert.Equal(metadata.Size,new FileInfo(files.Snapshot).Length);
    }
    [Fact]
    public void SpaceWaitingUsesPersistedSchedulerDeadlineWithoutDelayingFullScan()
    {
        var now=DateTimeOffset.UtcNow;var waiting=StagingDiskSpace.Wait(Item(),new("不足"),now);waiting=JsonSerializer.Deserialize<LocalUpload>(JsonSerializer.Serialize(waiting))!;
        Assert.Equal(now.AddMinutes(1),waiting.NextAttemptUtc);Assert.Null(UploadRules.NextUpload([waiting],now.AddSeconds(59)));
        Assert.Equal(waiting,UploadRules.NextUpload([waiting],now.AddMinutes(1)));Assert.Equal(TimeSpan.FromSeconds(15),UploadRules.UploadWait([waiting],now,now.AddSeconds(15)));
        Assert.Equal(waiting with{State="Queued",LastCheckedUtc=null,NextAttemptUtc=null},UploadRules.NextUpload([waiting with{State="Queued",LastCheckedUtc=null,NextAttemptUtc=null}],now.AddSeconds(3)));
    }
    /// <summary>预先取消必须先于磁盘空间查询，不能因空间不足变为等待或新建暂存文件。</summary>
    [Fact]
    public async Task CanceledSnapshotNeverQueriesSpaceOrCreatesOutput()
    {
        using var fixture=new Files();File.WriteAllBytes(fixture.Source,[1]);var stamp=LocalFileState.Read(fixture.Source);using var stop=new CancellationTokenSource();stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>UploadPreparation.CopyAsync(fixture.Source,fixture.Snapshot,stamp,stop.Token,availableBytes:_=>throw new InvalidOperationException("不得查盘")));
        Assert.False(File.Exists(fixture.Snapshot));Assert.Equal(new byte[]{1},File.ReadAllBytes(fixture.Source));
    }
    private static LocalUpload Item()=>new("id",Guid.NewGuid(),"file","file",false,"stamp",1,"Queued",DateTimeOffset.UtcNow,SnapshotPath:"prior.snapshot",SnapshotSha256:"keep");
    private sealed class Files:IDisposable
    {
        internal string Area {get;}=Directory.CreateTempSubdirectory("HybridMount.StagingSpace.").FullName;
        internal string Source=>Path.Combine(Area,"source");
        internal string Snapshot=>Path.Combine(Area,"file.snapshot");
        public void Dispose()=>Directory.Delete(Area,true);
    }
}
