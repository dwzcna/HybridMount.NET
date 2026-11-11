using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
namespace HybridMount.Services.Runtime;

/// <summary>当前进程拥有的原生运行时客户端；私有双管道认证，控制读循环从不等待UI或正文。</summary>
internal sealed class NativeRuntimeClient:ICloudFilesRuntime,IAsyncDisposable
{
    /// <summary>合作处理期限；生产默认两分钟，内部验证可缩短，不是提前ACK或终止处理器的期限。</summary>
    internal TimeSpan CheckpointHandlerTimeout {get;set;}=TimeSpan.FromMinutes(2);
    /// <summary>停止诊断期限；超时记录结果待核对并保留处理器拥有的资源。</summary>
    internal TimeSpan CheckpointStopTimeout {get;set;}=TimeSpan.FromSeconds(10);
    private int _checkpointStopReported;
#if HYBRIDMOUNT_HEADLESS_TEST
    internal Action? CheckpointStopTimeoutForTests {get;set;}
    /// <summary>测试在真实阶段前后注入取消，按挂载过滤；生产不包含此观察入口。</summary>
    internal Func<Guid?,string,bool,bool,CancellationToken,Task>? CheckpointForTests {get;set;}
#endif
    private sealed class Pending(Func<string,JsonElement,CancellationToken,Task<object?>>? checkpoint,CancellationToken token=default,Guid? mount=null)
    {
        internal readonly TaskCompletionSource<JsonElement> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Func<string,JsonElement,CancellationToken,Task<object?>>? Checkpoint=checkpoint;
        internal readonly CancellationToken Token=token;
        internal readonly Guid? Mount=mount;
        internal int Canceled;
        internal int Retiring;
        internal int Irreversible;
        internal Exception? CheckpointFailure;
    }
    private static readonly object SharedGate=new();private static Task<NativeRuntimeClient>? _shared;
    private readonly NamedPipeServerStream _control,_data;
    private readonly SemaphoreSlim _write=new(1,1),_dataWrite=new(1,1),_dataCredits=new(4,4);
    private readonly ConcurrentDictionary<string,Pending> _pending=new();
    private readonly ConcurrentDictionary<Guid,string> _generations=new();
    private sealed record ReadBlock(Guid Lease,long Offset,int Count,TaskCompletionSource<byte[]> Completion);
    private readonly ConcurrentDictionary<Guid,ReadBlock> _blocks=new();
    private Task _dataReader=Task.CompletedTask;
    private readonly object _checkpointGate=new();private readonly ConcurrentDictionary<long,Guid?> _checkpointMounts=new();
    private readonly ConcurrentDictionary<long,Task> _checkpoints=new();private long _nextCheckpoint;
    private readonly CancellationTokenSource _stop=new();private readonly Process _process;private readonly SafeFileHandle _job;
    private Task _reader=Task.CompletedTask,_stderr=Task.CompletedTask;private Task? _dispose;
    private int _failed;
    private readonly NativeEventInbox _events=new();
    internal string EventStatistics=>_events.Statistics;
    private Task _eventWorker=Task.CompletedTask,_previewWorker=Task.CompletedTask;
    private readonly Channel<NativeRuntimeEvent> _previewEvents=Channel.CreateBounded<NativeRuntimeEvent>(new BoundedChannelOptions(16){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true});
    internal event Action<NativeRuntimeEvent>? EventReceived;
    internal event Action<Exception>? Failed;
    internal int ProcessId=>_process.Id;
    internal bool IsAvailable=>!_stop.IsCancellationRequested&&Volatile.Read(ref _failed)==0;
    private NativeRuntimeClient(NamedPipeServerStream control,NamedPipeServerStream data,Process process,SafeFileHandle job){_control=control;_data=data;_process=process;_job=job;}
    /// <summary>按需创建空后台；故障后仅重建传输，不恢复任何根连接或重放已经发送的操作。</summary>
    internal static Task<NativeRuntimeClient> GetAsync()
    {
        lock(SharedGate)
        {
            if(_shared is {IsCompletedSuccessfully:true} current&&!current.Result.IsAvailable)_shared=RestartAsync(current);
            else if(_shared is {IsFaulted:true} or {IsCanceled:true})_shared=StartAsync();
            return _shared??=StartAsync();
        }
        static async Task<NativeRuntimeClient> RestartAsync(Task<NativeRuntimeClient> old){await(await old.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);return await StartAsync().ConfigureAwait(false);}
    }
    internal static NativeRuntimeClient Get()=>GetAsync().GetAwaiter().GetResult();
    internal static async Task StopSharedAsync(){Task<NativeRuntimeClient>? current;lock(SharedGate){current=_shared;_shared=null;}if(current is not null){try{await(await current.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);}catch(Exception){/* 启动失败已经由调用方记录，不再次改变退出结果。 */}}}
    /// <summary>只在明确连接时重建故障后台；从不重放原进程中的操作或自动接管既有会话。</summary>
    internal static Task PrepareConnectionAsync()=>GetAsync();
#if HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>测试独占运行时，不停止其他夹具的共享后台；只配合独立测试编译的原生映像。</summary>
    internal static Task<NativeRuntimeClient> StartOwnedForTestsAsync(bool exitDuringHandshake=false)=>StartAsync(exitDuringHandshake);
#endif
    private static async Task<NativeRuntimeClient> StartAsync(bool exitDuringHandshake=false)
    {
        var nonce=Guid.NewGuid().ToString("N");var sid=WindowsIdentity.GetCurrent().User?.Value??throw new IOException("当前账户无SID。");var prefix=$"{ApplicationPaths.ProductScope}.runtime.v1.{sid}.{Process.GetCurrentProcess().SessionId}.{nonce}";
        var control=new NamedPipeServerStream(prefix+".control",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,65536,65536);
        var data=new NamedPipeServerStream(prefix+".data",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,65536,65536);
        Process? process=null;SafeFileHandle? job=null;NativeRuntimeClient? owner=null;
        try
        {
            var path=Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe");
            VerifyExecutable(path);
            var info=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true};info.ArgumentList.Add(exitDuringHandshake?"--invalid-handshake-test-role":"--runtime");info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));info.ArgumentList.Add(nonce);
            process=Process.Start(info)??throw new IOException("未能启动原生运行时。");
            job=CreateJobObject(0,null);if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits=new ExtendedLimits{Basic=new(){Flags=0x2000}};if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>())||!AssignProcessToJobObject(job,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
            owner=new(control,data,process,job);owner._stderr=process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));var token=deadline.Token;
            var handshake=HandshakeAsync(control,data,process,nonce,token);var exited=process.WaitForExitAsync(token);
            try
            {
                if(await Task.WhenAny(handshake,exited).ConfigureAwait(false)==exited){await exited.ConfigureAwait(false);throw HandshakeExited(process.ExitCode);}
                await handshake.ConfigureAwait(false);
            }
            finally
            {
                await deadline.CancelAsync().ConfigureAwait(false);
                try{await handshake.ConfigureAwait(false);}catch(Exception) { }
                try{await exited.ConfigureAwait(false);}catch(Exception) { }
            }
            owner._eventWorker=owner.EventLoopAsync();owner._previewWorker=owner.PreviewLoopAsync();owner._reader=owner.ReadLoopAsync();owner._dataReader=owner.DataLoopAsync();return owner;
        }
        catch(Exception error)
        {
            int? exitCode=process is {HasExited:true}?process.ExitCode:null;
            control.Dispose();data.Dispose();job?.Dispose();if(process is not null){try{if(!process.HasExited){process.Kill();await process.WaitForExitAsync().ConfigureAwait(false);}if(owner is not null)try{await owner._stderr.ConfigureAwait(false);}catch(IOException){/* 创建失败只观察本次stderr收尾，保留握手原因。 */}}finally{process.Dispose();}}
            if(exitCode is {} code)throw HandshakeExited(code,error);
            if(error is OperationCanceledException)throw new IOException("原生后台握手超时，请检查安装文件并重试。",error);
            throw;
        }
    }
    /// <summary>仅核验本次部署的原生映像存在；失败提供可操作提示，不启动替代程序。</summary>
    internal static void VerifyExecutable(string path){if(!File.Exists(path))throw new IOException("未找到 HybridMount.Stock.exe，请重新生成或安装");}
    /// <summary>握手提前退出只报告数字退出码；保留安全的原始原因。</summary>
    internal static IOException HandshakeExited(int code,Exception? inner=null)=>new($"原生后台在握手期间退出（退出码：0x{code:X8}）。请检查原生后台诊断记录。",inner);
    /// <summary>等待双通道并核验本次子进程后交换版本；取消关闭本次握手，不启动事件消费者。</summary>
    private static async Task HandshakeAsync(NamedPipeServerStream control,NamedPipeServerStream data,Process process,string nonce,CancellationToken token)
    {
        await Task.WhenAll(control.WaitForConnectionAsync(token),data.WaitForConnectionAsync(token)).ConfigureAwait(false);
        VerifyPeer(control,process);VerifyPeer(data,process);
        var hello=await IpcProtocol.ReadAsync<JsonElement>(control,token).ConfigureAwait(false);VerifyHello(hello,nonce,process.Id);
        var build=typeof(NativeRuntimeClient).Assembly.GetName().Version!.ToString();
        await IpcProtocol.WriteAsync(control,new{kind="helloAck",version=1,nonce,build},token).ConfigureAwait(false);
    }
    /// <summary>认证字段必须先一致，再报告经白名单处理的版本差异；不接受缺失版本。</summary>
    internal static void VerifyHello(JsonElement hello,string nonce,int processId)
    {
        if(hello.GetProperty("kind").GetString()!="hello"||hello.GetProperty("version").GetInt32()!=1||hello.GetProperty("nonce").GetString()!=nonce||hello.GetProperty("scope").GetString()!=ApplicationPaths.ProductScope||hello.GetProperty("processId").GetInt32()!=processId)throw new IOException("原生运行时握手不匹配。");
        var build=typeof(NativeRuntimeClient).Assembly.GetName().Version!.ToString();var actual=hello.TryGetProperty("build",out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
        if(actual!=build){var safe=actual is {Length:>0 and <=64}&&actual.All(c=>c is >= '0' and <= '9' or '.')?actual:"未知";throw new IOException($"原生后台版本({safe})与主程序({build})不一致，请重新生成");}
    }
    private static void VerifyPeer(NamedPipeServerStream pipe,Process child)
    {
        if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var process)||process!=child.Id||child.SessionId!=Process.GetCurrentProcess().SessionId)throw new IOException("原生通道对端不属于本次子进程。");
        if(!string.Equals(child.MainModule?.FileName,Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("原生对端部署路径不匹配。");
    }
    /// <summary>调用者取消时通知原生端并等实际终态；已经开始的写入仍由其安全收尾。</summary>
    public Task<JsonElement> InvokeAsync(string operation,object data,Func<string,JsonElement,CancellationToken,Task<object?>>? checkpoint=null,CancellationToken token=default)=>InvokeCoreAsync(operation,data,checkpoint,token);
    /// <summary>本地正文的成功终态须先排空数据帧，再观察调用方取消；其他操作保留原有取消语义。</summary>
    private async Task<JsonElement> InvokeCoreAsync(string operation,object data,Func<string,JsonElement,CancellationToken,Task<object?>>? checkpoint=null,CancellationToken token=default,bool observeSuccessfulCancellation=true)
    {
        token.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested,this);var payload=NativeRuntimeProtocol.Element(data);
        Guid? mount=payload.TryGetProperty("mountId",out var mountValue)&&Guid.TryParse(mountValue.GetString(),out var parsedMount)?parsedMount:null;
        if(operation!="connectRoot"&&mount is {} known&&!payload.TryGetProperty("sessionId",out _)&&_generations.TryGetValue(known,out var generation))
        {var fields=payload.EnumerateObject().ToDictionary(item=>item.Name,item=>(object?)item.Value.Clone());fields["sessionId"]=generation;payload=NativeRuntimeProtocol.Element(fields);}
        var id=Guid.NewGuid().ToString("N");var pending=new Pending(checkpoint,token,mount);if(!_pending.TryAdd(id,pending))throw new IOException("原生请求ID重复。");
        try
        {
            await WriteAsync(new{kind="command",requestId=id,operation,data=payload}).ConfigureAwait(false);
            using var cancel=token.Register(()=>{Interlocked.Exchange(ref pending.Canceled,1);_ = SendCancelAsync(id);});
            JsonElement result;
            try{result=await pending.Completion.Task.ConfigureAwait(false);}
            catch(COMException error)when(token.IsCancellationRequested&&error.HResult==unchecked((int)0x800703e3)){throw new OperationCanceledException(token);}
            if(mount is {} mounted){if(operation=="connectRoot"){_generations[mounted]=payload.GetProperty("sessionId").GetString()!;}else if(operation=="disconnectRoot")_generations.TryRemove(mounted,out _);}
            if(pending.Irreversible==0&&observeSuccessfulCancellation)token.ThrowIfCancellationRequested();return result;
        }
        finally{_pending.TryRemove(id,out _);}
    }
    private async Task SendCancelAsync(string id){try{await WriteAsync(new{kind="cancel",requestId=id}).ConfigureAwait(false);}catch(Exception){/* 通道故障统一由读循环唤醒并报告。 */}}
    private async Task WriteAsync(object value)
    {
        await _write.WaitAsync(_stop.Token).ConfigureAwait(false);try{await IpcProtocol.WriteAsync(_control,value,_stop.Token).ConfigureAwait(false);}finally{_write.Release();}
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                var message=await IpcProtocol.ReadAsync<JsonElement>(_control,_stop.Token).ConfigureAwait(false);var kind=message.GetProperty("kind").GetString();
                if(kind=="response")
                {
                    var id=message.GetProperty("requestId").GetString()!;if(!_pending.TryGetValue(id,out var request))continue;
                    var code=message.GetProperty("hresult").GetInt32();var stage=FailureStage(message.GetProperty("data"));if(code<0)request.Completion.TrySetException(request.CheckpointFailure??new COMException($"原生操作未完成（HRESULT=0x{code:X8}；{stage}）。",code));else request.Completion.TrySetResult(message.GetProperty("data").Clone());
                }
                else if(kind=="checkpoint")
                {
                    lock(_checkpointGate){var key=Interlocked.Increment(ref _nextCheckpoint);var captured=message.Clone();_checkpointMounts[key]=_pending.TryGetValue(message.GetProperty("requestId").GetString()!,out var owner)?owner.Mount:null;var work=Task.Run(()=>HandleCheckpointAsync(captured));_checkpoints[key]=work;_ = work.ContinueWith(completed=>{_checkpoints.TryRemove(key,out _);_checkpointMounts.TryRemove(key,out _);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);}
                }
                else if(kind=="event")
                {
                    var value=NativeRuntimeProtocol.Read<NativeRuntimeEvent>(message);if(value.Event is "previewDemand" or "renameRejected"){_previewEvents.Writer.TryWrite(value);continue;}if(!_events.TryWrite(value))throw new IOException("原生事件队列已满，停止当前连接等待核对。");
                }
                else throw new IOException("原生消息类型无效。");
            }
        }
        catch(Exception error){if(!_stop.IsCancellationRequested)Fail(error);}
        finally{foreach(var item in _pending.Values)item.Completion.TrySetException(new IOException("原生运行时已断开；操作结果须核对。"));}
    }
    /// <summary>原生失败阶段只接受固定名称或有界JSON错误编号，忽略业务正文。</summary>
    internal static string FailureStage(JsonElement data)
    {
        if(!data.TryGetProperty("failureStage",out var value)||value.ValueKind!=JsonValueKind.String)return "operation";
        var stage=value.GetString();
        return stage is "local-read-canceled" or "local-read-close-pending" or "root-enumeration" or "root-native-query" or "root-register" or "orphan-inspect" or "orphan-register" or "orphan-connect" or "orphan-clear" or "std-exception" or "out-of-memory"||stage is {Length:>5 and <=16}&&stage.StartsWith("json:",StringComparison.Ordinal)&&stage[5..].All(char.IsAsciiDigit)?stage!:"operation";
    }
    private async Task EventLoopAsync()
    {
        try{await foreach(var value in _events.ReadAllAsync(_stop.Token).ConfigureAwait(false))if(EventReceived is {} listeners)foreach(Action<NativeRuntimeEvent> listener in listeners.GetInvocationList())try{listener(value);}catch(Exception error){Fail(error);}}
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    /// <summary>可丢弃预览及Shell说明独立消费；会话记录诊断，提示失败绝不停止挂载或关键事件。</summary>
    private async Task PreviewLoopAsync()
    {
        try{await foreach(var value in _previewEvents.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))if(EventReceived is {} listeners)foreach(Action<NativeRuntimeEvent> listener in listeners.GetInvocationList())try{listener(value);}catch(Exception){/* 会话拥有预览业务诊断；其他根仍继续。 */}}
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    private async Task HandleCheckpointAsync(JsonElement message)
    {
        var code=0;object result=new{};Pending? owner=null;
        try
        {
            Pending request;var stage=message.GetProperty("stage").GetString()!;var safety=message.TryGetProperty("safety",out var safetyValue)&&safetyValue.GetBoolean();
            lock(_checkpointGate)
            {
                if(!_pending.TryGetValue(message.GetProperty("requestId").GetString()!,out request!)||request.Checkpoint is null||request.Canceled!=0&&!safety||request.Retiring!=0)throw new OperationCanceledException();
                owner=request;
            }
            using var deadline=new CancellationTokenSource(CheckpointHandlerTimeout);
            using var warning=deadline.Token.Register(()=>Trace.TraceWarning("原生检查点处理超过期限，已请求合作取消；实际处理器结束后才回复。"));
            using var cancel=safety?CancellationTokenSource.CreateLinkedTokenSource(deadline.Token,CancellationToken.None):CancellationTokenSource.CreateLinkedTokenSource(request.Token,_stop.Token,deadline.Token);
#if HYBRIDMOUNT_HEADLESS_TEST
            if(CheckpointForTests is {} before)await before(request.Mount,stage,safety,false,cancel.Token).ConfigureAwait(false);
#endif
            result=await request.Checkpoint!(stage,message.GetProperty("data").Clone(),cancel.Token).ConfigureAwait(false)??new{};
#if HYBRIDMOUNT_HEADLESS_TEST
            if(CheckpointForTests is {} after)await after(request.Mount,stage,safety,true,cancel.Token).ConfigureAwait(false);
#endif
            if(message.TryGetProperty("beginIrreversible",out var irreversible)&&irreversible.GetBoolean())Interlocked.Exchange(ref request.Irreversible,1);
        }
        catch(Exception error){if(owner is not null)Interlocked.CompareExchange(ref owner.CheckpointFailure,error,null);code=error is OperationCanceledException?unchecked((int)0x800703e3):error.HResult;}
        try{await WriteAsync(new{kind="checkpointAck",checkpointId=message.GetProperty("checkpointId").GetString(),hresult=code,data=NativeRuntimeProtocol.Element(result)}).ConfigureAwait(false);}catch(Exception error){if(!_stop.IsCancellationRequested)Fail(error);}
    }
    /// <summary>关键原生状态无法确认时封闭此通道，保留挂载注册与恢复数据，禁止伪造成功。</summary>
    internal void AbortForSafety(Exception error)=>Fail(error);
    private void Fail(Exception error)
    {
        if(Interlocked.Exchange(ref _failed,1)!=0)return;_ = _stop.CancelAsync();_control.Dispose();_data.Dispose();foreach(var request in _pending.Values)request.Completion.TrySetException(new IOException("原生运行时连接中断；已发送操作的结果须核对。",error));
        if(Failed is {} listeners)foreach(Action<Exception> listener in listeners.GetInvocationList())try{listener(error);}catch(Exception){}
    }
    /// <summary>最多四个256KiB块在途；帧内写入只响应整体运行时停止，单次取消不截断帧。</summary>
    internal async Task TransferAsync(Guid fetchId,long offset,ReadOnlyMemory<byte> bytes,CancellationToken token)
    {
        if(offset<0||bytes.Length is <1 or >NativeRuntimeProtocol.BlockBytes)throw new IOException("原生正文块无效。");token.ThrowIfCancellationRequested();await _dataCredits.WaitAsync(token).ConfigureAwait(false);var id=Guid.NewGuid();var key=id.ToString("N");var pending=new Pending(null);_pending[key]=pending;
        try
        {
            var header=new byte[44];BinaryPrimitives.WriteUInt32LittleEndian(header,(uint)(40+bytes.Length));id.TryWriteBytes(header.AsSpan(4));fetchId.TryWriteBytes(header.AsSpan(20));BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(36),offset);
            await _dataWrite.WaitAsync(_stop.Token).ConfigureAwait(false);try{await _data.WriteAsync(header,_stop.Token).ConfigureAwait(false);await _data.WriteAsync(bytes,_stop.Token).ConfigureAwait(false);}finally{_dataWrite.Release();}
            await pending.Completion.Task.ConfigureAwait(false);token.ThrowIfCancellationRequested();
        }
        finally{_pending.TryRemove(key,out _);_dataCredits.Release();}
    }
    /// <summary>正文读取独立于控制循环；帧必须对应本地租约和精确请求，不接纳主动推送。</summary>
    private async Task DataLoopAsync()
    {
        try
        {
            var header=new byte[44];while(!_stop.IsCancellationRequested)
            {
                await _data.ReadExactlyAsync(header,_stop.Token).ConfigureAwait(false);var size=BinaryPrimitives.ReadUInt32LittleEndian(header);if(size<40||size>40+NativeRuntimeProtocol.BlockBytes)throw new IOException("原生本地正文帧长度无效。");
                var id=new Guid(header.AsSpan(4,16));var lease=new Guid(header.AsSpan(20,16));var offset=BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(36));var count=checked((int)size-40);
                if(!_blocks.TryGetValue(id,out var pending)||pending.Lease!=lease||pending.Offset!=offset||pending.Count!=count)throw new IOException("原生本地正文身份无效。");
                var bytes=new byte[count];await _data.ReadExactlyAsync(bytes,_stop.Token).ConfigureAwait(false);pending.Completion.TrySetResult(bytes);
            }
        }
        catch(Exception error)when(error is IOException or OperationCanceledException or ObjectDisposedException){if(!_stop.IsCancellationRequested)Fail(error);}
        finally{foreach(var pending in _blocks.Values)pending.Completion.TrySetException(new IOException("原生本地读取已断开。"));}
    }
    internal async Task<byte[]> ReadLocalRangeAsync(Guid mount,Guid lease,long offset,int count,CancellationToken token)
    {
        var id=Guid.NewGuid();var completion=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);_blocks[id]=new(lease,offset,count,completion);
        try
        {
            token.ThrowIfCancellationRequested();
            // 单独捕获取消信号，避免Invoke在成功应答后提前抛出而移除尚在数据通道的请求。
            await InvokeCoreAsync("readLocalRange",new{mountId=mount,leaseId=lease,readId=id,offset,count},token:token,observeSuccessfulCancellation:false).ConfigureAwait(false);
            var bytes=await completion.Task.WaitAsync(_stop.Token).ConfigureAwait(false);token.ThrowIfCancellationRequested();return bytes;
        }
        finally{_blocks.TryRemove(id,out _);}
    }
    public async Task<NativeFileSnapshot> InspectAsync(string path,bool persisted=false,bool ranges=false,string? expectedIdentity=null,CancellationToken token=default)=>NativeRuntimeProtocol.Read<NativeFileSnapshot>(await InvokeAsync("queryFile",new{path,persisted,ranges,expectedIdentity},token:token).ConfigureAwait(false));
    public Task<JsonElement> RootInfoAsync(string path,CancellationToken token=default)=>InvokeAsync("queryRoot",new{path},token:token);
    internal JsonElement Invoke(string operation,object data,Func<string,JsonElement,CancellationToken,Task<object?>>? checkpoint=null,CancellationToken token=default)=>InvokeAsync(operation,data,checkpoint,token).GetAwaiter().GetResult();
    public ValueTask DisposeAsync(){lock(_pending)return new(_dispose??=StopAsync());}
    /// <summary>停止达到诊断期限后继续持有处理器所有权，直到实际结束；不能把超时当成功或让业务回调越过拥有者释放。</summary>
    private async Task AwaitCheckpointOwnersAsync(Task work)
    {
        try{await work.WaitAsync(CheckpointStopTimeout).ConfigureAwait(false);}
        catch(TimeoutException)
        {
            if(Interlocked.Exchange(ref _checkpointStopReported,1)==0)
            {
#if HYBRIDMOUNT_HEADLESS_TEST
                if(CheckpointStopTimeoutForTests is {} report)report();else
#endif
                HostDiagnosticStore.Record("NativeRuntime",new NativeCheckpointDrainTimeoutException());
                Trace.TraceWarning("原生检查点停止超过诊断期限，结果待核对；保留所有者并等待处理器结束。");
            }
            await work.ConfigureAwait(false);
        }
    }
    private sealed class NativeCheckpointDrainTimeoutException:TimeoutException { }
    /// <summary>挂载协调器释放业务服务前封闭本组新检查点并排空已有处理器；不等待其他挂载的有效任务。</summary>
    internal static async Task DrainSharedCheckpointsAsync(IReadOnlyCollection<Guid> mounts)
    {
        Task<NativeRuntimeClient>? shared;lock(SharedGate)shared=_shared;if(shared is null)return;NativeRuntimeClient runtime;
        try{runtime=await shared.ConfigureAwait(false);}catch(Exception){return;}
        var ids=mounts.ToHashSet();Task[] work;lock(runtime._checkpointGate){foreach(var pending in runtime._pending.Values)if(pending.Mount is {} id&&ids.Contains(id))pending.Retiring=1;work=runtime._checkpoints.Where(x=>runtime._checkpointMounts.TryGetValue(x.Key,out var mount)&&mount is {} id&&ids.Contains(id)).Select(x=>x.Value).ToArray();}
        await runtime.AwaitCheckpointOwnersAsync(Task.WhenAll(work)).ConfigureAwait(false);
    }
    private async Task StopAsync()
    {
        var cancellation=_stop.CancelAsync();_control.Dispose();_data.Dispose();await _reader.ConfigureAwait(false);await _dataReader.ConfigureAwait(false);_events.Complete();_previewEvents.Writer.TryComplete();
        await AwaitCheckpointOwnersAsync(Task.WhenAll(_checkpoints.Values.Concat(new[]{_eventWorker,_previewWorker,cancellation}))).ConfigureAwait(false);
        try{await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);}catch(TimeoutException){_job.Dispose();await _process.WaitForExitAsync().ConfigureAwait(false);}
        await _stderr.ConfigureAwait(false);_job.Dispose();_process.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits{public long ProcessTime,JobTime;public uint Flags;public nuint MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcesses;public nuint Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong Reads,Writes,Other,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public nuint ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeFileHandle CreateJobObject(nint security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref ExtendedLimits info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint process);
}
