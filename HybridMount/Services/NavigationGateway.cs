namespace HybridMount.Services;

/// <summary>应用范围导航入口；页面 VM 不依赖窗口，窗口绑定 Frame 服务，退出先拒绝新请求。</summary>
public sealed class NavigationGateway : INavigationService
{
    private INavigationService? _service;
    private bool _stopping;
    internal void Bind(INavigationService service){if(_stopping)throw new InvalidOperationException("程序正在退出。");if(_service is not null)throw new InvalidOperationException("导航已绑定。");_service=service;}
    internal void Unbind(INavigationService service){if(ReferenceEquals(_service,service))_service=null;}
    public bool Navigate(Uri target)=>!_stopping&&_service?.Navigate(target)==true;
    public void RequestStop()=>_stopping=true;
}
