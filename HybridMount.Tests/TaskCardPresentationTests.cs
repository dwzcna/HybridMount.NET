using System.Diagnostics;
using System.Globalization;
using HybridMount.Models.Mounting;
namespace HybridMount.Tests;

/// <summary>验证真实计数、重试与分类语义；WinUI排版和主题仍需要界面验收。</summary>
public sealed class TaskCardPresentationTests
{
    [Fact]
    public void CardKeepsRelativeDirectoryAndHidesNonFileByteTotals()
    {
        var row=new MountOperationSnapshot("stable-id","upload","Sending",0,1,[],null,ItemPath:"a/b.xlsx",BytesTransferred:1000,TotalBytes:2000,IsDirectory:false){MountName="WebDAV 挂载",Priority=MountingPriority.User};
        Assert.Equal(@"a\b.xlsx",row.CardPath);Assert.Contains("P1",row.CardSchedule);Assert.Contains("kB",row.CardSchedule);Assert.Contains("stable-id",row.IssueDetails);
        Assert.Equal(@"a\b.xlsx\",(row with{IsDirectory=true}).CardPath);Assert.DoesNotContain("kB",(row with{IsDirectory=true}).CardSchedule);
        Assert.DoesNotContain("kB",(row with{Operation="remoteUpdate"}).CardSchedule);
    }
    [Fact]
    public void StructuredConflictWinsAndOtherProblemsRemainErrors()
    {
        var row=new MountOperationSnapshot("x","upload","Sending",0,1,[],null);Assert.Equal(TaskIssueKind.Normal,row.IssueKind);
        Assert.Equal(TaskIssueKind.Error,(row with{State="Failed"}).IssueKind);
        Assert.Equal(TaskIssueKind.Conflict,(row with{State="Failed",ConflictPending=true}).IssueKind);
        Assert.Equal(TaskIssueKind.Conflict,(row with{State="Conflict",Error="failed"}).IssueKind);
    }
    [Fact]
    public void RateUsesMonotonicByteDeltaAndResetsForWaitRetryPathAndCompletion()
    {
        var rates=new TaskTransferRate();var start=Stopwatch.GetTimestamp();var second=Stopwatch.Frequency;
        var row=new MountOperationSnapshot("x","upload","Sending",0,1,[],null,ItemPath:"one",IsDirectory:false);
        Assert.Equal(0,rates.Apply(row,start).BytesPerSecond);row=row with{BytesTransferred=1500};
        Assert.Equal(1500,rates.Apply(row,start+second).BytesPerSecond);
        Assert.Equal(0,rates.Apply(row,start+4*second).BytesPerSecond);
        Assert.Equal(0,rates.Apply(row with{BytesTransferred=0},start+5*second).BytesPerSecond);
        Assert.Equal(0,rates.Apply(row with{ItemPath="two"},start+6*second).BytesPerSecond);
        Assert.Equal(0,rates.Apply(row with{WaitingReason="wait"},start+7*second).BytesPerSecond);
        Assert.Equal(0,rates.Apply(row with{State="Completed"},start+8*second).BytesPerSecond);
    }
    [Fact]
    public void SpeedHasOneDecimalAndNoInventedInvalidSamples()
    {
        var before=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;Assert.Equal("1.5kB/s",TaskByteFormat.Speed(1500));Assert.Equal("",TaskByteFormat.Speed(double.NaN));}finally{CultureInfo.CurrentCulture=before;}
    }
    [Fact]
    public async Task DownloadPresentationLeasesAreBoundedAndReleasedIndependently()
    {
        await using var queue=new TasksQueueService();var leases=Enumerable.Range(0,128).Select(i=>queue.BeginDownloadProgress(i.ToString(),"file",100)!).ToArray();
        Assert.All(leases,Assert.NotNull);Assert.Null(queue.BeginDownloadProgress("full","file",100));
        leases[0].Advance(50);Assert.Equal(50,leases[0].Bytes);leases[0].Dispose();leases[0].Dispose();leases[0].Advance(10);Assert.Equal(50,leases[0].Bytes);
        using var next=queue.BeginDownloadProgress("next","file",100);Assert.NotNull(next);foreach(var lease in leases)lease.Dispose();
    }
}
