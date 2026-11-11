namespace HybridMount.Services;

/// <summary>应用生命周期偏好与页面状态分离；关闭到托盘读取最后有效值，不依赖页面是否已创建。</summary>
public sealed class ApplicationPreferencesService
{
    private readonly Action<bool> _write;
    private readonly LoggerService? _logger;
    private bool _closeToTray=true,_compactLayout,_layoutLoaded=true;
    private readonly Action<bool> _writeCompact;
    public bool CompactLayout=>_compactLayout;
    public string? LayoutErrorStatus{get;private set;}
    public event Action? LayoutChanged;
    public bool CloseToTray=>_closeToTray;
    public string? ErrorStatus {get;private set;}
    public ApplicationPreferencesService(LoggerService logger):this(()=>WinRegisterService.ReadFlag("CloseToTray",true),value=>WinRegisterService.WriteFlag("CloseToTray",value),logger,()=>WinRegisterService.ReadFlag("CompactLayout",false),value=>WinRegisterService.WriteFlag("CompactLayout",value)) { }
    /// <summary>隔离测试注入，不访问正式注册表；保存失败不发布候选偏好。</summary>
    internal ApplicationPreferencesService(Func<bool> read,Action<bool> write,LoggerService? logger=null,Func<bool>? readCompact=null,Action<bool>? writeCompact=null)
    {
        _write=write;_logger=logger;_writeCompact=writeCompact??(_=>{});
        try{_compactLayout=readCompact?.Invoke()??false;}catch(Exception error){_layoutLoaded=false;LayoutErrorStatus="界面密度读取失败，暂用标准布局。";_logger?.Queue("外观","Warning",error.GetType().Name);}
        try{_closeToTray=read();}
        catch(Exception error){ErrorStatus="关闭偏好读取失败，暂按隐藏到托盘处理。";_logger?.Queue("设置","Warning",error.GetType().Name);}
    }
    /// <summary>两种密度仅在保存成功后发布；读取失败不覆盖未知值，订阅失败不撤销已经保存的偏好。</summary>
    public bool TrySetCompactLayout(bool value,out string? error)
    {
        if(!_layoutLoaded){error=LayoutErrorStatus;return false;}
        try{_writeCompact(value);_compactLayout=value;LayoutErrorStatus=null;}
        catch(Exception failure){error=LayoutErrorStatus="界面密度保存失败，已保留原值。";_logger?.Queue("外观","Error",failure.GetType().Name);return false;}
        error=null;if(LayoutChanged is {} handlers)foreach(Action handler in handlers.GetInvocationList())try{handler();}catch(Exception failure){_logger?.Queue("外观","Warning",failure.GetType().Name);}return true;
    }
    public bool TrySetCloseToTray(bool value,out string? error)
    {
        try{_write(value);_closeToTray=value;ErrorStatus=null;error=null;return true;}
        catch(Exception failure){error=ErrorStatus="关闭偏好保存失败，已保留原值。";_logger?.Queue("设置","Error",failure.GetType().Name);return false;}
    }
}
