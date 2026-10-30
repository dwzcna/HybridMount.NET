using System.Text;
using System.Text.Json;
namespace HybridMount.Services;

/// <summary>独立于异步日志队列的生命周期证据；异常只保存类型、HRESULT 和堆栈，不保存消息、数据或服务正文。</summary>
public sealed class LifecycleDiagnostics
{
    private sealed record SessionMarker(string Id,int ProcessId,string Version,string Mode,DateTimeOffset StartedUtc,string Phase,bool Completed);
    private sealed record SafeException(string Type,int HResult,string? Stack);
    private readonly object _gate=new();
    private readonly string _directory,_marker;
    private SessionMarker _session;
    private static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=16};
    public string EventFilePath {get;}
    public bool PreviousSessionIncomplete {get;}
    public string? LastWriteError {get;private set;}
    /// <summary>创建当前进程会话；上次未完成只能说明非正常结束待定位，不能据此认定崩溃。</summary>
    public LifecycleDiagnostics(string directory,string version,string mode)
    {
        _directory=Path.GetFullPath(directory);_marker=Path.Combine(_directory,"last-session.json");
        var id=Guid.NewGuid().ToString("N");_session=new(id,Environment.ProcessId,version,mode,DateTimeOffset.UtcNow,"Starting",false);
        EventFilePath=Path.Combine(_directory,$"lifecycle-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{id}.jsonl");
        try
        {
            Directory.CreateDirectory(_directory);
            if(File.Exists(_marker)&&new FileInfo(_marker).Length<=65536)
            {var previous=JsonSerializer.Deserialize<SessionMarker>(File.ReadAllText(_marker),Json);PreviousSessionIncomplete=previous is {Completed:false};}
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException){LastWriteError=error.GetType().Name;}
        if(PreviousSessionIncomplete)Record("PreviousSessionIncomplete");Record("Starting");
        try
        {
            foreach(var old in new DirectoryInfo(_directory).EnumerateFiles("lifecycle-*.jsonl").OrderByDescending(file=>file.LastWriteTimeUtc).Skip(32))old.Delete();
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException){LastWriteError=error.GetType().Name;}
    }
    /// <summary>同步写出有限安全证据，返回失败而不覆盖正在处理的原始异常；不设置异常已处理标志。</summary>
    public bool Record(string phase,Exception? error=null,bool fatal=false,bool completed=false)
    {
        lock(_gate)
        {
            try
            {
                var chain=new List<SafeException>();for(var current=error;current is not null&&chain.Count<8;current=current.InnerException)
                    chain.Add(new(current.GetType().FullName??current.GetType().Name,current.HResult,current.StackTrace is {Length:>16384} stack?stack[..16384]:current.StackTrace));
                var item=new{utc=DateTimeOffset.UtcNow,session=_session.Id,processId=_session.ProcessId,version=_session.Version,mode=_session.Mode,phase,fatal,exceptions=chain};
                var bytes=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item,Json)+"\n");
                using(var stream=new FileStream(EventFilePath,FileMode.Append,FileAccess.Write,FileShare.ReadWrite,4096,FileOptions.WriteThrough)){stream.Write(bytes);stream.Flush(true);}
                _session=_session with{Phase=phase,Completed=completed};var temporary=_marker+"."+_session.Id+".tmp";
                File.WriteAllText(temporary,JsonSerializer.Serialize(_session,Json),new UTF8Encoding(false));File.Move(temporary,_marker,true);LastWriteError=null;return true;
            }
            catch(Exception failure)when(failure is IOException or UnauthorizedAccessException or System.Security.SecurityException){LastWriteError=failure.GetType().Name;return false;}
        }
    }
}
