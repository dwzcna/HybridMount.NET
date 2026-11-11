using System.Net;
using System.Text;
using HybridMount.Models.Mounting.Synology;
namespace HybridMount.Tests;

/// <summary>群晖整文件校验屏障、共享取消、有界公平调度和缓存命中；仅使用隔离HTTP夹具及临时目录。</summary>
public sealed class SynologySnapshotSchedulingTests:IDisposable
{
    private readonly string _area=Path.Combine(Path.GetTempPath(),"HybridMount.Snapshot63",Guid.NewGuid().ToString("N"));
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SynologyFileSource Source(SynologyFakeDsm server,WholeFileCache? cache=null)=>new(SynologyTests.Options,new("user","password"),cache??new(_area),server,null);
    public void Dispose(){if(Directory.Exists(_area))Directory.Delete(_area,true);}
    private static async Task Until(Func<bool> condition){var deadline=DateTime.UtcNow.AddSeconds(8);while(!condition()&&DateTime.UtcNow<deadline)await Task.Delay(5);Assert.True(condition());}
    private void NoPublishedFiles(){Assert.Empty(Directory.Exists(_area)?Directory.GetFiles(_area,"*.cache"):[]);Assert.Empty(Directory.Exists(_area)?Directory.GetFiles(_area,"*.tmp"):[]);}

    [Fact(Timeout=20000)]
    public async Task SharedDownloadReturnsNoBytesUntilFinalMetadataAndDigestAndOneWaiterMayCancel()
    {
        var body=new HeldBody();var finalStarted=Signal();var finalRelease=Signal();var downloads=0;
        var server=new SynologyFakeDsm();server.Intercept=async(request,fields,token)=>
        {
            if(fields["api"]=="SYNO.FileStation.Download"){Interlocked.Increment(ref downloads);return new(HttpStatusCode.OK){Content=new StreamContent(body)};}
            if(fields["api"]=="SYNO.FileStation.List"&&fields["method"]=="getinfo"&&body.Ended){finalStarted.TrySetResult();await finalRelease.Task.WaitAsync(token);}
            return null;
        };
        await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");using var cancel=new CancellationTokenSource();
        var a=Enumerable.Repeat((byte)0xCC,8).ToArray();var b=Enumerable.Repeat((byte)0xCC,8).ToArray();
        var first=source.ReadRangeAsync(entry,0,a,cancel.Token).AsTask();await body.Started.Task;
        var second=source.ReadRangeAsync(entry,0,b).AsTask();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
        Assert.All(a,value=>Assert.Equal((byte)0xCC,value));Assert.All(b,value=>Assert.Equal((byte)0xCC,value));
        body.Release.TrySetResult();await finalStarted.Task;Assert.False(second.IsCompleted);Assert.All(b,value=>Assert.Equal((byte)0xCC,value));Assert.Empty(Directory.GetFiles(_area,"*.cache"));
        finalRelease.TrySetResult();Assert.Equal(8,await second);Assert.Equal("abcdefgh",Encoding.UTF8.GetString(b));Assert.Equal(1,downloads);
        Assert.Equal(1,source.SnapshotMetrics["downloads"]);Assert.Equal(1,source.SnapshotMetrics["published"]);
        var requests=server.Requests.Count;await source.ReadRangeAsync(entry,0,new byte[1]);Assert.Equal(requests,server.Requests.Count);Assert.True(source.SnapshotMetrics["cacheHits"]>=1);
    }

    [Theory(Timeout=20000)]
    [InlineData("total")][InlineData("idle")][InlineData("last")][InlineData("stop")]
    public async Task DeadlineLastWaiterOrStopOwnsCleanupAndCannotPublish(string reason)
    {
        var body=new HeldBody();var server=new SynologyFakeDsm();server.Intercept=(request,fields,token)=>Task.FromResult<HttpResponseMessage?>(fields["api"]=="SYNO.FileStation.Download"?new(HttpStatusCode.OK){Content=new StreamContent(body)}:null);
        await using var source=Source(server);source.SnapshotTotalDeadline=reason=="total"?TimeSpan.FromMilliseconds(180):TimeSpan.FromSeconds(8);source.SnapshotIdleDeadline=reason=="idle"?TimeSpan.FromMilliseconds(180):TimeSpan.FromSeconds(8);
        var entry=await source.GetMetadataAsync("file.bin");using var cancel=new CancellationTokenSource();var bytes=Enumerable.Repeat((byte)0xCC,8).ToArray();var read=source.ReadRangeAsync(entry,0,bytes,cancel.Token).AsTask();await body.Started.Task;
        if(reason=="last")cancel.Cancel();if(reason=="stop")await source.DisposeAsync();
        var error=await Record.ExceptionAsync(()=>read);Assert.NotNull(error);
        if(reason is "total" or "idle")Assert.Equal(ConnectionFailureKind.Timeout,Assert.IsType<SourceConnectionException>(error).Diagnostic.Kind);else Assert.IsAssignableFrom<OperationCanceledException>(error);
        await source.DisposeAsync();await body.Disposed.Task;Assert.All(bytes,value=>Assert.Equal((byte)0xCC,value));NoPublishedFiles();
        if(reason=="total")Assert.Equal(1,source.SnapshotMetrics["timeouts"]);
    }

    [Fact(Timeout=30000)]
    public async Task DistinctWorkIsBoundedAndCanceledQueuedItemsNeverStartNetwork()
    {
        var body=new HeldBody();var server=new SynologyFakeDsm();for(var i=0;i<65;i++)server.Files["/share/f"+i+".bin"]=Encoding.UTF8.GetBytes("abcdefgh");
        var downloads=0;var block=false;server.Intercept=(request,fields,token)=>
        {if(!block||fields["api"]!="SYNO.FileStation.Download")return Task.FromResult<HttpResponseMessage?>(null);Interlocked.Increment(ref downloads);return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.OK){Content=new StreamContent(body)});};
        var cache=new WholeFileCache(_area);await using var source=Source(server,cache);source.SnapshotTotalDeadline=TimeSpan.FromSeconds(20);var original=await source.GetMetadataAsync("file.bin");await source.ReadRangeAsync(original,0,new byte[8]);block=true;using var cancel=new CancellationTokenSource();
        var reads=new List<Task<int>>();for(var i=0;i<64;i++)reads.Add(source.ReadRangeAsync(original with{Path="f"+i+".bin"},0,new byte[1],cancel.Token).AsTask());
        await body.Started.Task;await Until(()=>cache.VerifiedWorkCount==64);
        Assert.Equal(8,await source.ReadRangeAsync(original,0,new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        var rejected=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ReadRangeAsync(original with{Path="f64.bin"},0,new byte[1]).AsTask());Assert.Equal("快照排队",rejected.Diagnostic.Stage);Assert.Equal(1,downloads);
        cancel.Cancel();foreach(var read in reads)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>read);await source.DisposeAsync();Assert.Equal(0,cache.VerifiedWorkCount);Assert.Equal(1,downloads);Assert.Single(Directory.GetFiles(_area,"*.cache"));Assert.Empty(Directory.GetFiles(_area,"*.tmp"));
    }

    [Fact(Timeout=20000)]
    public async Task PublishedCacheBypassesAnotherHeldDownloadAndDiagnosticQueryDoesNoIo()
    {
        var body=new HeldBody();var server=new SynologyFakeDsm();server.Files["/share/other.bin"]=Encoding.UTF8.GetBytes("abcdefgh");
        server.Intercept=(request,fields,token)=>Task.FromResult<HttpResponseMessage?>(fields["api"]=="SYNO.FileStation.Download"&&fields["path"].Contains("other.bin",StringComparison.Ordinal)?new(HttpStatusCode.OK){Content=new StreamContent(body)}:null);
        await using var source=Source(server);var a=await source.GetMetadataAsync("file.bin");var b=await source.GetMetadataAsync("other.bin");await source.ReadRangeAsync(a,0,new byte[8]);
        using var cancel=new CancellationTokenSource();var pending=source.ReadRangeAsync(b,0,new byte[1],cancel.Token).AsTask();await body.Started.Task;
        var requests=server.Requests.Count;var bytes=new byte[8];Assert.Equal(8,await source.ReadRangeAsync(a,0,bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(1)));Assert.Equal("abcdefgh",Encoding.UTF8.GetString(bytes));
        for(var i=0;i<100;i++){_=source.SnapshotMetrics;_=((IMountSourceRuntime)source).ConnectionDiagnostics;}Assert.Equal(requests,server.Requests.Count);Assert.False(pending.IsCompleted);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
    }

    [Fact(Timeout=20000)]
    public async Task StableIdentitySeparatesOtherwiseEqualSnapshotKeys()
    {
        var server=new SynologyFakeDsm();await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");
        await source.ReadRangeAsync(entry with{StableId="first"},0,new byte[8]);await source.ReadRangeAsync(entry with{StableId="replacement"},0,new byte[8]);Assert.Equal(2,server.Downloads);
        await source.ReadRangeAsync(entry with{StableId="replacement"},0,new byte[1]);Assert.Equal(2,server.Downloads);
    }

    [Fact(Timeout=20000)]
    public async Task HighPriorityWaiterPromotesSharedQueuedSnapshotWithoutDuplicateDownload()
    {
        await using var queue=new TasksQueueService();var binding=queue.StartMount(Guid.NewGuid());var body=new HeldBody();var started=new List<string>();
        var server=new SynologyFakeDsm();foreach(var name in new[]{"low.bin","medium.bin"})server.Files["/share/"+name]=Encoding.UTF8.GetBytes("abcdefgh");
        server.Intercept=(request,fields,token)=>
        {
            if(fields["api"]!="SYNO.FileStation.Download")return Task.FromResult<HttpResponseMessage?>(null);
            lock(started)started.Add(fields["path"]);
            return Task.FromResult<HttpResponseMessage?>(fields["path"].Contains("file.bin",StringComparison.Ordinal)?new(HttpStatusCode.OK){Content=new StreamContent(body)}:null);
        };
        var cache=new WholeFileCache(_area);await using var source=Source(server,cache);((IMountNetworkSource)source).BindQueue(binding);
        var a=await source.GetMetadataAsync("file.bin");var b=await source.GetMetadataAsync("low.bin");var c=await source.GetMetadataAsync("medium.bin");
        Task<int> Read(SourceEntry entry,string id,MountingPriority priority)
        {using var scope=queue.Enter(binding,new(id,binding.MountId,binding.Generation,priority,DateTimeOffset.UtcNow,"download",entry.Path,Version:entry.Version));return source.ReadRangeAsync(entry,0,new byte[8]).AsTask();}
        var first=Read(a,"active",MountingPriority.Demand);await body.Started.Task;
        var low=Read(b,"low",MountingPriority.Observation);var medium=Read(c,"medium",MountingPriority.User);await Until(()=>cache.VerifiedWorkCount==3&&cache.VerifiedQueuedCount==2);
        var high=Read(b,"high",MountingPriority.Demand);body.Release.TrySetResult();await Task.WhenAll(first,low,medium,high);
        Assert.Equal(3,started.Count);Assert.Contains("low.bin",started[1]);Assert.Contains("medium.bin",started[2]);Assert.Equal(3,source.SnapshotMetrics["downloads"]);
    }

    [Fact(Timeout=15000)]
    public async Task FairAdmissionRechecksPriorityAndAgesOldestRequest()
    {
        var queue=new PriorityReadQueue(1);var active=await queue.EnterFairAsync(()=>1,default);var promoted=1;
        var low=queue.EnterFairAsync(()=>promoted,default);var medium=queue.EnterFairAsync(()=>5,default);promoted=9;active.Dispose();var lease=await low;Assert.False(medium.IsCompleted);lease.Dispose();(await medium).Dispose();
        lease=await queue.EnterFairAsync(()=>1,default);var oldest=queue.EnterFairAsync(()=>1,default);var grants=0;
        while(!oldest.IsCompleted&&grants<8){var urgent=queue.EnterFairAsync(()=>10,default);lease.Dispose();if(await Task.WhenAny(oldest,urgent)==oldest){lease=await oldest;lease.Dispose();(await urgent).Dispose();return;}lease=await urgent;grants++;}
        Assert.True(oldest.IsCompleted);lease.Dispose();(await oldest).Dispose();
    }
    /// <summary>先交付四字节，再等待取消或显式放行；EOF与关闭可分别核验。</summary>
    private sealed class HeldBody:MemoryStream
    {
        public readonly TaskCompletionSource Started=Signal(),Release=Signal(),Disposed=Signal();private int _reads;public bool Ended;
        public HeldBody():base(Encoding.UTF8.GetBytes("abcdefgh")){}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {Started.TrySetResult();if(Interlocked.Increment(ref _reads)>=2)await Release.Task.WaitAsync(token);var count=await base.ReadAsync(buffer[..Math.Min(4,buffer.Length)],token);if(count==0)Ended=true;return count;}
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing)Disposed.TrySetResult();}
    }
}
