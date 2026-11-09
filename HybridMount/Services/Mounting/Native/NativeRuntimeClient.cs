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
namespace HybridMount.Services.Mounting;

/// <summary>当前进程拥有的原生运行时客户端；私有双管道认证，控制读循环从不等待UI或正文。</summary>
internal sealed class NativeRuntimeClient:ICloudFilesRuntime,IAsyncDisposable
{
    private static readonly TimeSpan CheckpointHandlerTimeout=TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckpointStopTimeout=TimeSpan.FromSeconds(10);
    private sealed class Pending(Func<string,JsonElement,CancellationToken,Task<object?>>? checkpoint,CancellationToken token=default)
    {
        internal readonly TaskCompletionSource<JsonElement> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Func<string,JsonElement,CancellationToken,Task<object?>>? Checkpoint=checkpoint;
        internal readonly CancellationToken Token=token;
        internal int Canceled;
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
    private readonly ConcurrentDictionary<long,Task> _checkpoints=new();private long _nextCheckpoint;
    private readonly CancellationTokenSource _stop=new();private readonly Process _process;private readonly SafeFileHandle _job;
    private Task _reader=Task.CompletedTask,_stderr=Task.CompletedTask;private Task? _dispose;
    private int _failed;
    private readonly Channel<NativeRuntimeEvent> _events=Channel.CreateBounded<NativeRuntimeEvent>(512);
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
    internal static Task<NativeRuntimeClient> StartOwnedForTestsAsync()=>StartAsync();
#endif
    private static async Task<NativeRuntimeClient> StartAsync()
    {
        var nonce=Guid.NewGuid().ToString("N");var sid=WindowsIdentity.GetCurrent().User?.Value??throw new IOException("当前账户无SID。");var prefix=$"{ApplicationPaths.ProductScope}.runtime.v1.{sid}.{Process.GetCurrentProcess().SessionId}.{nonce}";
        var control=new NamedPipeServerStream(prefix+".control",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,65536,65536);
        var data=new NamedPipeServerStream(prefix+".data",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly,65536,65536);
        Process? process=null;SafeFileHandle? job=null;NativeRuntimeClient? owner=null;
        try
        {
            var path=Path.Combine(AppContext.BaseDirectory,"HybridMount.Native.exe");
            var info=new ProcessStartInfo(path){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true};info.ArgumentList.Add("--runtime");info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));info.ArgumentList.Add(nonce);
            process=Process.Start(info)??throw new IOException("未能启动原生运行时。");
            job=CreateJobObject(0,null);if(job.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits=new ExtendedLimits{Basic=new(){Flags=0x2000}};if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>())||!AssignProcessToJobObject(job,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
            owner=new(control,data,process,job);owner._stderr=process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(12));var token=deadline.Token;
            await Task.WhenAll(control.WaitForConnectionAsync(token),data.WaitForConnectionAsync(token)).ConfigureAwait(false);
            VerifyPeer(control,process);VerifyPeer(data,process);
            var hello=await IpcProtocol.ReadAsync<JsonElement>(control,token).ConfigureAwait(false);
            var build=typeof(NativeRuntimeClient).Assembly.GetName().Version!.ToString();
            if(hello.GetProperty("kind").GetString()!="hello"||hello.GetProperty("version").GetInt32()!=1||hello.GetProperty("nonce").GetString()!=nonce||hello.GetProperty("scope").GetString()!=ApplicationPaths.ProductScope||hello.GetProperty("processId").GetInt32()!=process.Id||hello.GetProperty("build").GetString()!=build)throw new IOException("原生运行时握手不匹配。");
            await IpcProtocol.WriteAsync(control,new{kind="helloAck",version=1,nonce,build},token).ConfigureAwait(false);owner._eventWorker=owner.EventLoopAsync();owner._previewWorker=owner.PreviewLoopAsync();owner._reader=owner.ReadLoopAsync();owner._dataReader=owner.DataLoopAsync();return owner;
        }
        catch
        {
            control.Dispose();data.Dispose();job?.Dispose();if(process is not null){try{if(!process.HasExited){process.Kill();await process.WaitForExitAsync().ConfigureAwait(false);}}finally{process.Dispose();}}
            throw;
        }
    }
    private static void VerifyPeer(NamedPipeServerStream pipe,Process child)
    {
        if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var process)||process!=child.Id||child.SessionId!=Process.GetCurrentProcess().SessionId)throw new IOException("原生通道对端不属于本次子进程。");
        if(!string.Equals(child.MainModule?.FileName,Path.Combine(AppContext.BaseDirectory,"HybridMount.Native.exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("原生对端部署路径不匹配。");
    }
    /// <summary>调用者取消时通知原生端并等实际终态；已经开始的写入仍由其安全收尾。</summary>
    public async Task<JsonElement> InvokeAsync(string operation,object data,Func<string,JsonElement,CancellationToken,Task<object?>>? checkpoint=null,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested,this);var payload=NativeRuntimeProtocol.Element(data);
        Guid? mount=payload.TryGetProperty("mountId",out var mountValue)&&Guid.TryParse(mountValue.GetString(),out var parsedMount)?parsedMount:null;
        if(operation!="connectRoot"&&mount is {} known&&!payload.TryGetProperty("sessionId",out _)&&_generations.TryGetValue(known,out var generation))
        {var fields=payload.EnumerateObject().ToDictionary(item=>item.Name,item=>(object?)item.Value.Clone());fields["sessionId"]=generation;payload=NativeRuntimeProtocol.Element(fields);}
        var id=Guid.NewGuid().ToString("N");var pending=new Pending(checkpoint,token);if(!_pending.TryAdd(id,pending))throw new IOException("原生请求ID重复。");
        try
        {
            await WriteAsync(new{kind="command",requestId=id,operation,data=payload}).ConfigureAwait(false);
            using var cancel=token.Register(()=>{Interlocked.Exchange(ref pending.Canceled,1);_ = SendCancelAsync(id);});
            JsonElement result;
            try{result=await pending.Completion.Task.ConfigureAwait(false);}
            catch(COMException error)when(token.IsCancellationRequested&&error.HResult==unchecked((int)0x800703e3)){throw new OperationCanceledException(token);}
            if(mount is {} mounted){if(operation=="connectRoot")_generations[mounted]=payload.GetProperty("sessionId").GetString()!;else if(operation=="disconnectRoot")_generations.TryRemove(mounted,out _);}
            if(pending.Irreversible==0)token.ThrowIfCancellationRequested();return result;
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
                    var code=message.GetProperty("hresult").GetInt32();var stage=message.GetProperty("data").TryGetProperty("failureStage",out var nativeStage)&&nativeStage.GetString() is "root-enumeration" or "root-native-query" or "root-register"?nativeStage.GetString():"operation";if(code<0)request.Completion.TrySetException(request.CheckpointFailure??new COMException($"原生操作未完成（HRESULT=0x{code:X8}；{stage}）。",code));else request.Completion.TrySetResult(message.GetProperty("data").Clone());
                }
                else if(kind=="checkpoint")
                {
                    var key=Interlocked.Increment(ref _nextCheckpoint);var captured=message.Clone();var work=Task.Run(()=>HandleCheckpointAsync(captured));_checkpoints[key]=work;_ = work.ContinueWith(completed=>_checkpoints.TryRemove(key,out _),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
                }
                else if(kind=="event")
                {
                    var value=NativeRuntimeProtocol.Read<NativeRuntimeEvent>(message);if(value.Event=="previewDemand"){_previewEvents.Writer.TryWrite(value);continue;}if(!_events.Writer.TryWrite(value))throw new IOException("原生事件队列已满，停止当前连接等待核对。");
                }
                else throw new IOException("原生消息类型无效。");
            }
        }
        catch(Exception error){if(!_stop.IsCancellationRequested)Fail(error);}
        finally{foreach(var item in _pending.Values)item.Completion.TrySetException(new IOException("原生运行时已断开；操作结果须核对。"));}
    }
    private async Task EventLoopAsync()
    {
        try{await foreach(var value in _events.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))if(EventReceived is {} listeners)foreach(Action<NativeRuntimeEvent> listener in listeners.GetInvocationList())try{listener(value);}catch(Exception error){Fail(error);}}
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    /// <summary>可丢弃预览提示独立消费；会话记录诊断，提示失败绝不停止挂载或关键事件。</summary>
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
            if(!_pending.TryGetValue(message.GetProperty("requestId").GetString()!,out var request)||(request.Canceled!=0&&!((request.Irreversible!=0||message.GetProperty("stage").GetString()=="pinReleasing")&&message.TryGetProperty("safety",out var safety)&&safety.GetBoolean()))||request.Checkpoint is null)throw new OperationCanceledException();
            owner=request;
            using var deadline=new CancellationTokenSource(CheckpointHandlerTimeout);
            using var warning=deadline.Token.Register(()=>Trace.TraceWarning("原生检查点处理超过期限，已请求取消；等待处理器实际结束后才回复。"));
            using var cancel=CancellationTokenSource.CreateLinkedTokenSource(request.Token,_stop.Token,deadline.Token);
            result=await request.Checkpoint(message.GetProperty("stage").GetString()!,message.GetProperty("data").Clone(),cancel.Token).ConfigureAwait(false)??new{};
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
        try{token.ThrowIfCancellationRequested();await InvokeAsync("readLocalRange",new{mountId=mount,leaseId=lease,readId=id,offset,count}).ConfigureAwait(false);var bytes=await completion.Task.WaitAsync(_stop.Token).ConfigureAwait(false);token.ThrowIfCancellationRequested();return bytes;}
        finally{_blocks.TryRemove(id,out _);}
    }
    public async Task<NativeFileSnapshot> InspectAsync(string path,bool persisted=false,bool ranges=false,string? expectedIdentity=null,CancellationToken token=default)=>NativeRuntimeProtocol.Read<NativeFileSnapshot>(await InvokeAsync("queryFile",new{path,persisted,ranges,expectedIdentity},token:token).ConfigureAwait(false));
    public Task<JsonElement> RootInfoAsync(string path,CancellationToken token=default)=>InvokeAsync("queryRoot",new{path},token:token);
    internal JsonElement Invoke(string operation,object data,Func<string,JsonElement,CancellationToken,Task<object?>>? checkpoint=null,CancellationToken token=default)=>InvokeAsync(operation,data,checkpoint,token).GetAwaiter().GetResult();
    public ValueTask DisposeAsync(){lock(_pending)return new(_dispose??=StopAsync());}
    private async Task StopAsync()
    {
        var cancellation=_stop.CancelAsync();_control.Dispose();_data.Dispose();await _reader.ConfigureAwait(false);await _dataReader.ConfigureAwait(false);_events.Writer.TryComplete();_previewEvents.Writer.TryComplete();await Task.WhenAll(_eventWorker,_previewWorker).ConfigureAwait(false);
        try{await Task.WhenAll(_checkpoints.Values.Append(cancellation)).WaitAsync(CheckpointStopTimeout).ConfigureAwait(false);}
        catch(TimeoutException){Trace.TraceWarning("停止原生运行时时检查点处理器未在期限内结束；继续退出，操作结果须核对。");}
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
