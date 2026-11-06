using HybridMount.Common;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class UploadRetryTests
{
    private sealed class ControlledSource(IFileSource inner):IFileSource
    {
        public bool FailPrepare,FailSend;public int Sends;
        public SourceWriteCapabilities WriteCapabilities{get;set;}=new(CreateOnly:CapabilitySupport.Supported,ConditionalUpdate:CapabilitySupport.Supported);
        public FileSourceCapabilities Capabilities=>inner.Capabilities;
        public bool IsSameVersion(string left,string right)=>inner.IsSameVersion(left,right);
        public Task ProbeAsync(CancellationToken token=default)=>inner.ProbeAsync(token);
        public IAsyncEnumerable<SourceEntry> ListAsync(string path,CancellationToken token=default)=>inner.ListAsync(path,token);
        public Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)=>inner.GetMetadataAsync(path,token);
        public Task<SourceEntry?> TryGetMetadataAsync(string path,CancellationToken token=default)=>inner.TryGetMetadataAsync(path,token);
        public ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)=>inner.ReadRangeAsync(entry,offset,buffer,token);
        public Task PrepareUploadAsync(string path,UploadPrecondition condition,long length,CancellationToken token=default)
        {if(FailPrepare)throw new SourceOutcomeUnknownException("发送前探测",new IOException("模拟网络中断"));return inner.PrepareUploadAsync(path,condition,length,token);}
        public Task<SourceEntry> UploadAsync(string path,Stream content,string? expectedVersion,CancellationToken token=default)=>UploadWithPreconditionAsync(path,content,new(expectedVersion),null,token);
        public Task<SourceEntry> UploadWithPreconditionAsync(string path,Stream content,UploadPrecondition condition,IProgress<UploadProgress>? progress=null,CancellationToken token=default)
        {Sends++;if(FailSend){FailSend=false;throw new SourceOutcomeUnknownException("条件上传",new IOException("模拟请求丢失"));}return inner.UploadWithPreconditionAsync(path,content,condition,progress,token);}
        public Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)=>inner.CreateDirectoryAsync(path,token);
        public Task<SourceEntry> MoveAsync(string path,string destination,string expectedVersion,CancellationToken token=default)=>inner.MoveAsync(path,destination,expectedVersion,token);
        public Task DeleteAsync(string path,string expectedVersion,CancellationToken token=default)=>inner.DeleteAsync(path,expectedVersion,token);
        public ValueTask DisposeAsync()=>inner.DisposeAsync();
    }
    private sealed class SourceFactory(IFileSource source):IFileSourceFactory
    {
        public async Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)
        {await source.ProbeAsync(token);return source;}
    }
    private static IFileSourceFactory Factory(DavFixture dav,Action<ControlledSource> configure)
    {
        var source=new ControlledSource(dav.Source());configure(source);return new SourceFactory(source);
    }
    [Theory]
    [InlineData("Prepared","Waiting")]
    [InlineData("Queued","Waiting")]
    [InlineData("Sending","OutcomeUnknown")]
    public async Task UnknownExceptionRequiresPersistedSendingState(string currentState,string expected)
    {
        await using var dav=new DavFixture();var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        try
        {
            var repository=new MountRepository(Path.Combine(dav.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(dav.Area,"logs.db"));await using var manager=new MountManager(repository,logger);
            var mount=new MountDefinition(Guid.NewGuid(),"发送阶段",dav.Area,Source:dav.Options);var item=new LocalUpload(Guid.NewGuid().ToString("N"),mount.Id,"file","file",false,"stamp",1,currentState,DateTimeOffset.UtcNow);repository.SaveUpload(item);
            manager.UploadCheckpointForTests=(_,_)=>throw new SourceOutcomeUnknownException("发送前探测",new IOException("模拟网络中断"));
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            var root=Activator.CreateInstance(typeof(MountManager).GetNestedType("Runtime",System.Reflection.BindingFlags.NonPublic)!,[mount])!;
            using var runtime=(IDisposable)Activator.CreateInstance(typeof(MountManager).GetNestedType("UploadRuntime",System.Reflection.BindingFlags.NonPublic)!,[CancellationToken.None])!;
            await (Task)typeof(MountManager).GetMethod("RunUploadAsync",flags)!.Invoke(manager,[root,runtime,item,CancellationToken.None])!;
            var result=repository.Upload(item.Id)!;Assert.Equal(expected,result.State);Assert.NotNull(result.LastCheckedUtc);
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
    [Theory]
    [InlineData(false,CapabilitySupport.Supported,0,false,false,"Waiting")]
    [InlineData(false,CapabilitySupport.Supported,4,false,false,"Waiting")]
    [InlineData(false,CapabilitySupport.Unsupported,0,false,false,"Conflict")]
    [InlineData(false,CapabilitySupport.Unknown,0,false,false,"Conflict")]
    [InlineData(false,CapabilitySupport.Supported,5,false,false,"Conflict")]
    [InlineData(true,CapabilitySupport.Supported,0,false,false,"Waiting")]
    [InlineData(true,CapabilitySupport.Unsupported,0,false,false,"Failed")]
    [InlineData(true,CapabilitySupport.Supported,5,false,false,"Failed")]
    [InlineData(true,CapabilitySupport.Supported,0,true,false,"Conflict")]
    [InlineData(false,CapabilitySupport.Supported,0,false,true,"Canceled")]
    [InlineData(true,CapabilitySupport.Supported,0,false,true,"Canceled")]
    public async Task ReadonlyReconciliationPersistsBoundedRetryWithoutCfApi(bool update,CapabilitySupport support,int retries,bool changed,bool canceled,string state)
    {
        await using var dav=new DavFixture();var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        try
        {
            var repository=new MountRepository(Path.Combine(dav.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(dav.Area,"logs.db"));await using var manager=new MountManager(repository,logger);
            await using var source=new ControlledSource(dav.Source()){WriteCapabilities=new(CreateOnly:support,ConditionalUpdate:support)};
            var mount=new MountDefinition(Guid.NewGuid(),"条件重试",dav.Area,Source:dav.Options);var baseline=update?await source.GetMetadataAsync("文件.bin"):null;
            var digest=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dav.Bytes));
            var item=new LocalUpload(Guid.NewGuid().ToString("N"),mount.Id,"文件.bin","文件.bin",false,"stamp",1,"OutcomeUnknown",DateTimeOffset.UtcNow,
                ExpectedVersion:baseline?.Version,BaselineSha256:changed?new string('0',64):baseline is null?null:digest,SnapshotPath:Path.Combine(dav.Area,"saved.snapshot"),SnapshotSha256:new string('F',64),Total:1,AutoRetryCount:retries,CancelRequested:canceled);
            if(!update)dav.Objects.TryRemove("文件.bin",out _);repository.SaveUpload(item);
            const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            var type=typeof(MountManager).GetNestedType("Runtime",System.Reflection.BindingFlags.NonPublic)!;var root=Activator.CreateInstance(type,[mount])!;type.GetField("Source")!.SetValue(root,source);
            await (Task)typeof(MountManager).GetMethod("ReconcileUploadAsync",flags)!.Invoke(manager,[root,item,CancellationToken.None])!;
            var result=new MountRepository(repository.DataDirectory,registry).Upload(item.Id)!;Assert.Equal(state,result.State);Assert.Equal(state=="Waiting"?retries+1:retries,result.AutoRetryCount);
            Assert.Equal(item.SnapshotPath,result.SnapshotPath);
            if(state=="Waiting")
            {
                Assert.NotNull(result.LastCheckedUtc);Assert.NotNull(result.NextAttemptUtc);Assert.True(result.NextAttemptUtc>result.LastCheckedUtc);
                var delay=(result.NextAttemptUtc!.Value-result.LastCheckedUtc!.Value).TotalSeconds;var basis=3*Math.Pow(2,retries);Assert.InRange(delay,basis*0.8-0.000001,basis*1.2+0.000001);
                Assert.Contains("下次重试",result.Error);Assert.Null(UploadRules.NextUpload([result],result.LastCheckedUtc.Value));Assert.Same(result,UploadRules.NextUpload([result],result.NextAttemptUtc.Value));
            }
            Assert.Equal(0,source.Sends);
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
    [Fact(Timeout=120000)]
    public async Task PreSendProbeNetworkFailureWaitsWithoutSending()
    {
        ControlledSource source=null!;await using var test=await UploadFlowTests.Fixture.Create(sourceFactory:dav=>Factory(dav,value=>{source=value;source.FailPrepare=true;}));
        await File.WriteAllTextAsync(Path.Combine(test.Local,"probe.txt"),"snapshot");var waiting=await test.Wait("probe.txt","Waiting");
        Assert.NotNull(waiting.LastCheckedUtc);Assert.True(File.Exists(waiting.SnapshotPath));Assert.Equal(0,source.Sends);Assert.Null(await source.TryGetMetadataAsync("probe.txt"));
        source.FailPrepare=false;var completed=await test.Wait("probe.txt");Assert.Equal(0,completed.AutoRetryCount);Assert.Equal(1,source.Sends);
    }
    [Theory(Timeout=120000)]
    [InlineData(false,true,0,"Completed")]
    [InlineData(false,false,0,"Conflict")]
    [InlineData(true,true,0,"Completed")]
    [InlineData(true,false,0,"Failed")]
    [InlineData(false,true,5,"Conflict")]
    [InlineData(true,true,5,"Failed")]
    public async Task UnknownConditionalWriteRetriesOnlySupportedSafeBaseline(bool update,bool supported,int retries,string state)
    {
        ControlledSource source=null!;await using var test=await UploadFlowTests.Fixture.Create(sourceFactory:dav=>Factory(dav,value=>source=value));
        var path=Path.Combine(test.Local,"retry.txt");if(update){await File.WriteAllTextAsync(path,"original");await test.Wait("retry.txt");}
        source.WriteCapabilities=new(CreateOnly:supported?CapabilitySupport.Supported:CapabilitySupport.Unsupported,ConditionalUpdate:supported?CapabilitySupport.Supported:CapabilitySupport.Unsupported);source.FailSend=true;
        await File.WriteAllTextAsync(path,"changed");var unknown=await test.Wait("retry.txt","OutcomeUnknown");var sends=source.Sends;
        test.Repository.SaveUpload(unknown with{AutoRetryCount=retries,LastCheckedUtc=DateTimeOffset.UtcNow.AddMinutes(-2)});
        // 直接改库绕过生产调度入口；事件驱动调度需要显式唤醒，不能依赖旧的500毫秒轮询。
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        var result=await test.Wait("retry.txt",state);Assert.Equal(state=="Completed"?sends+1:sends,source.Sends);
        Assert.Equal(state=="Completed"?0:retries,result.AutoRetryCount);if(state=="Completed")Assert.Equal("changed",System.Text.Encoding.UTF8.GetString(test.Dav.Objects["retry.txt"].Bytes));
    }
}
