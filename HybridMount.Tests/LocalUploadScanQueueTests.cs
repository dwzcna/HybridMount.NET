using System.Reflection;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class LocalUploadScanQueueTests
{
    private static readonly DateTimeOffset Now=new(2026,11,6,0,0,0,TimeSpan.Zero);
    private static readonly string Root=Path.GetFullPath("scan-queue-test-root");
    private static FileSystemEventArgs Change(string path,WatcherChangeTypes kind=WatcherChangeTypes.Changed)=>new(kind,Root,path.Replace('/',Path.DirectorySeparatorChar));
    private static LocalUploadScanQueue Ready(int capacity=4096)
    {
        var queue=new LocalUploadScanQueue(capacity);var startup=queue.Take(Now);Assert.NotNull(startup);Assert.True(startup.IsFull);queue.Completed(startup,Now);return queue;
    }
    [Fact]
    public void StartupAndPathlessRequestRequireFullScan()
    {
        var queue=Ready();Assert.Null(queue.Take(Now.AddSeconds(1)));var requested=queue.Take(Now.AddSeconds(1),true);Assert.NotNull(requested);Assert.True(requested.IsFull);
    }
    [Fact]
    public void DuplicateEventsNormalizeAndDebounceWithoutExpandingParentSubtree()
    {
        var queue=Ready();queue.Record(Root,Change("dir/file"),Now);queue.Record(Root,Change("dir/./file"),Now.AddMilliseconds(100));
        Assert.Null(queue.Take(Now.AddMilliseconds(299),true));var batch=queue.Take(Now.AddMilliseconds(300));Assert.NotNull(batch);Assert.False(batch.IsFull);Assert.Equal(2,batch.Scopes.Count);
        Assert.Contains(new LocalUploadScanScope("dir/file",true),batch.Scopes);Assert.Contains(new LocalUploadScanScope("dir",false),batch.Scopes);Assert.True(batch.Covers("dir/file"));Assert.False(batch.Covers("dir/sibling"));Assert.Null(queue.Take(Now.AddSeconds(1)));
    }
    [Fact]
    public void ContinuousEventsHaveBoundedDebounce()
    {
        var queue=Ready();for(var index=0;index<=20;index++){queue.Record(Root,Change("file"),Now.AddMilliseconds(index*100));if(index<20)Assert.Null(queue.Take(Now.AddMilliseconds(index*100)));}
        Assert.NotNull(queue.Take(Now.AddSeconds(2)));
    }
    [Fact]
    public void RenameQueuesBothNamesTheirParentsAndSubtrees()
    {
        var queue=Ready();queue.Record(Root,new RenamedEventArgs(WatcherChangeTypes.Renamed,Root,"new/location","old/location"),Now);
        var batch=queue.Take(Now.AddSeconds(1));Assert.NotNull(batch);Assert.False(batch.IsFull);
        Assert.Contains(new LocalUploadScanScope("old",false),batch.Scopes);Assert.Contains(new LocalUploadScanScope("new",false),batch.Scopes);
        Assert.True(batch.Covers("old/location/child"));Assert.True(batch.Covers("new/location/child"));Assert.False(batch.Covers("old/location-other"));Assert.False(batch.Covers("new/sibling"));
    }
    [Fact]
    public void CaseOnlyRenameUsesFullScanForCanonicalDiskNames()
    {
        var queue=Ready();queue.Record(Root,new RenamedEventArgs(WatcherChangeTypes.Renamed,Root,"File","file"),Now);Assert.True(queue.Take(Now)!.IsFull);
    }
    [Theory]
    [InlineData(WatcherChangeTypes.Created)][InlineData(WatcherChangeTypes.Deleted)][InlineData(WatcherChangeTypes.Changed)]
    public void DirectoryEventsCoverOnlyTheirSubtree(WatcherChangeTypes kind)
    {
        var queue=Ready();queue.Record(Root,Change("parent/dir",kind),Now);var batch=queue.Take(Now.AddSeconds(1))!;
        Assert.True(batch.Covers("parent/dir"));Assert.True(batch.Covers("parent/dir/deep/file"));Assert.True(batch.Covers("parent"));Assert.False(batch.Covers("parent/sibling"));Assert.False(batch.Covers("parent/dir-other"));
    }
    [Fact]
    public void RecursiveAncestorCoalescesDescendantsButMetadataParentDoesNot()
    {
        var queue=Ready();queue.Record(Root,Change("dir/child/file"),Now);queue.Record(Root,Change("dir"),Now);var batch=queue.Take(Now.AddSeconds(1))!;
        Assert.Equal(2,batch.Scopes.Count);Assert.Contains(new LocalUploadScanScope("dir",true),batch.Scopes);Assert.True(batch.Covers("dir/anything"));Assert.False(batch.Covers("other"));
    }
    [Fact]
    public void SaturationPromotesToFullWhileDuplicateStormStaysIncremental()
    {
        var queue=Ready(3);for(var index=0;index<10000;index++)queue.Record(Root,Change("one"),Now);Assert.False(queue.Take(Now.AddSeconds(1))!.IsFull);
        queue.Record(Root,Change("one"),Now.AddSeconds(1));queue.Record(Root,Change("two"),Now.AddSeconds(1));queue.Record(Root,Change("three"),Now.AddSeconds(1));Assert.True(queue.Take(Now.AddSeconds(1))!.IsFull);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void WatcherErrorAndOverflowForceImmediateFullScan(bool overflow)
    {
        var queue=Ready();using var watcher=new FileSystemWatcher();var signals=0;
        MountManager.BindUploadWatcher(watcher,()=>signals++,change=>queue.Record(Root,change,Now),queue.RequestFull);
        Raise(watcher,"OnChanged",Change("file"));Assert.Equal(1,signals);Assert.Null(queue.Take(Now,true));
        Raise(watcher,"OnError",new ErrorEventArgs(overflow?new InternalBufferOverflowException():new IOException("watcher failed")));Assert.Equal(2,signals);Assert.True(queue.Take(Now,true)!.IsFull);
    }
    [Fact]
    public void IncrementalActivityCannotPostponePeriodicFullScan()
    {
        var queue=Ready();for(var second=1;second<60;second++){queue.Record(Root,Change("file"),Now.AddSeconds(second));var batch=queue.Take(Now.AddSeconds(second).AddMilliseconds(250))!;Assert.False(batch.IsFull);queue.Completed(batch,Now.AddSeconds(second).AddMilliseconds(250));}
        queue.Record(Root,Change("file"),Now.AddSeconds(60));Assert.True(queue.Take(Now.AddSeconds(60))!.IsFull);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void EventsArrivingDuringScanAreRetainedForNextBatch(bool full)
    {
        var queue=Ready();queue.Record(Root,Change("before"),Now);if(full)queue.RequestFull();var batch=queue.Take(Now.AddSeconds(1))!;
        queue.Record(Root,Change("during"),Now.AddSeconds(1));queue.Completed(batch,Now.AddSeconds(1));var next=queue.Take(Now.AddSeconds(2))!;
        Assert.False(next.IsFull);Assert.True(next.Covers("during"));Assert.False(next.Covers("before"));
    }
    [Fact]
    public void OverflowDuringFullScanIsNotClearedByCompletion()
    {
        var queue=Ready();queue.RequestFull();var batch=queue.Take(Now)!;queue.RequestFull();queue.Completed(batch,Now);Assert.True(queue.Take(Now)!.IsFull);
    }
    [Fact]
    public void ExplicitFullScanResetsClockWithoutDroppingLaterEvents()
    {
        var queue=Ready();queue.Record(Root,Change("old"),Now.AddSeconds(40));var batch=queue.StartFull();queue.Record(Root,Change("new"),Now.AddSeconds(41));queue.Completed(batch,Now.AddSeconds(42));
        Assert.True(queue.Take(Now.AddSeconds(43))!.Covers("new"));Assert.Null(queue.Take(Now.AddSeconds(61)));Assert.True(queue.Take(Now.AddSeconds(102))!.IsFull);
    }
    [Fact]
    public void IncompleteScanSchedulesFullRetryWithoutBusyLoop()
    {
        var queue=Ready();queue.Record(Root,Change("file"),Now);var batch=queue.Take(Now.AddSeconds(1))!;queue.Completed(batch,Now.AddSeconds(1),false);Assert.Null(queue.Take(Now.AddSeconds(2)));Assert.True(queue.Take(Now.AddSeconds(3))!.IsFull);
    }
    [Theory]
    [InlineData("../outside")][InlineData("CON")][InlineData("")]
    public void UnsafeOrRootEventFallsBackToFull(string path)
    {
        var queue=Ready();queue.Record(Root,Change(path),Now);Assert.True(queue.Take(Now)!.IsFull);
    }
    [Fact]
    public void MissingUploadCancellationIsRestrictedToScannedScope()
    {
        var queue=Ready();queue.Record(Root,Change("dir/deleted",WatcherChangeTypes.Deleted),Now);var batch=queue.Take(Now.AddSeconds(1))!;
        LocalUpload Item(string path,string state="Queued",bool copy=false)=>new(path,Guid.NewGuid(),path,path,false,"stamp",1,state,Now,IsCopy:copy);
        var rows=new[]{Item("dir/deleted"),Item("dir/deleted/child"),Item("dir/sibling"),Item("elsewhere/file"),Item("dir/deleted/present"),Item("dir/deleted/completed","Completed"),Item("dir/deleted/canceled","Canceled"),Item("dir/deleted/ignored","Ignored"),Item("dir/deleted/copy",copy:true)}.ToDictionary(item=>item.Id);
        foreach(var item in batch.MissingCandidates(rows.Values,new HashSet<string>(StringComparer.Ordinal){"dir/deleted/present"}).ToArray())rows[item.Id]=item with{State="Canceled"};
        Assert.Equal("Canceled",rows["dir/deleted"].State);Assert.Equal("Canceled",rows["dir/deleted/child"].State);Assert.Equal("Queued",rows["dir/sibling"].State);Assert.Equal("Queued",rows["elsewhere/file"].State);Assert.Equal("Queued",rows["dir/deleted/present"].State);Assert.Equal("Queued",rows["dir/deleted/copy"].State);
    }
    [Fact]
    public void ScopeMatchingIsOrdinalAndRootMetadataDoesNotCoverSiblings()
    {
        var queue=Ready();queue.Record(Root,Change("File"),Now);var batch=queue.Take(Now.AddSeconds(1))!;Assert.True(batch.Covers("File"));Assert.False(batch.Covers("file"));Assert.False(batch.Covers("sibling"));
        Assert.Single(LocalUploadScanBatch.Full.MissingCandidates([new("id",Guid.NewGuid(),"any","any",false,"s",1,"Queued",Now)],new HashSet<string>()));
    }
    /// <summary>只有一次通知时，事件驱动等待必须自行在去抖截止唤醒，不依赖后续事件。</summary>
    [Fact(Timeout=5000)]
    public async Task SingleEventIsScannedAtDebounceDeadlineWithoutAnotherSignal()
    {
        var queue=Ready();using var signal=new SemaphoreSlim(1,1);queue.Record(Root,Change("single"),Now);await signal.WaitAsync();
        Assert.Null(queue.Take(Now,true));var due=queue.NextDueUtc(Now);Assert.Equal(Now.AddMilliseconds(200),due);
        var timeout=UploadRules.UploadWait([],Now,due);Assert.Equal(TimeSpan.FromMilliseconds(200),timeout);
        Assert.False(await signal.WaitAsync(timeout));var batch=queue.Take(due);Assert.NotNull(batch);Assert.True(batch.Covers("single"));Assert.False(batch.IsFull);
    }
    [Fact]
    public void DeadlineIncludesFailureRetryAndImmediateFullRequest()
    {
        var queue=Ready();queue.Completed(LocalUploadScanBatch.Full,Now,false);Assert.Equal(Now.AddSeconds(2),queue.NextDueUtc(Now));
        Assert.Null(queue.Take(Now.AddSeconds(1)));queue.RequestFull();Assert.Equal(Now,queue.NextDueUtc(Now));Assert.True(queue.Take(Now)!.IsFull);
    }
    [Fact]
    public void DeadlineRespectsPeriodicScanAndMaximumCoalescing()
    {
        var queue=Ready();queue.Record(Root,Change("late"),Now.AddSeconds(59.9));Assert.Equal(Now.AddSeconds(60),queue.NextDueUtc(Now.AddSeconds(59.9)));
        var continuous=Ready();for(var index=0;index<20;index++)continuous.Record(Root,Change("file"),Now.AddMilliseconds(index*100));
        Assert.Equal(Now.AddSeconds(2),continuous.NextDueUtc(Now.AddMilliseconds(1900)));
    }
    private static void Raise(FileSystemWatcher watcher,string method,EventArgs args)=>typeof(FileSystemWatcher).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(watcher,[args]);
}
