using System.ComponentModel;
using System.Reflection;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>隔离回归一次性戳过滤边界：外部编辑、删除、重命名、过期、读取失败和停机会话仍保留正常处理。</summary>
public sealed class LocalUploadSelfEventTests
{
    private static readonly DateTimeOffset Now=new(2026,11,6,0,0,0,TimeSpan.Zero);
    private static readonly string Root=Path.GetFullPath("self-event-test-root");
    private static readonly LocalFileStamp Stamp=new("file-id",42,64,100,200,false,0x80000000);
    private static FileSystemEventArgs Change(string path,WatcherChangeTypes kind=WatcherChangeTypes.Changed)=>new(kind,Root,path.Replace('/',Path.DirectorySeparatorChar));
    private static LocalUploadScanQueue Ready(int capacity=4096)
    {
        var queue=new LocalUploadScanQueue(capacity);var startup=queue.Take(Now)!;queue.Completed(startup,Now);return queue;
    }
    [Theory]
    [InlineData(WatcherChangeTypes.Changed,0)][InlineData(WatcherChangeTypes.Changed,64)][InlineData(WatcherChangeTypes.Created,0)][InlineData(WatcherChangeTypes.Created,64)]
    public void MatchingStampSuppressesOnlyFileScopeAndIsConsumedOnce(WatcherChangeTypes kind,int length)
    {
        var stamp=Stamp with{Length=length};var queue=Ready();queue.Writes.Record("dir/file",stamp,Now);queue.Record(Root,Change("dir/file",kind),Now);
        var reads=0;var batch=queue.Take(Now.AddMilliseconds(300),true,path=>{Assert.Equal("dir/file",path);reads++;return stamp;})!;
        Assert.False(batch.IsFull);Assert.False(batch.Covers("dir/file"));Assert.Contains(new LocalUploadScanScope("dir",false),batch.Scopes);Assert.Equal(1,reads);
        queue.Record(Root,Change("dir/file"),Now.AddMilliseconds(400));batch=queue.Take(Now.AddMilliseconds(700),true,_=>{reads++;return stamp;})!;
        Assert.True(batch.Covers("dir/file"));Assert.Equal(1,reads);
    }
    [Theory]
    [InlineData("identity")][InlineData("usn")][InlineData("length")][InlineData("write")][InlineData("change")][InlineData("directory")][InlineData("reasons")]
    public void AnyDifferentStampRetainsUserChangeAndConsumesRecord(string field)
    {
        var current=field switch
        {
            "identity"=>Stamp with{Identity="replacement"},"usn"=>Stamp with{Usn=43},"length"=>Stamp with{Length=65},
            "write"=>Stamp with{LastWriteTime=101},"change"=>Stamp with{ChangeTime=201},"directory"=>Stamp with{IsDirectory=true},_=>Stamp with{Reasons=0x80000001}
        };
        var queue=Ready();queue.Writes.Record("dir/file",Stamp,Now);queue.Record(Root,Change("dir/file"),Now);
        Assert.True(queue.Take(Now.AddMilliseconds(300),true,_=>current)!.Covers("dir/file"));
        queue.Record(Root,Change("dir/file"),Now.AddMilliseconds(400));Assert.True(queue.Take(Now.AddMilliseconds(700),true,_=>Stamp)!.Covers("dir/file"));
    }
    [Theory]
    [InlineData(2000)][InlineData(2500)]
    public void ExpiredRecordDoesNotReadOrSuppress(int elapsed)
    {
        var queue=Ready();queue.Writes.Record("file",Stamp,Now);queue.Record(Root,Change("file"),Now);
        Assert.True(queue.Take(Now.AddMilliseconds(elapsed),true,_=>throw new InvalidOperationException("expired record read"))!.Covers("file"));
    }
    [Theory]
    [InlineData("missing")][InlineData("denied")][InlineData("sharing")][InlineData("io")]
    public void MissingOrUnreadableFileUsesNormalHandling(string error)
    {
        var queue=Ready();queue.Writes.Record("file",Stamp,Now);queue.Record(Root,Change("file"),Now);
        LocalFileStamp Read(string _)
        {
            Exception failure=error switch{"missing"=>new Win32Exception(2),"sharing"=>new Win32Exception(32),"denied"=>new UnauthorizedAccessException(),_=>new IOException()};throw failure;
        }
        Assert.True(queue.Take(Now.AddMilliseconds(300),true,Read)!.Covers("file"));
    }
    [Fact]
    public void RenameAlwaysKeepsBothSubtreesAndInvalidatesBothRecords()
    {
        var queue=Ready();queue.Writes.Record("old/file",Stamp,Now);queue.Writes.Record("new/file",Stamp,Now);
        queue.Record(Root,Change("old/file"),Now);queue.Record(Root,new RenamedEventArgs(WatcherChangeTypes.Renamed,Root,"new/file","old/file"),Now);
        var batch=queue.Take(Now.AddMilliseconds(300),true,_=>throw new InvalidOperationException("rename read"))!;
        Assert.True(batch.Covers("old/file/child"));Assert.True(batch.Covers("new/file/child"));Assert.True(batch.Covers("old"));Assert.True(batch.Covers("new"));
        foreach(var path in new[]{"old/file","new/file"})queue.Record(Root,Change(path),Now.AddMilliseconds(400));
        batch=queue.Take(Now.AddMilliseconds(700),true,_=>Stamp)!;Assert.True(batch.Covers("old/file"));Assert.True(batch.Covers("new/file"));
    }
    [Theory]
    [InlineData(WatcherChangeTypes.Deleted)][InlineData(WatcherChangeTypes.Renamed)]
    public void LateOwnCompletionCannotSuppressQueuedDeletionOrRename(WatcherChangeTypes kind)
    {
        var queue=Ready();queue.Record(Root,kind==WatcherChangeTypes.Renamed?new RenamedEventArgs(kind,Root,"file","before"):Change("file",kind),Now);
        queue.Writes.Record("file",Stamp,Now.AddMilliseconds(100));queue.Record(Root,Change("file"),Now.AddMilliseconds(100));
        Assert.True(queue.Take(Now.AddMilliseconds(400),true,_=>throw new InvalidOperationException("unsafe event read"))!.Covers("file"));
    }
    [Fact]
    public void CaseOnlyRenameStillRequiresFullScan()
    {
        var queue=Ready();queue.Writes.Record("File",Stamp,Now);queue.Record(Root,new RenamedEventArgs(WatcherChangeTypes.Renamed,Root,"File","file"),Now);
        Assert.True(queue.Take(Now,true,_=>Stamp)!.IsFull);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public void DirectoryAndUnjournaledStampsAreNeverSuppressed(bool directory)
    {
        var stamp=directory?Stamp with{IsDirectory=true}:Stamp with{Usn=0};var queue=Ready();queue.Writes.Record("dir",stamp,Now);queue.Record(Root,Change("dir"),Now);
        Assert.True(queue.Take(Now.AddMilliseconds(300),true,_=>throw new InvalidOperationException("unsafe stamp read"))!.Covers("dir/child"));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void OverflowAndSaturationKeepFullScanAndForgetExpectedStamps(bool saturation)
    {
        var queue=Ready(2);queue.Writes.Record("dir/file",Stamp,Now);queue.Record(Root,Change("dir/file"),Now);
        if(saturation)queue.Record(Root,Change("dir/another"),Now);else queue.RequestFull();
        var batch=queue.Take(Now,true,_=>Stamp)!;Assert.True(batch.IsFull);queue.Completed(batch,Now);
        queue.Record(Root,Change("dir/file"),Now);Assert.True(queue.Take(Now.AddMilliseconds(300),true,_=>Stamp)!.Covers("dir/file"));
    }
    [Fact]
    public void SuppressionDoesNotPostponePeriodicFullOrCancelOutOfScopeUploads()
    {
        var queue=Ready();queue.Writes.Record("dir/file",Stamp,Now.AddSeconds(59));queue.Record(Root,Change("dir/file"),Now.AddSeconds(59));
        var batch=queue.Take(Now.AddSeconds(59.3),true,_=>Stamp)!;queue.Completed(batch,Now.AddSeconds(59.3));
        var upload=new LocalUpload("id",Guid.NewGuid(),"dir/file","dir/file",false,Stamp.Key,1,"Queued",Now);
        Assert.Empty(batch.MissingCandidates([upload],new HashSet<string>()));Assert.True(queue.Take(Now.AddSeconds(60),true,_=>Stamp)!.IsFull);
    }
    [Fact]
    public void CacheIsBoundedOrdinalAndSessionLocal()
    {
        var cache=new LocalWriteStampCache(2);cache.Record("first",Stamp,Now);cache.Record("second",Stamp,Now);cache.Record("third",Stamp,Now);
        Assert.False(cache.Consume("first",Now,()=>Stamp));Assert.False(cache.Consume("second",Now,()=>Stamp));Assert.False(cache.Consume("Third",Now,()=>Stamp));Assert.True(cache.Consume("third",Now,()=>Stamp));
        cache.Record("file",Stamp,Now);Assert.False(new LocalWriteStampCache().Consume("file",Now,()=>Stamp));
    }
    [Fact]
    public void CompletionRecordedDuringComparisonSurvivesOldRecordConsumption()
    {
        var cache=new LocalWriteStampCache();cache.Record("file",Stamp,Now);var next=Stamp with{Usn=43};
        Assert.True(cache.Consume("file",Now,()=>{cache.Record("file",next,Now);return Stamp;}));Assert.True(cache.Consume("file",Now,()=>next));Assert.False(cache.Consume("file",Now,()=>next));
    }
    [Fact(Timeout=10000)]
    public async Task EventArrivingDuringDequeueIsScannedNextTime()
    {
        var queue=Ready();queue.Writes.Record("file",Stamp,Now);queue.Record(Root,Change("file"),Now);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var recording=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var release=new ManualResetEventSlim();
        var take=Task.Run(()=>queue.Take(Now.AddMilliseconds(300),true,_=>{entered.SetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));return Stamp;}));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var user=Task.Run(()=>{recording.SetResult();queue.Record(Root,Change("file"),Now.AddMilliseconds(400));});
        try{await recording.Task.WaitAsync(TimeSpan.FromSeconds(5));}finally{release.Set();}
        Assert.False((await take)!.Covers("file"));await user;
        Assert.True(queue.Take(Now.AddMilliseconds(700),true,_=>throw new InvalidOperationException("record reused"))!.Covers("file"));
    }
    [Fact]
    public void ActualUserEditAfterExpectedStampIsNotSuppressed()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.SelfEventTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        try
        {
            var path=Path.Combine(area,"file");File.WriteAllText(path,"before");var stamp=LocalWriteStampCache.Read(path);var queue=Ready();queue.Writes.Record("file",stamp,Now);
            File.WriteAllText(path,"after user edit");queue.Record(area,new FileSystemEventArgs(WatcherChangeTypes.Changed,area,"file"),Now);
            Assert.True(queue.Take(Now.AddMilliseconds(300),true,_=>LocalWriteStampCache.Read(path))!.Covers("file"));
        }
        finally{Directory.Delete(area,true);}
    }
    [Fact]
    public void ActiveWriterWithoutDeleteSharingIsAllowedAndLaterChangeScans()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.SelfEventTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        try
        {
            var path=Path.Combine(area,"file");File.WriteAllText(path,"before");var stamp=LocalWriteStampCache.Read(path);var queue=Ready();queue.Writes.Record("file",stamp,Now);
            using var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite);
            Assert.Equal(stamp,LocalWriteStampCache.Read(path));
            writer.Write(new byte[128]);writer.Flush();
            queue.Record(area,new FileSystemEventArgs(WatcherChangeTypes.Changed,area,"file"),Now);
            Assert.NotEqual(stamp,LocalWriteStampCache.Read(path));
            Assert.True(queue.Take(Now.AddMilliseconds(300),true,_=>LocalWriteStampCache.Read(path))!.Covers("file"));
        }
        finally{Directory.Delete(area,true);}
    }
    [Fact]
    public async Task SessionCompletionIsFileOnlyAndStoppedOrDisposedObserversCannotSuppress()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.SelfEventTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        try
        {
            var repository=new MountRepository(Path.Combine(area,"data"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));
            await using var logger=new LoggerService(Path.Combine(area,"logs.db"));
            await using var session=new CloudMountSession(new(Guid.NewGuid(),"self-event",area),null,repository,logger);
            var queue=Ready();var calls=0;Action<string,LocalFileStamp> observed=(path,stamp)=>{calls++;queue.Writes.Record(path,stamp,Now);};
            var type=typeof(MountManager).GetNestedType("UploadRuntime",BindingFlags.NonPublic)!;
            using var runtime=(IDisposable)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[CancellationToken.None],null)!;
            type.GetField("Session")!.SetValue(runtime,session);type.GetField("LocalWriteCompleted")!.SetValue(runtime,observed);session.LocalWriteCompleted+=observed;
            var entry=new MountEntry("id","dir/file","dir/file","v1",false);session.RecordLocalWrite(entry,Stamp);Assert.Equal(1,calls);
            queue.Record(Root,Change("dir/file"),Now);Assert.False(queue.Take(Now.AddMilliseconds(300),true,_=>Stamp)!.Covers("dir/file"));
            session.RecordLocalWrite(entry with{IsDirectory=true},Stamp with{IsDirectory=true});Assert.Equal(1,calls);
            runtime.Dispose();session.RecordLocalWrite(entry,Stamp);Assert.Equal(1,calls);
            session.LocalWriteCompleted+=observed;await session.RequestStop();session.RecordLocalWrite(entry,Stamp);Assert.Equal(1,calls);session.LocalWriteCompleted-=observed;
        }
        finally{Directory.Delete(area,true);}
    }
}
