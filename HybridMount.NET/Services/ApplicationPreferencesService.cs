namespace HybridMount.Services;

/// <summary>应用生命周期偏好与页面状态分离；关闭到托盘读取最后有效值，不依赖页面是否已创建。</summary>
public sealed class ApplicationPreferencesService
{
    private readonly Action<bool> _write;
    private readonly LoggerService? _logger;
    private bool _closeToTray=true;
    public bool CloseToTray=>_closeToTray;
    public string? ErrorStatus {get;private set;}
    public ApplicationPreferencesService(LoggerService logger):this(()=>WinRegisterService.ReadFlag("CloseToTray",true),value=>WinRegisterService.WriteFlag("CloseToTray",value),logger) { }
    /// <summary>隔离测试注入，不访问正式注册表；保存失败不发布候选偏好。</summary>
    internal ApplicationPreferencesService(Func<bool> read,Action<bool> write,LoggerService? logger=null)
    {
        _write=write;_logger=logger;
        try{_closeToTray=read();}
        catch(Exception error){ErrorStatus="关闭偏好读取失败，暂按隐藏到托盘处理。";_logger?.Queue("设置","Warning",error.GetType().Name);}
    }
    public bool TrySetCloseToTray(bool value,out string? error)
    {
        try{_write(value);_closeToTray=value;ErrorStatus=null;error=null;return true;}
        catch(Exception failure){error=ErrorStatus="关闭偏好保存失败，已保留原值。";_logger?.Queue("设置","Error",failure.GetType().Name);return false;}
    }
}
