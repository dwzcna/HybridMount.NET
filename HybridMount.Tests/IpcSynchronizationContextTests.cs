using System.Collections.Concurrent;
using System.Text.Json;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>模拟窗口线程不泵消息时的原生同步调用；仅使用内存帧和独立只读原生进程，不连接用户挂载。</summary>
[Collection("Native IPC")]
public sealed class IpcSynchronizationContextTests
{
    /// <summary>分片及延迟I/O必须脱离调用方上下文；外层ConfigureAwait(false)不能掩盖帧内部捕获。</summary>
    [Theory(Timeout=15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FramingCompletesWithoutDispatchingToBlockedCaller(bool writing)
    {
        using var bytes=new MemoryStream();
        if(!writing){await IpcProtocol.WriteAsync(bytes,new{message="independent frame"});bytes.Position=0;}
        using var stream=new DelayedStream(bytes);
        async Task<JsonElement> Run()
        {
            if(writing){await IpcProtocol.WriteAsync(stream,new{message="independent frame"}).ConfigureAwait(false);bytes.Position=0;return await IpcProtocol.ReadAsync<JsonElement>(bytes).ConfigureAwait(false);}
            return await IpcProtocol.ReadAsync<JsonElement>(stream).ConfigureAwait(false);
        }
        var result=await WithoutMessagePump(Run);
        Assert.Equal("independent frame",result.GetProperty("message").GetString());
    }
    /// <summary>真实私有管道在窗口同步边界仍可查询并回收；测试不注册根、不读取正文。</summary>
    [Fact(Timeout=30000)]
    public async Task NativeQueryCompletesWithoutCallerMessagePump()
    {
        var path=Path.Combine(Path.GetTempPath(),"HybridMount.UiPipe."+Guid.NewGuid().ToString("N"));await File.WriteAllTextAsync(path,"metadata only");
        try
        {
            await using var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync();
            var expected=LocalFileState.Read(path);var result=await WithoutMessagePump(()=>runtime.InspectAsync(path));
            Assert.Equal(expected,result.Stamp);Assert.Null(result.Placeholder);Assert.True(runtime.IsAvailable);
        }
        finally{File.Delete(path);}
    }
    /// <summary>已预热后台时点击连接也必须离开窗口线程；源工厂明确失败，不注册根或发出远端请求。</summary>
    [Fact(Timeout=30000)]
    public async Task PreparedConnectionRunsNativeChecksAndSourceCreationOutsideCallerContext()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.UiConnect."+Guid.NewGuid().ToString("N"));var local=Path.Combine(area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\UiConnect\"+Guid.NewGuid().ToString("N");
        try
        {
            var repository=new MountRepository(Path.Combine(area,"data"),registry);var mount=new MountDefinition(Guid.NewGuid(),"UI connection regression",local,false,RootIdentity:MountPathGuard.FileIdentity(local),RegistrationStage:MountRegistrationStage.NotRegistered);
            repository.SaveDefinition(mount);await using var logger=new LoggerService(Path.Combine(area,"logs.db"));var factory=new RejectingFactory();await using var manager=new MountManager(repository,logger,factory);
            await NativeRuntimeClient.PrepareConnectionAsync();
            // 断言框架自身也可能捕获上下文；窗口阻塞区内只运行真实连接和不捕获上下文的失败收尾。
            async Task<bool> ConnectAndObserveFailure(){try{await manager.ConnectAsync(mount.Id).ConfigureAwait(false);return false;}catch(IOException){return true;}}
            Assert.True(await WithoutMessagePump(ConnectAndObserveFailure));
            Assert.Equal(1,factory.Calls);Assert.Null(factory.CallerContext);Assert.False(Assert.Single(manager.Mounts).IsConnecting);Assert.Null(CloudFilesNative.RootIdentity(local,true));
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);Directory.Delete(area,true);}
    }
    /// <summary>记录执行线程后明确拒绝连接；无网络、注册或正文副作用。</summary>
    private sealed class RejectingFactory:IFileSourceFactory
    {
        internal int Calls;internal SynchronizationContext? CallerContext;
        public Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token){Calls++;CallerContext=SynchronizationContext.Current;return Task.FromException<IFileSource>(new IOException("isolated connection failure"));}
    }
    /// <summary>阻塞拥有者模拟同步API边界，finally在原上下文外排空回调以使失败测试也完整收尾。</summary>
    private static async Task<T> WithoutMessagePump<T>(Func<Task<T>> work)
    {
        var observed=await Task.Run(()=>
        {
            var context=new HeldContext();var previous=SynchronizationContext.Current;Task<T> task;bool completed;
            SynchronizationContext.SetSynchronizationContext(context);
            try{task=work();completed=task.Wait(TimeSpan.FromSeconds(1));}
            finally{SynchronizationContext.SetSynchronizationContext(previous);context.Drain();}
            return(Task:task,Completed:completed,Posts:context.Posts);
        });
        var result=await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observed.Completed,"IPC完成依赖被同步等待阻塞的调用方上下文。");Assert.Equal(0,observed.Posts);return result;
    }
    /// <summary>模拟窗口调度器；阻塞期间仅记录回调，不在后台冒充UI执行。</summary>
    private sealed class HeldContext:SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback,object? State)> _pending=new();private int _posts;
        internal int Posts=>Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback,object? state){Interlocked.Increment(ref _posts);_pending.Enqueue((callback,state));}
        internal void Drain(){while(_pending.TryDequeue(out var value))value.Callback(value.State);}
    }
    /// <summary>强制真实异步分片，底层自身不捕获上下文；仅用于揭示调用帧的隐藏上下文依赖。</summary>
    private sealed class DelayedStream(MemoryStream inner):Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>true;
        public override long Length=>inner.Length;public override long Position{get=>inner.Position;set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){await Task.Delay(5,token).ConfigureAwait(false);return inner.Read(buffer.Span[..Math.Min(buffer.Length,3)]);}
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default){await Task.Delay(5,token).ConfigureAwait(false);inner.Write(buffer.Span);}
        public override Task FlushAsync(CancellationToken token)=>Task.Delay(5,token);
        public override void Flush()=>throw new NotSupportedException();public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();
    }
    /// <summary>已有配置的构造只建立检查中快照，启动核对在不泵消息的调用上下文外完成。</summary>
    [Fact(Timeout=30000)]
    public async Task StartupInspectionDoesNotBlockCallerContext()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.UiInspect."+Guid.NewGuid().ToString("N"));var local=Path.Combine(area,"view");Directory.CreateDirectory(local);var registry=@"Software\HybridMount.NET.Tests\UiInspect\"+Guid.NewGuid().ToString("N");
        try
        {
            var repository=new MountRepository(Path.Combine(area,"data"),registry);repository.SaveDefinition(new(Guid.NewGuid(),"inspect",local,false,RootIdentity:MountPathGuard.FileIdentity(local),RegistrationStage:MountRegistrationStage.NotRegistered));await using var logger=new LoggerService(Path.Combine(area,"logs.db"));await using var manager=new MountManager(repository,logger,new RejectingFactory());
            Assert.Equal("检查中",Assert.Single(manager.Mounts).Status);
            var state=await WithoutMessagePump(async()=>{await manager.StartAsync().ConfigureAwait(false);return Assert.Single(manager.Mounts).Status;});Assert.Equal("未连接",state);
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);Directory.Delete(area,true);}
    }
    /// <summary>新增和凭据保存即使预热后台也离开UI；工厂只记录上下文并拒绝，不联网或注册根。</summary>
    [Fact(Timeout=30000)]
    public async Task AddAndCredentialSaveLeaveCallerContext()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.UiEdit."+Guid.NewGuid().ToString("N"));var local=Path.Combine(area,"view");Directory.CreateDirectory(local);var registry=@"Software\HybridMount.NET.Tests\UiEdit\"+Guid.NewGuid().ToString("N");
        try
        {
            var repository=new MountRepository(Path.Combine(area,"data"),registry);await using var logger=new LoggerService(Path.Combine(area,"logs.db"));var factory=new RejectingFactory();await using var manager=new MountManager(repository,logger,factory);await NativeRuntimeClient.PrepareConnectionAsync();
            async Task<bool> Add(){try{await manager.AddAsync("edit",local,false,source:new(FileSourceKind.WebDav,"https://example.invalid/")).ConfigureAwait(false);return false;}catch(IOException){return true;}}
            Assert.True(await WithoutMessagePump(Add));var id=Assert.Single(manager.Mounts).Definition.Id;
            async Task<bool> Edit(){try{await manager.UpdateCredentialsAsync(id,new("user","test")).ConfigureAwait(false);return false;}catch(IOException){return true;}}
            Assert.True(await WithoutMessagePump(Edit));Assert.Equal(2,factory.Calls);Assert.Null(factory.CallerContext);Assert.Null(CloudFilesNative.RootIdentity(local,true));
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);Directory.Delete(area,true);}
    }
}
