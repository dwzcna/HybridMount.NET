namespace HybridMount.Services;

/// <summary>当前部署模式的云请求并发偏好；只在显式保存成功后更新队列，读取错误从不回写。</summary>
public sealed class CloudOperationSettings
{
    public const int Default=4,Maximum=128;
    private readonly Action<int> _write;
    private readonly LoggerService? _logger;
    private TasksQueueService? _queue;
    private int _current=Default;
    public int Current=>Volatile.Read(ref _current);
    public bool ReadFailed {get;}
    public static int Recommendation=>Math.Clamp(Environment.ProcessorCount/2,1,Maximum);
    public CloudOperationSettings(LoggerService logger):this(
        ()=>WinRegisterService.ReadValue(ApplicationPaths.RegistryPath,"CloudOperationConcurrency"),
        value=>WinRegisterService.WriteDword("CloudOperationConcurrency",value),logger){}
    /// <summary>测试注入独立读写；缺值默认4，格式错误与访问失败保留诊断。</summary>
    internal CloudOperationSettings(Func<object?> read,Action<int> write,LoggerService? logger=null)
    {
        _write=write;_logger=logger;
        try
        {
            var raw=read();if(raw is null)return;
            if(raw is not int value||value is <1 or >Maximum)throw new InvalidDataException("云并发设置不是有效的 DWORD（1—128）。");
            _current=value;
        }
        catch(Exception error){ReadFailed=true;_logger?.QueueException("云调度","Warning","云并发读取失败，暂用4；未改写原设置。",error);}
    }
    internal void Bind(TasksQueueService queue){_queue=queue;queue.SetConcurrency(Current);}
    /// <summary>由页面后台保存，调用者串行化显式操作；保存失败不改变当前值或现有传输。</summary>
    internal bool TrySave(int value)
    {
        if(value is <1 or >Maximum)return false;
        try{_write(value);}
        catch(Exception error){_logger?.QueueException("云调度","Error","云并发保存失败，保留原设置。",error);return false;}
        Volatile.Write(ref _current,value);
        try{_queue?.SetConcurrency(value);}catch(ObjectDisposedException){/* 设置已保存，退出中的队列不再接纳新请求。 */}
        return true;
    }
}
