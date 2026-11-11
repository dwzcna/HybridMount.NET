using System.Diagnostics;
using System.Text.Json;
namespace HybridMount.Tests;

/// <summary>测试生产邮箱的容量、绝对计数及因果屏障，并在独立宿主注入发送停止；不操作正式根。</summary>
[Collection("Native IPC")]
public sealed class NativeNotificationQueueTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static NativeRuntimeEvent State(string id,int sequence,int opens=0,int closes=0)=>new("mount","session","activity",NativeRuntimeProtocol.Element(new{entryId=id,sequence=(long)sequence,openCount=opens,closeCount=(long)closes}));
    private static NativeRuntimeEvent Critical(string id,string name="renamed")=>new("mount","session",name,NativeRuntimeProtocol.Element(new{entryId=id}));
    [Fact]
    public async Task ManagedStateMergesWithoutLosingCloseCountOrCrossingCriticalBarrier()
    {
        var queue=new NativeEventInbox();for(var i=1;i<=10000;i++)Assert.True(queue.TryWrite(State("same",i,i%2,i/2)));
        Assert.True(queue.TryWrite(Critical("same")));Assert.True(queue.TryWrite(State("same",10001,1,5000)));Assert.True(queue.TryWrite(State("same",9999,0,4999)));queue.Complete();
        var values=new List<NativeRuntimeEvent>();await foreach(var value in queue.ReadAllAsync(default))values.Add(value);
        Assert.Equal(3,values.Count);Assert.Equal(10000,NativeRuntimeProtocol.Read<long>(values[0].Data.GetProperty("sequence")));Assert.Equal(5000,NativeRuntimeProtocol.Read<long>(values[0].Data.GetProperty("closeCount")));Assert.Equal("renamed",values[1].Event);Assert.Equal(10001,NativeRuntimeProtocol.Read<long>(values[2].Data.GetProperty("sequence")));
        Assert.False(queue.TryWrite(Critical("late")));
    }
    [Fact]
    public async Task ManagedCriticalEventsHaveReservedCapacityAndStayFifo()
    {
        var queue=new NativeEventInbox();for(var i=0;i<128;i++)Assert.True(queue.TryWrite(State(i.ToString(),1)));Assert.False(queue.TryWrite(State("state-overflow",1)));
        for(var i=0;i<256;i++)Assert.True(queue.TryWrite(Critical((1000+i).ToString(),i%2==0?"renamed":"deleted")));Assert.False(queue.TryWrite(Critical("overflow")));queue.Complete();
        var values=new List<NativeRuntimeEvent>();await foreach(var value in queue.ReadAllAsync(default))values.Add(value);Assert.Equal(384,values.Count);Assert.Equal(Enumerable.Range(1000,256).Select(i=>i.ToString()),values.Skip(128).Select(value=>value.Data.GetProperty("entryId").GetString()));
    }
    [Fact]
    public async Task EmptyManagedInboxRespondsToCancellation()
    {var queue=new NativeEventInbox();using var cancel=new CancellationTokenSource();await using var reader=queue.ReadAllAsync(cancel.Token).GetAsyncEnumerator();var wait=reader.MoveNextAsync().AsTask();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>wait);queue.Complete();}
    [Fact(Timeout=20000)]
    public async Task ActivityCountRejectsOldGenerationOldSequenceAndPostStopUpdates()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.Notify65",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        try
        {
            var repository=new MountRepository(Path.Combine(area,"data"),definitions:new Definitions());await using var logger=new LoggerService(Path.Combine(area,"log.db"));var changed=0;await using var session=new CloudMountSession(new(Guid.NewGuid(),"notification",area,false),null,repository,logger,localChanged:()=>changed++);
            var state=State("entry",5,2,3).Data;session.NotificationForTests(state,false);Assert.False(session.IsActive("entry"));session.NotificationForTests(state);Assert.True(session.IsActive("entry"));Assert.Equal(1,changed);
            session.NotificationForTests(State("entry",2,0,1).Data);Assert.True(session.IsActive("entry"));Assert.Equal(1,changed);session.NotificationForTests(State("entry",6,0,4).Data);Assert.False(session.IsActive("entry"));Assert.Equal(2,changed);
            await session.RequestStop();session.NotificationForTests(State("entry",7,1,5).Data);Assert.False(session.IsActive("entry"));Assert.Equal(2,changed);await session.DisposeAsync();
        }
        finally{Directory.Delete(area,true);}
    }
    [Fact(Timeout=20000)]
    public async Task StopDrainsAdmittedNotificationsInOrderWithoutNeedingNewQueueCapacity()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.Notify65",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        try
        {
            var repository=new MountRepository(Path.Combine(area,"data"),definitions:new Definitions());await using var logger=new LoggerService(Path.Combine(area,"log.db"));await using var session=new CloudMountSession(new(Guid.NewGuid(),"drain",area,false),null,repository,logger);using var release=new ManualResetEventSlim();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var order=new List<int>();
            try{session.QueueNotificationForTests(()=>{entered.TrySetResult();Assert.True(release.Wait(TimeSpan.FromSeconds(5)));order.Add(1);});session.QueueNotificationForTests(()=>order.Add(2));await entered.Task;await session.RequestStop();var stop=session.DisposeAsync().AsTask();Assert.False(stop.IsCompleted);release.Set();await stop.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(new[]{1,2},order);session.QueueNotificationForTests(()=>order.Add(3));Assert.Equal(new[]{1,2},order);}
            finally{release.Set();}
        }
        finally{Directory.Delete(area,true);}
    }
    private sealed class Definitions:IMountDefinitionStore
    {public IReadOnlyList<MountDefinition> LoadDefinitions()=>[];public void SaveDefinition(MountDefinition value){}public void DeleteDefinition(Guid value){}public long CacheLimitBytes{get;set;}}
    [Theory(Timeout=20000)]
    [InlineData("full")][InlineData("stop")]
    public async Task NativeMailboxHasReservedBoundsAndFiniteAdmission(string mode)
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var value=await runtime.InvokeAsync("testCallbackMailbox",new{mode});output.WriteLine(value.GetRawText());Assert.False(value.GetProperty("accepted").GetBoolean());var stats=value.GetProperty("stats");Assert.Equal(384,stats.GetProperty("pending").GetInt32());Assert.Equal(1,stats.GetProperty("rejected").GetInt32());Assert.InRange(value.GetProperty("elapsedMs").GetInt64(),0,150);Assert.True(runtime.IsAvailable);
    }
    [Fact(Timeout=20000)]
    public async Task NativeStateByteBudgetCannotConsumeCriticalReservation()
    {await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var value=await runtime.InvokeAsync("testCallbackMailbox",new{mode="bytes"});Assert.False(value.GetProperty("stateAccepted").GetBoolean());Assert.Equal(258,value.GetProperty("stats").GetProperty("pending").GetInt32());}
    [Fact]
    public async Task ManagedStateByteBudgetCannotConsumeCriticalReservation()
    {
        var queue=new NativeEventInbox();NativeRuntimeEvent Large(string id)=>new("mount","session","activity",NativeRuntimeProtocol.Element(new{entryId=id,sequence=1L,openCount=1,closeCount=0L,padding=new string('x',400*1024)}));Assert.True(queue.TryWrite(Large("one")));Assert.False(queue.TryWrite(Large("overflow")));for(var i=0;i<256;i++)Assert.True(queue.TryWrite(Critical(i.ToString())));queue.Complete();var count=0;await foreach(var value in queue.ReadAllAsync(default))count++;Assert.Equal(257,count);
    }
    [Fact(Timeout=20000)]
    public async Task NativeMailboxCoalescesLatestSnapshotAndKeepsRenameOrder()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();var value=await runtime.InvokeAsync("testCallbackMailbox",new{mode="order"});var events=value.GetProperty("events").EnumerateArray().ToArray();Assert.Equal(3,events.Length);Assert.Equal("2",events[0].GetProperty("data").GetProperty("sequence").GetString());Assert.Equal("renamed",events[1].GetProperty("event").GetString());Assert.Equal("3",events[2].GetProperty("data").GetProperty("sequence").GetString());
    }
    [Fact(Timeout=30000)]
    public async Task ExternalProcessOpenAndClosePublishAbsoluteNativeCountsWithoutSourceReads()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");await File.ReadAllBytesAsync(path);var before=LocalFileState.Read(path);var cloud=CloudFilesNative.Inspect(path)!.Value;var requests=test.Dav.Server.Requests.Count;var entry=test.Repository.EntryBySource(test.Id,"文件.bin")!;var runtime=await NativeRuntimeClient.GetAsync();var messages=System.Threading.Channels.Channel.CreateUnbounded<NativeRuntimeEvent>();
        void Receive(NativeRuntimeEvent message){if(message.MountId==test.Id.ToString("N")&&message.Event=="activity"&&message.Data.GetProperty("entryId").GetString()==entry.Id)messages.Writer.TryWrite(message);}
        runtime.EventReceived+=Receive;
        var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};info.Environment["HM_NOTIFY_TEST_FILE"]=path;
        const string script="$file=[IO.File]::Open($env:HM_NOTIFY_TEST_FILE,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete));[Console]::Out.WriteLine('ready');[Console]::Out.Flush();[Console]::ReadLine()|Out-Null;$file.Dispose()";
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script))})info.ArgumentList.Add(arg);using var process=Process.Start(info)!;
        try
        {
            Assert.Equal("ready",await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));NativeRuntimeEvent opened;do{opened=await messages.Reader.ReadAsync(deadline.Token);}while(opened.Data.GetProperty("openCount").GetInt32()==0);
            Assert.True((await runtime.InvokeAsync("activity",NativeRuntimeArguments.Entry(test.Manager.Mounts.Single().Definition,entry))).GetProperty("active").GetBoolean());var sequence=NativeRuntimeProtocol.Read<long>(opened.Data.GetProperty("sequence"));var closes=NativeRuntimeProtocol.Read<long>(opened.Data.GetProperty("closeCount"));await process.StandardInput.WriteLineAsync("release");await process.StandardInput.FlushAsync();await process.WaitForExitAsync(deadline.Token);Assert.Equal(0,process.ExitCode);
            NativeRuntimeEvent closed;do{closed=await messages.Reader.ReadAsync(deadline.Token);}while(NativeRuntimeProtocol.Read<long>(closed.Data.GetProperty("sequence"))<=sequence||NativeRuntimeProtocol.Read<long>(closed.Data.GetProperty("closeCount"))<=closes);output.WriteLine(closed.Data.GetRawText());
            Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(cloud.Identity,after.Identity);Assert.Equal(cloud.Info.PinState,after.Info.PinState);Assert.Equal(cloud.Info.InSyncState,after.Info.InSyncState);
        }
        finally{runtime.EventReceived-=Receive;if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}}
    }
    [Fact(Timeout=20000)]
    public async Task FullNativeCallbackQueueFailsOutsideProducerAndWakesPendingCalls()
    {
        await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();await runtime.InvokeAsync("testCallbackWriterPause",new{paused=true});var mountId=Guid.NewGuid();var sessionId=Guid.NewGuid();
        await runtime.InvokeAsync("testCallbackEvents",new{mountId,sessionId,count=256});var stats=await runtime.InvokeAsync("notificationDiagnostics",new{});Assert.Equal(256,stats.GetProperty("pending").GetInt32());
        var failed=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);runtime.Failed+=_=>failed.TrySetResult();var watch=Stopwatch.StartNew();await Assert.ThrowsAnyAsync<Exception>(()=>runtime.InvokeAsync("testCallbackEvents",new{mountId,sessionId,count=1}).WaitAsync(TimeSpan.FromSeconds(3)));await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.False(runtime.IsAvailable);Assert.True(watch.Elapsed<TimeSpan.FromSeconds(3));
    }
}
