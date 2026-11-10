using System.Diagnostics;
using System.Text.Json;
namespace HybridMount.Tests;

/// <summary>生产异步回调的真实原生进程验证；只操作隔离根，不借助主程序或远端预览补图。</summary>
[Collection("Native IPC")]
public sealed class AsyncThumbnailTests
{
    [Fact(Timeout=90000)]
    public async Task FullLocalMissReturnsImmediatelyThenUsesBoundedMemoryCacheWithoutSourceOrDatabaseWrites()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("async.bmp",PreviewPipelineTests.Bmp(600,600));await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var path=Path.Combine(test.Local,"async.bmp");
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]))).State);await test.Manager.DisconnectAsync(test.Id);
        var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try
        {
            var before=CloudFilesNative.InspectPersisted(path);var requests=test.Dav.Server.Requests.Count;var identity=LocalFileState.Read(path).Identity;
            var result=await Probe(path,180);var samples=result.GetProperty("samples").EnumerateArray().ToArray();var stats=result.GetProperty("statistics");
            Assert.False(samples[0].GetProperty("hit").GetBoolean());Assert.True(samples.Any(x=>x.GetProperty("hit").GetBoolean()),result.ToString());Assert.Equal(1,stats.GetProperty("submitted").GetInt32());Assert.Equal(1,stats.GetProperty("decoded").GetInt32());Assert.InRange(stats.GetProperty("bytes").GetInt64(),33,32*1024*1024);Assert.InRange(stats.GetProperty("cached").GetInt32(),1,128);
            var durations=samples.Skip(1).Select(x=>x.GetProperty("ms").GetDouble()).Order().ToArray();Assert.True(durations[(int)(durations.Length*.95)]<=50,result.ToString());Assert.True(durations[(int)(durations.Length*.99)]<=150,result.ToString());
            var after=CloudFilesNative.InspectPersisted(path);Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.Null(store.Binding(path));
            store.Configure(root with{Enabled=false});var disabled=await Probe(path,20);Assert.All(disabled.GetProperty("samples").EnumerateArray(),sample=>Assert.False(sample.GetProperty("hit").GetBoolean()));
        }
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task FailedVersionCoolsDownButChangedContentCanGenerateAgain()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"changed.bmp");await File.WriteAllBytesAsync(path,[1,2,3,4]);await test.Manager.DisconnectAsync(test.Id);var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try{var task=Probe(path,300);// 用原子替换提供完整的新内容版本，避免把写入中的临时版本混入冷却验证。
            await Task.Delay(800);var replacement=path+".new";await File.WriteAllBytesAsync(replacement,PreviewPipelineTests.Bmp());File.Move(replacement,path,true);var result=await task;var stats=result.GetProperty("statistics");Assert.Equal(2,stats.GetProperty("submitted").GetInt32());Assert.Equal(1,stats.GetProperty("failed").GetInt32());Assert.Equal(1,stats.GetProperty("decoded").GetInt32());Assert.Contains(result.GetProperty("samples").EnumerateArray(),x=>x.GetProperty("hit").GetBoolean());Assert.Null(store.Binding(path));}
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task DisabledWhileDecodeWaitsCannotPublishOrNotify()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"disabled.bmp");await File.WriteAllBytesAsync(path,PreviewPipelineTests.Bmp());await test.Manager.DisconnectAsync(test.Id);var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try{var task=Probe(path,200,800);await Task.Delay(350);store.Configure(root with{Enabled=false});var result=await task;Assert.All(result.GetProperty("samples").EnumerateArray(),x=>Assert.False(x.GetProperty("hit").GetBoolean()));var stats=result.GetProperty("statistics");Assert.Equal(0,stats.GetProperty("decoded").GetInt32());Assert.Equal(0,stats.GetProperty("notified").GetInt32());Assert.Null(store.Binding(path));}
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task SlowDecoderTimesOutWithoutBlockingRepeatedShellReads()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"slow.bmp");await File.WriteAllBytesAsync(path,PreviewPipelineTests.Bmp());await test.Manager.DisconnectAsync(test.Id);var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try{var result=await Probe(path,1100,12000);var stats=result.GetProperty("statistics");Assert.InRange(stats.GetProperty("submitted").GetInt32(),1,3);Assert.InRange(stats.GetProperty("failed").GetInt32(),1,3);Assert.Equal(0,stats.GetProperty("decoded").GetInt32());Assert.All(result.GetProperty("samples").EnumerateArray(),x=>{Assert.False(x.GetProperty("hit").GetBoolean());Assert.True(x.GetProperty("ms").GetDouble()<1000);});}
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task NativeAndManagedDecodeShareTheSameFileMutex()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"mutex.bmp");await File.WriteAllBytesAsync(path,PreviewPipelineTests.Bmp());await test.Manager.DisconnectAsync(test.Id);var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try{var lease=await PreviewDecodeLease.AcquireAsync(path,default);Task<JsonElement> task;try{task=Probe(path,200);await Task.Delay(500);}finally{await lease.DisposeAsync();}var result=await task;Assert.All(result.GetProperty("samples").EnumerateArray().Take(20),x=>Assert.False(x.GetProperty("hit").GetBoolean()));Assert.Equal(1,result.GetProperty("statistics").GetProperty("decoded").GetInt32());}
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task BusyDatabaseReturnsMissImmediatelyWithoutStartingDecode()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"busy.bmp");await File.WriteAllBytesAsync(path,PreviewPipelineTests.Bmp());await test.Manager.DisconnectAsync(test.Id);var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try
        {
            using var connection=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=store.Database,Pooling=false}.ToString());connection.Open();using var command=connection.CreateCommand();command.CommandText="PRAGMA journal_mode=DELETE;BEGIN EXCLUSIVE;";command.ExecuteNonQuery();
            try{var result=await Probe(path,50);Assert.All(result.GetProperty("samples").EnumerateArray(),x=>{Assert.False(x.GetProperty("hit").GetBoolean());Assert.True(x.GetProperty("ms").GetDouble()<150);});Assert.Empty(result.GetProperty("statistics").EnumerateObject());}
            finally{command.CommandText="ROLLBACK";command.ExecuteNonQuery();}
        }
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task ThousandFileHintsKeepNativeQueueBoundedAndStopOwnedWorker()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);await test.Manager.DisconnectAsync(test.Id);var files=Enumerable.Range(0,1000).Select(i=>Path.Combine(test.Local,$"queue-{i:D4}.bmp")).ToArray();var bytes=PreviewPipelineTests.Bmp();foreach(var path in files)await File.WriteAllBytesAsync(path,bytes);var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try
        {
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--test-thumbnail-batch");start.Environment["HYBRIDMOUNT_TEST_LOCAL_DELAY_MS"]="1500";
            using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await IpcProtocol.WriteAsync(process.StandardInput.BaseStream,new{paths=files});var watch=Stopwatch.StartNew();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));Assert.Equal(0,process.ExitCode);Assert.Empty(await error);using var document=JsonDocument.Parse(await output);var stats=document.RootElement;Assert.InRange(stats.GetProperty("pending").GetInt32(),1,64);Assert.True(stats.GetProperty("rejected").GetInt64()>0);Assert.True(watch.Elapsed<TimeSpan.FromSeconds(15));
        }
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task ZeroLengthColdMediaNeverStartsLocalDecode()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("zero.bmp",[]);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));var path=Path.Combine(test.Local,"zero.bmp");await test.Manager.DisconnectAsync(test.Id);var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        try{var requests=test.Dav.Server.Requests.Count;var result=await Probe(path,20);Assert.All(result.GetProperty("samples").EnumerateArray(),x=>Assert.False(x.GetProperty("hit").GetBoolean()));Assert.Empty(result.GetProperty("statistics").EnumerateObject());Assert.Equal(requests,test.Dav.Server.Requests.Count);}
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    /// <summary>同一原生进程重复调用生产回调；同时核验无CLR模块，性能原始样本保存忽略目录。</summary>
    private static async Task<JsonElement> Probe(string path,int count,int delay=0)
    {
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var argument in new[]{"--diagnose-async-thumbnail",path,"64",count.ToString()})start.ArgumentList.Add(argument);if(delay>0)start.Environment["HYBRIDMOUNT_TEST_LOCAL_DELAY_MS"]=delay.ToString();
        using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await Task.Delay(100);process.Refresh();Assert.DoesNotContain(process.Modules.Cast<ProcessModule>(),x=>x.ModuleName.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase));await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));Assert.Equal(0,process.ExitCode);Assert.Empty(await error);var json=await output;
        var directory=Environment.GetEnvironmentVariable("HYBRIDMOUNT_ASYNC_PROOF");if(!string.IsNullOrEmpty(directory)){Directory.CreateDirectory(directory);await File.WriteAllTextAsync(Path.Combine(directory,Guid.NewGuid().ToString("N")+".json"),json);}
        using var document=JsonDocument.Parse(json);return document.RootElement.Clone();
    }
}
