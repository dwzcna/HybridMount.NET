using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
namespace HybridMount.Services.Runtime;

internal sealed partial class NativeRuntimeClient
{
    private sealed record TaskSink(string Session,Func<NativeTaskIntent,bool> Post,Action<string> Acknowledge);
    private readonly ConcurrentDictionary<Guid,TaskSink> _taskSinks=new();
    private readonly Channel<JsonElement> _taskOutgoing=Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(4096){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private Task _taskReader=Task.CompletedTask,_taskWriter=Task.CompletedTask;
    /// <summary>每挂载注册有界内存邮箱；传输读取循环不执行数据库、扫描或根核验。</summary>
    internal IDisposable RegisterTaskSink(Guid mount,string session,Func<NativeTaskIntent,bool> post,Action<string> acknowledge)
    {
        var sink=new TaskSink(session,post,acknowledge);if(!_taskSinks.TryAdd(mount,sink))throw new IOException("挂载任务邮箱已登记。");
        return new TaskSinkLease(()=>_taskSinks.TryRemove(new KeyValuePair<Guid,TaskSink>(mount,sink)));
    }
    private sealed class TaskSinkLease(Action release):IDisposable
    {
        private Action? _release=release;public void Dispose()=>Interlocked.Exchange(ref _release,null)?.Invoke();
    }
    internal bool SendTaskFrame(object value)=>IsAvailable&&_taskOutgoing.Writer.TryWrite(NativeRuntimeProtocol.Element(value));
    internal bool SendTaskResult(NativeTaskResult result)=>SendTaskFrame(new{kind="taskResult",result.ResultId,result.TaskId,result.MountId,result.SessionId,result.Stage,result.Outcome,result.Final,result.WorkflowId});
    internal bool SendTaskAcknowledgment(NativeTaskIntent intent,string status)=>SendTaskFrame(new{kind="intentAck",intent.TaskId,intent.MountId,intent.SessionId,status});
    private async Task TaskPipeReadAsync()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                var frame=await IpcProtocol.ReadAsync<JsonElement>(_tasks,_stop.Token).ConfigureAwait(false);
                var kind=frame.GetProperty("kind").GetString();
                if(kind=="intent")
                {
                    var intent=NativeRuntimeProtocol.Read<NativeTaskIntent>(frame);intent.Validate();
                    var status=_taskSinks.TryGetValue(intent.MountId,out var sink)&&sink.Session==intent.SessionId
                        ?sink.Post(intent)?null:"deferred":"rejected";
                    if(status is not null)SendTaskAcknowledgment(intent,status);
                }
                else if(kind=="resultAck")
                {
                    var mount=Guid.Parse(frame.GetProperty("mountId").GetString()!);var id=frame.GetProperty("resultId").GetString();
                    if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("任务结果回执身份无效。");
                    // 回执消费也只投递内存邮箱；旧代次结果由持久ResultID精确核对。
                    if(_taskSinks.TryGetValue(mount,out var sink))sink.Acknowledge(id!);
                }
                else throw new InvalidDataException("原生任务消息类型无效。");
            }
        }
        catch(Exception error){if(!_stop.IsCancellationRequested)Fail(error);}
    }
    private async Task TaskPipeWriteAsync()
    {
        try{await foreach(var frame in _taskOutgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))await IpcProtocol.WriteAsync(_tasks,frame,_stop.Token).ConfigureAwait(false);}
        catch(Exception error){if(!_stop.IsCancellationRequested)Fail(error);}
    }
}
