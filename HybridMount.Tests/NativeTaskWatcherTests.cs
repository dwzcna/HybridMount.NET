using System.Collections.Concurrent;
using System.Threading.Channels;
namespace HybridMount.Tests;

/// <summary>真实ReadDirectoryChangesW／IOCP及独立认证管道；全部使用随机临时根，不涉及正式挂载。</summary>
public sealed class NativeTaskWatcherTests
{
    [Fact(Timeout=60000)]
    public async Task NativeEventsRenameOverflowAndStopAreDeliveredWithStableIdentity()
    {
        using var area=new Area();await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var mount=Guid.NewGuid();var session=Guid.NewGuid().ToString("N");
        var events=Channel.CreateUnbounded<NativeTaskIntent>();var observed=new List<NativeTaskIntent>();
        using var sink=runtime.RegisterTaskSink(mount,session,intent=>events.Writer.TryWrite(intent),_=>{});
        await runtime.InvokeAsync("testTaskWatchAdd",new{mountId=mount,sessionId=session,rootPath=area.Path});
        async Task<NativeTaskIntent> Next(Func<NativeTaskIntent,bool> match)
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(true){var intent=await events.Reader.ReadAsync(deadline.Token);intent.Validate();observed.Add(intent);runtime.SendTaskAcknowledgment(intent,"accepted");runtime.SendTaskResult(new(Guid.NewGuid().ToString("N"),intent.TaskId,mount,session,"test","Completed",true));if(match(intent))return intent;}
        }
        try
        {
            var first=Path.Combine(area.Path,"first.txt");await File.WriteAllTextAsync(first,"created");var created=await Next(x=>x.Path=="first.txt");Assert.Equal(mount,created.MountId);
            await File.AppendAllTextAsync(first,"changed");await Next(x=>x.EventKind=="changed"&&x.Path=="first.txt");
            File.Move(first,Path.Combine(area.Path,"renamed.txt"));var renamed=await Next(x=>x.EventKind=="renamed");Assert.Equal("first.txt",renamed.OldPath);Assert.Equal("renamed.txt",renamed.Path);
            await runtime.InvokeAsync("testTaskWatchOverflow",new{mountId=mount,sessionId=session});await Next(x=>x.EventKind=="rescan");
            var stop=await runtime.InvokeAsync("testTaskWatchRemove",new{mountId=mount,sessionId=session});var sequence=NativeRuntimeProtocol.Read<long>(stop.GetProperty("sequence"));
            await Next(x=>x.Sequence==sequence);var stats=await runtime.InvokeAsync("taskWatchDiagnostics",new{});Assert.Equal(0,stats.GetProperty("watches").GetInt32());
            Assert.True(stats.GetProperty("overflows").GetInt64()>=1);Assert.True(observed.DistinctBy(x=>x.TaskId).OrderBy(x=>x.Sequence).Select(x=>x.Sequence).SequenceEqual(Enumerable.Range(1,(int)sequence).Select(x=>(long)x)));
        }
        finally{await runtime.InvokeAsync("testTaskWatchRemove",new{mountId=mount,sessionId=session});}
    }
    [Fact(Timeout=60000)]
    public async Task LostAcknowledgmentReusesIntentIdAndBusySinkDoesNotBlockControl()
    {
        using var area=new Area();await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var mount=Guid.NewGuid();var session=Guid.NewGuid().ToString("N");var events=Channel.CreateUnbounded<NativeTaskIntent>();
        using var sink=runtime.RegisterTaskSink(mount,session,intent=>events.Writer.TryWrite(intent),_=>{});
        await runtime.InvokeAsync("testTaskWatchAdd",new{mountId=mount,sessionId=session,rootPath=area.Path});
        try
        {
            await File.WriteAllTextAsync(Path.Combine(area.Path,"retry.txt"),"x");using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var first=await events.Reader.ReadAsync(deadline.Token);NativeTaskIntent again;
            do{again=await events.Reader.ReadAsync(deadline.Token);}while(again.TaskId!=first.TaskId);
            Assert.Equal(first,again);Assert.True((await runtime.InvokeAsync("ping",new{}).WaitAsync(TimeSpan.FromSeconds(1))).GetProperty("ready").GetBoolean());
            runtime.SendTaskAcknowledgment(first,"accepted");runtime.SendTaskResult(new(Guid.NewGuid().ToString("N"),first.TaskId,mount,session,"test","NoOp",true));
        }
        finally{await runtime.InvokeAsync("testTaskWatchRemove",new{mountId=mount,sessionId=session});}
    }
    [Fact(Timeout=90000)]
    public async Task DurableAcceptanceAndSlowRoutingStayIndependentAcrossMounts()
    {
        using var area=new Area();var a=Directory.CreateDirectory(Path.Combine(area.Path,"a")).FullName;var b=Directory.CreateDirectory(Path.Combine(area.Path,"b")).FullName;
        var repo=new MountRepository(Path.Combine(area.Path,"data"));await using var logger=new LoggerService(Path.Combine(area.Path,"logs.db"));await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();await using var dispatcher=new Services.FileTasks.TaskDispatcher(repo,logger);
        var mountA=Guid.NewGuid();var mountB=Guid.NewGuid();var sessionA=Guid.NewGuid().ToString("N");var sessionB=Guid.NewGuid().ToString("N");var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var routed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<long> Stop(Guid mount,string session){var result=await runtime.InvokeAsync("testTaskWatchRemove",new{mountId=mount,sessionId=session});return NativeRuntimeProtocol.Read<long>(result.GetProperty("sequence"));}
        var ownerA=dispatcher.Register(mountA,sessionA,runtime,async(_,token)=>{entered.TrySetResult();await blocked.Task.WaitAsync(token);return [];},()=>Stop(mountA,sessionA));
        var ownerB=dispatcher.Register(mountB,sessionB,runtime,(_,_)=>{routed.TrySetResult();return Task.FromResult<IReadOnlyCollection<string>?>([]);},()=>Stop(mountB,sessionB));
        try
        {
            await runtime.InvokeAsync("testTaskWatchAdd",new{mountId=mountA,sessionId=sessionA,rootPath=a});await runtime.InvokeAsync("testTaskWatchAdd",new{mountId=mountB,sessionId=sessionB,rootPath=b});
            await File.WriteAllTextAsync(Path.Combine(a,"slow.txt"),"a");await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(Path.Combine(b,"fast.txt"),"b");await routed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(repo.AcceptedTaskSequence(mountA,sessionA)>0);Assert.True(repo.AcceptedTaskSequence(mountB,sessionB)>0);
            Assert.True((await runtime.InvokeAsync("ping",new{}).WaitAsync(TimeSpan.FromSeconds(1))).GetProperty("ready").GetBoolean());
            blocked.TrySetResult();await ownerA.StopCaptureAsync();await ownerB.StopCaptureAsync();
            Assert.True(repo.AcceptedTaskSequence(mountA,sessionA)>1);Assert.True(repo.AcceptedTaskSequence(mountB,sessionB)>1);
            await Task.Delay(200);Assert.True(runtime.IsAvailable);Assert.True((await runtime.InvokeAsync("ping",new{})).GetProperty("ready").GetBoolean());
            Assert.Equal(0,(await runtime.InvokeAsync("taskWatchDiagnostics",new{})).GetProperty("watches").GetInt32());
        }
        finally{blocked.TrySetResult();}
    }
    [Fact(Timeout=90000)]
    public async Task TenThousandChangesStayBoundedAndDoNotFollowExternalJunctions()
    {
        using var area=new Area();var watch=Directory.CreateDirectory(Path.Combine(area.Path,"watch")).FullName;var outside=Directory.CreateDirectory(Path.Combine(area.Path,"outside")).FullName;
        var link=Path.Combine(watch,"external");ClearRemovalTests.CreateJunction(link,outside);
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var mount=Guid.NewGuid();var session=Guid.NewGuid().ToString("N");var events=new ConcurrentQueue<NativeTaskIntent>();
        using var sink=runtime.RegisterTaskSink(mount,session,intent=>
        {
            events.Enqueue(intent);runtime.SendTaskAcknowledgment(intent,"accepted");runtime.SendTaskResult(new(Guid.NewGuid().ToString("N"),intent.TaskId,mount,session,"test","Completed",true));return true;
        },_=>{});
        await runtime.InvokeAsync("testTaskWatchAdd",new{mountId=mount,sessionId=session,rootPath=watch});
        try
        {
            for(var index=0;index<1000;index++)
            {
                var path=Path.Combine(watch,"file-"+index.ToString("D4")+".txt");for(var write=0;write<10;write++)File.AppendAllText(path,"x");
            }
            await File.WriteAllTextAsync(Path.Combine(outside,"keep.txt"),"outside must stay outside");
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while(!events.Any(item=>item.EventKind=="rescan")&&events.Select(item=>item.Path).Distinct().Count()<1000&&DateTime.UtcNow<deadline)await Task.Delay(50);
            Assert.True(events.Any(item=>item.EventKind=="rescan")||events.Select(item=>item.Path).Distinct().Count()==1000);
            await Task.Delay(400);Assert.DoesNotContain(events,item=>item.Path.StartsWith("external/",StringComparison.OrdinalIgnoreCase));
            var stats=await runtime.InvokeAsync("taskWatchDiagnostics",new{});Assert.InRange(stats.GetProperty("queuedPaths").GetInt32(),0,4096);Assert.InRange(stats.GetProperty("pendingIntents").GetInt32(),0,4096);
            Assert.InRange(stats.GetProperty("pathBytes").GetInt64(),0,8*1024*1024);Assert.InRange(stats.GetProperty("intentBytes").GetInt64(),0,8*1024*1024);
            Assert.True((await runtime.InvokeAsync("ping",new{}).WaitAsync(TimeSpan.FromSeconds(1))).GetProperty("ready").GetBoolean());Assert.Equal("outside must stay outside",await File.ReadAllTextAsync(Path.Combine(outside,"keep.txt")));
        }
        finally
        {
            await runtime.InvokeAsync("testTaskWatchRemove",new{mountId=mount,sessionId=session});Directory.Delete(link);
        }
    }
    [Fact(Timeout=60000)]
    public async Task MoreThanSixtyFourRootsShareOneIocpWithoutArtificialConnectionFailure()
    {
        using var area=new Area();await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var roots=new List<(Guid Mount,string Session)>();
        try
        {
            for(var index=0;index<70;index++)
            {
                var path=Directory.CreateDirectory(Path.Combine(area.Path,index.ToString())).FullName;var mount=Guid.NewGuid();var session=Guid.NewGuid().ToString("N");
                await runtime.InvokeAsync("testTaskWatchAdd",new{mountId=mount,sessionId=session,rootPath=path});roots.Add((mount,session));
            }
            Assert.Equal(70,(await runtime.InvokeAsync("taskWatchDiagnostics",new{})).GetProperty("watches").GetInt32());
        }
        finally{foreach(var root in roots)await runtime.InvokeAsync("testTaskWatchRemove",new{mountId=root.Mount,sessionId=root.Session});}
        Assert.Equal(0,(await runtime.InvokeAsync("taskWatchDiagnostics",new{})).GetProperty("watches").GetInt32());
    }
    private sealed class Area:IDisposable
    {
        internal string Path {get;}=Directory.CreateTempSubdirectory("HybridMount.NativeTaskWatch.").FullName;
        public void Dispose(){if(!System.IO.Path.GetFullPath(Path).StartsWith(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"HybridMount.NativeTaskWatch."),StringComparison.OrdinalIgnoreCase))throw new IOException("测试根越界。");Directory.Delete(Path,true);}
    }
}
