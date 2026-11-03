using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.Win32;
using Xunit.Abstractions;
namespace HybridMount.Tests;

/// <summary>在真实 HTTP／S3 SDK／NTFS／CFAPI 上验证停止；唯一隔离根不接触用户挂载或真实凭据。</summary>
[Collection("Process exception diagnostics")]
public sealed class ShutdownTests(ITestOutputHelper output)
{
    private static TaskCompletionSource Signal()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private async Task StopWithinTarget(MountManager manager)
    {
        var clock=Stopwatch.StartNew();var first=manager.DisposeAsync().AsTask();
        var repeated=Enumerable.Range(0,16).Select(_=>Task.Run(()=>manager.DisposeAsync().AsTask())).ToArray();
        await first.WaitAsync(TimeSpan.FromSeconds(5));await Task.WhenAll(repeated).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(first,manager.DisposeAsync().AsTask());Assert.True(clock.Elapsed<TimeSpan.FromSeconds(5));output.WriteLine("stop milliseconds="+clock.ElapsedMilliseconds);
    }
    private static async Task NoErrorLogs(LoggerService logger)
        =>Assert.DoesNotContain(await logger.ReadAsync(),item=>item.Severity=="Error");

    [Theory(Timeout=30000)]
    [InlineData(false)][InlineData(true)]
    public async Task S3ProbeCancellationWaitsForManualAndStartupOwners(bool startup)
    {
        await using var s3=new S3Fixture();await using var host=new Host(s3.Area,s3.Options,S3Fixture.Credentials);
        var entered=Signal();var release=Signal();var handler=s3.Server.Handler;
        s3.Server.Handler=async request=>(await handler(request)) with{BeforeBody=release.Task,HeadersSent=entered};
        try
        {
            var phases=new ConcurrentQueue<string>();host.Manager.StopObserver=(phase,_)=>phases.Enqueue(phase);
            var connecting=startup?host.Manager.StartAsync():host.Manager.ConnectAsync(host.Id);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));await StopWithinTarget(host.Manager);
            if(startup)await connecting;else await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>connecting);
            Assert.Equal("未连接",host.Manager.Mounts.Single().Status);Assert.Contains("ConnectionCanceled.Shutdown",phases);Assert.Null(CloudFilesNative.RootIdentity(host.Local,strict:true));
            await NoErrorLogs(host.Logger);Assert.All(s3.Server.Requests,request=>Assert.Equal("GET",request.Method));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>host.Manager.ConnectAsync(host.Id));
            Assert.ThrowsAny<OperationCanceledException>(()=>host.Manager.Queue(host.Id,"refresh"));
        }
        finally{release.TrySetResult();}
    }
    [Theory(Timeout=30000)]
    [InlineData(false)][InlineData(true)]
    public async Task ExplicitDisconnectCancelsProbeWithoutMarkingMountAbnormal(bool callerCancellation)
    {
        await using var s3=new S3Fixture();await using var host=new Host(s3.Area,s3.Options,S3Fixture.Credentials);
        var entered=Signal();var release=Signal();var handler=s3.Server.Handler;
        s3.Server.Handler=async request=>(await handler(request)) with{BeforeBody=release.Task,HeadersSent=entered};
        try
        {
            var phases=new ConcurrentQueue<string>();host.Manager.StopObserver=(phase,_)=>phases.Enqueue(phase);using var caller=new CancellationTokenSource();
            var connecting=host.Manager.ConnectAsync(host.Id,caller.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(callerCancellation)await caller.CancelAsync();else await host.Manager.DisconnectAsync(host.Id).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>connecting);Assert.Contains("ConnectionCanceled."+(callerCancellation?"Caller":"Disconnect"),phases);
            Assert.Equal("未连接",host.Manager.Mounts.Single().Status);Assert.False(host.Manager.IsStopping);await NoErrorLogs(host.Logger);
        }
        finally{release.TrySetResult();s3.Server.Handler=handler;}
    }
    [Theory(Timeout=45000)]
    [InlineData("enumeration")][InlineData("hydration")][InlineData("preview")][InlineData("formProbe")]
    public async Task BlockingS3IoIsCanceledBeforeClientRelease(string phase)
    {
        await using var s3=new S3Fixture();s3.Put("remote/photo.bmp",PreviewPipelineTests.Bmp());
        await using var host=new Host(s3.Area,s3.Options,S3Fixture.Credentials);if(phase!="formProbe")await host.Manager.ConnectAsync(host.Id);
        var entered=Signal();var release=Signal();var handler=s3.Server.Handler;
        s3.Server.Handler=async request=>
        {
            var response=await handler(request);var block=phase is "enumeration" or "formProbe"?request.Target.Contains("list-type=2",StringComparison.Ordinal):request.Method=="GET"&&request.Headers.ContainsKey("Range");
            return block?response with{BeforeBody=release.Task,HeadersSent=entered}:response;
        };
        Task? read=null;string? job=null;
        try
        {
            if(phase=="enumeration")job=host.Manager.Queue(host.Id,"refresh");
            else if(phase=="hydration")read=Task.Run(()=>File.ReadAllBytes(Path.Combine(host.Local,"文件.bin")));
            else if(phase=="preview"){await host.Manager.SetThumbnailsEnabledAsync(host.Id,true);host.Manager.RequestPreview(Path.Combine(host.Local,"photo.bmp"),128);}
            else read=host.Manager.TestConnectionAsync(s3.Options,S3Fixture.Credentials);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));await StopWithinTarget(host.Manager);
            if(read is not null)Assert.NotNull(await Record.ExceptionAsync(()=>read));
            if(job is not null)Assert.Equal("Canceled",host.Manager.GetOperation(job)!.State);
            if(phase!="formProbe")Assert.True(CloudRootRegistration.IsOwnedRoot(host.Definition));
            Assert.DoesNotContain(host.Manager.Operations,operation=>operation.State is "Failed" or "PartialFailure");await NoErrorLogs(host.Logger);
        }
        finally{release.TrySetResult();s3.Server.Handler=handler;}
    }
    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task SentS3WriteRemainsUnknownAndRestartDoesNotBlindlyRepeat(bool serverCommitted)
    {
        await using var s3=new S3Fixture();await using var host=new Host(s3.Area,s3.Options,S3Fixture.Credentials);await host.Manager.ConnectAsync(host.Id);
        const string name="shutdown-write.txt";var entered=Signal();var release=Signal();var handler=s3.Server.Handler;
        s3.Server.Handler=async request=>
        {
            if(request.Method=="PUT"&&request.Target.EndsWith("/"+name,StringComparison.Ordinal))
            {
                var response=serverCommitted?await handler(request):new HttpTestServer.Response(503,[]);
                entered.TrySetResult();await release.Task;return response;
            }
            return await handler(request);
        };
        try
        {
            await File.WriteAllTextAsync(Path.Combine(host.Local,name),"write-result-must-be-reconciled");await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await StopWithinTarget(host.Manager);var sent=host.Repository.Uploads(host.Id).Single(item=>item.LocalPath==name);
            Assert.Equal("OutcomeUnknown",sent.State);Assert.False(sent.CancelRequested);Assert.NotNull(sent.SnapshotPath);Assert.True(File.Exists(sent.SnapshotPath));
            Assert.Equal(serverCommitted,s3.Objects.ContainsKey("remote/"+name));
            release.TrySetResult();s3.Server.Handler=handler;host.Manager=new MountManager(host.Repository,host.Logger);await host.Manager.ConnectAsync(host.Id);
            var limit=DateTime.UtcNow.AddSeconds(15);
            while(host.Repository.Upload(sent.Id)!.State is "OutcomeUnknown" or "RemoteCommitted"&&DateTime.UtcNow<limit)await Task.Delay(30);
            Assert.Equal(serverCommitted?"Completed":"Conflict",host.Repository.Upload(sent.Id)!.State);
            await host.Manager.ScanUploadChangesAsync(host.Id);await Task.Delay(2300);
            Assert.Single(s3.Server.Requests,request=>request.Method=="PUT"&&request.Target.EndsWith("/"+name,StringComparison.Ordinal));
        }
        finally{release.TrySetResult();s3.Server.Handler=handler;}
    }
    [Fact(Timeout=30000)]
    public async Task LateNonCooperativeConnectionIsAwaitedAndDisposedExactlyOnce()
    {
        await using var dav=new DavFixture();var entered=Signal();var release=Signal();var source=new CountingSource();CancellationToken connection=default;
        var factory=new ControlledFactory(async token=>{connection=token;entered.TrySetResult();await release.Task;return source;});
        await using var host=new Host(dav.Area,dav.Options,new(),factory);
        var connecting=host.Manager.ConnectAsync(host.Id);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping=host.Manager.DisposeAsync().AsTask();Assert.True(connection.IsCancellationRequested);Assert.False(stopping.IsCompleted);
        release.TrySetResult();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>connecting);await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1,source.Disposals);Assert.Null(CloudFilesNative.RootIdentity(host.Local,strict:true));Assert.Equal("未连接",host.Manager.Mounts.Single().Status);await NoErrorLogs(host.Logger);
    }
    [Fact(Timeout=30000)]
    public async Task CleanupFailureStillDisposesOtherRootsAndIsNotReportedAsSuccess()
    {
        await using var dav=new DavFixture();var broken=new CountingSource{FailDisposal=true};var good=new CountingSource();var calls=0;
        var factory=new ControlledFactory(_=>Task.FromResult<IFileSource>(Interlocked.Increment(ref calls)==1?broken:good));
        await using var host=new Host(dav.Area,dav.Options,new(),factory){ExpectedStopFailure=true};await host.Manager.ConnectAsync(host.Id);
        var second=Path.Combine(dav.Area,"second-view");Directory.CreateDirectory(second);await host.Manager.AddAsync("第二根",second,false,source:dav.Options);
        var phases=new ConcurrentQueue<string>();host.Manager.StopObserver=(phase,_)=>phases.Enqueue(phase);
        await Assert.ThrowsAsync<AggregateException>(()=>host.Manager.DisposeAsync().AsTask());
        Assert.Equal(1,broken.Disposals);Assert.Equal(1,good.Disposals);Assert.Contains("DisconnectFailed",phases);Assert.Contains("StoppedWithErrors",phases);
        Assert.All(host.Manager.Mounts,mount=>Assert.True(CloudRootRegistration.IsOwnedRoot(mount.Definition)));
        await Assert.ThrowsAsync<AggregateException>(()=>host.Manager.DisposeAsync().AsTask());Assert.Equal(1,good.Disposals);
    }
    [Fact]
    public async Task CacheMissingIsNullableWithoutNormalFileOrDirectoryExceptions()
    {
        await using var dav=new DavFixture();await using var cache=new ExePreviewCache(Path.Combine(dav.Area,"absent","cache"));
        var mount=new MountDefinition(Guid.NewGuid(),"cache",dav.Area,ThumbnailsEnabled:true);var entry=new MountEntry("entry","test.png","test.png","v1",false);
        var count=0;EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observer=(_,args)=>{if(args.Exception is FileNotFoundException or DirectoryNotFoundException)Interlocked.Increment(ref count);};
        AppDomain.CurrentDomain.FirstChanceException+=observer;
        try{for(var index=0;index<100;index++){Assert.Null(cache.Get(mount,entry,64));Assert.Null(LocalFileMetadata.TryRead(Path.Combine(dav.Area,"not-existing","file")));}Assert.Equal(0,count);}
        finally{AppDomain.CurrentDomain.FirstChanceException-=observer;}
        var path=Path.Combine(dav.Area,"metadata.bin");await File.WriteAllBytesAsync(path,new byte[12345]);Assert.Equal(12345,LocalFileMetadata.TryRead(path)!.Value.Length);
    }
    [Fact]
    public async Task InternalHttpTimeoutStillFailsWhileCallerCancellationRemainsNormal()
    {
        await using var s3=new S3Fixture();using var client=new TimeoutS3Client();await using var source=new S3FileSource(s3.Options,S3Fixture.Credentials,Path.Combine(s3.Area,"timeout"),client:client);
        var error=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync());Assert.Equal(ConnectionFailureKind.Network,error.Diagnostic.Kind);
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>source.ProbeAsync(canceled.Token));
        await using var dav=new DavFixture();await using var davSource=new WebDavFileSource(dav.Options,new(),new WholeFileCache(Path.Combine(dav.Area,"cache")),new TimeoutHttpHandler());
        var davTimeout=await Assert.ThrowsAsync<SourceConnectionException>(()=>davSource.ProbeAsync());Assert.Equal(ConnectionFailureKind.Timeout,davTimeout.Diagnostic.Kind);Assert.Contains("超时",davTimeout.Message);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>davSource.ProbeAsync(canceled.Token));
    }
    [Fact]
    public async Task ShutdownStagesContinueAfterFailureAndLifecycleRetainsActualContext()
    {
        await using var dav=new DavFixture();var logs=Path.Combine(dav.Area,"lifecycle");var lifecycle=new LifecycleDiagnostics(logs,"0.0.5.2","Packaged");var order=new List<string>();
        var steps=new ShutdownSequence((phase,error)=>{order.Add(phase);lifecycle.Record(phase,error,cancellationReason:"Shutdown");});
        steps.Run("First",()=>throw new IOException("do-not-log-this-body"));await steps.RunAsync("Second",()=>Task.FromException(new InvalidOperationException("secret")));steps.Run("Last",()=>{});
        Assert.Equal(2,steps.Failures);Assert.Equal(new[]{"FirstStarting","FirstFailed","SecondStarting","SecondFailed","LastStarting","LastCompleted"},order);
        lifecycle.Record("ExitCompletedWithErrors",completed:false,cancellationReason:"Shutdown");Assert.True(new LifecycleDiagnostics(logs,"0.0.5.2","Unpackaged").PreviousSessionIncomplete);
        var text=File.ReadAllText(lifecycle.EventFilePath);Assert.DoesNotContain("do-not-log",text);Assert.DoesNotContain("secret",text);
        using var json=JsonDocument.Parse(File.ReadLines(lifecycle.EventFilePath).Last());var value=json.RootElement;
        Assert.Equal(Debugger.IsAttached,value.GetProperty("debuggerAttached").GetBoolean());Assert.Equal(JsonValueKind.Null,value.GetProperty("packageIdentity").ValueKind);Assert.Equal("Shutdown",value.GetProperty("cancellationReason").GetString());
    }
    [Fact(Timeout=30000)]
    public async Task AllManualConnectionsReceiveStopBeforeEitherIsAwaited()
    {
        await using var dav=new DavFixture();var entered=Signal();var bothCanceled=Signal();var arrived=0;var canceled=0;
        var factory=new ControlledFactory(async token=>
        {
            if(Interlocked.Increment(ref arrived)==2)entered.TrySetResult();
            using var registration=token.Register(()=>{if(Interlocked.Increment(ref canceled)==2)bothCanceled.TrySetResult();});
            await bothCanceled.Task;token.ThrowIfCancellationRequested();throw new InvalidOperationException("不可达");
        });
        await using var host=new Host(dav.Area,dav.Options,new(),factory);
        var otherLocal=Path.Combine(dav.Area,"other-pending");Directory.CreateDirectory(otherLocal);var other=new MountDefinition(Guid.NewGuid(),"另一连接",otherLocal,false,RootIdentity:MountPathGuard.FileIdentity(otherLocal),Source:dav.Options,RegistrationStage:MountRegistrationStage.NotRegistered);
        host.Repository.SaveDefinition(other);await host.Manager.DisposeAsync();host.Manager=new MountManager(host.Repository,host.Logger,factory);
        var first=host.Manager.ConnectAsync(host.Id);var second=host.Manager.ConnectAsync(other.Id);await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StopWithinTarget(host.Manager);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>second);
        Assert.Equal(2,canceled);Assert.All(host.Manager.Mounts,mount=>Assert.Equal("未连接",mount.Status));await NoErrorLogs(host.Logger);
    }
    [Fact(Timeout=15000)]
    public async Task IpcStopsPartialClientAndRepeatedDisposalUsesSameTask()
    {
        await using var dav=new DavFixture();await using var logger=new LoggerService(Path.Combine(dav.Area,"ipc-log.db"));
        var name="HybridMount.StopTest."+Guid.NewGuid().ToString("N");await using var server=new IpcServerService(logger,name);server.Start();
        using var client=new System.IO.Pipes.NamedPipeClientStream(".",name,System.IO.Pipes.PipeDirection.InOut,System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);await client.WriteAsync(new byte[]{4,0});
        var signals=server.RequestStop();var stop=server.DisposeAsync().AsTask();Assert.Same(stop,server.DisposeAsync().AsTask());await Task.WhenAll(signals,stop).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Throws<ObjectDisposedException>(()=>server.Start());await NoErrorLogs(logger);
    }
    private sealed class TimeoutHttpHandler:HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromException<HttpResponseMessage>(new TaskCanceledException("HTTP internal timeout"));}
    private sealed class TimeoutS3Client:AmazonS3Client
    {
        public TimeoutS3Client():base(new Amazon.Runtime.AnonymousAWSCredentials(),new AmazonS3Config{ServiceURL="http://127.0.0.1:1",AuthenticationRegion="us-east-1",MaxErrorRetry=0}){}
        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request,CancellationToken token=default)=>Task.FromException<ListObjectsV2Response>(new TaskCanceledException("HTTP internal timeout"));
    }
    private sealed class ControlledFactory(Func<CancellationToken,Task<IFileSource>> create):IFileSourceFactory
    {public Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)=>create(token);}
    /// <summary>只有空枚举与释放计数，工厂延迟由测试控制；不伪装网络层验收。</summary>
    private sealed class CountingSource:IFileSource
    {
        public int Disposals;public bool FailDisposal;
        public FileSourceCapabilities Capabilities=>new(CapabilitySupport.Unsupported,CapabilitySupport.Unsupported,CapabilitySupport.Unsupported,CapabilitySupport.Unsupported,CapabilitySupport.Unsupported,SourceCaseSensitivity.Sensitive);
        public async IAsyncEnumerable<SourceEntry> ListAsync(string path,[EnumeratorCancellation]CancellationToken token=default){await Task.CompletedTask;token.ThrowIfCancellationRequested();yield break;}
        public Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
        public ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> UploadAsync(string path,Stream content,string? expectedVersion,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> MoveAsync(string path,string destination,string expectedVersion,CancellationToken token=default)=>throw new NotSupportedException();
        public Task DeleteAsync(string path,string expectedVersion,CancellationToken token=default)=>throw new NotSupportedException();
        public ValueTask DisposeAsync(){Interlocked.Increment(ref Disposals);if(FailDisposal)throw new IOException("test-dispose-failure");return ValueTask.CompletedTask;}
    }
    /// <summary>持久定义与凭据均使用本轮命名空间；常规停止保留根，最终只清空本轮隔离根。</summary>
    private sealed class Host:IAsyncDisposable
    {
        public readonly Guid Id=Guid.NewGuid();public readonly string Local,RegistryPath;public readonly MountRepository Repository;public readonly LoggerService Logger;
        public MountManager Manager;public bool ExpectedStopFailure;
        public MountDefinition Definition=>Repository.LoadDefinitions().Single(item=>item.Id==Id);
        public Host(string area,FileSourceConfiguration settings,SourceCredentials credentials,IFileSourceFactory? factory=null)
        {
            Local=Path.Combine(area,"stop-view-"+Id.ToString("N"));Directory.CreateDirectory(Local);RegistryPath=@"Software\HybridMount.NET.Tests\0052-"+Id.ToString("N");
            Repository=new(Path.Combine(area,"stop-data-"+Id.ToString("N")),RegistryPath);Logger=new(Path.Combine(area,"stop-logs-"+Id.ToString("N")+".db"));
            Repository.SaveDefinition(new(Id,"停止回归",Local,true,RootIdentity:MountPathGuard.FileIdentity(Local),Source:settings,RegistrationStage:MountRegistrationStage.NotRegistered,AutoUploadEnabled:true,UploadInitialReviewCompleted:true,ContinuousUploadMigrated:true));
            new CredentialStore(Repository.DataDirectory).Save(Id,credentials);Manager=new(Repository,Logger,factory);
        }
        public async ValueTask DisposeAsync()
        {
            try{await Manager.DisposeAsync();}catch(AggregateException)when(ExpectedStopFailure){}
            await using(var cleanup=new MountManager(Repository,Logger))
            {
                foreach(var mount in cleanup.Mounts){var result=await WebDavTests.Finished(cleanup,cleanup.Queue(mount.Definition.Id,"clearAndRemove"));Assert.True(result.State=="Completed",result.Error);}
            }
            await Logger.DisposeAsync();Registry.CurrentUser.DeleteSubKeyTree(RegistryPath,false);
        }
    }
}
