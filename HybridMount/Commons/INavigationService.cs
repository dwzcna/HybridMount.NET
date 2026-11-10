namespace HybridMount.Commons;

/// <summary>在 UI 线程请求页面导航与原生返回；未绑定、正在退出或目标无效时拒绝请求。</summary>
public interface INavigationService
{
    bool Navigate(ApplicationPage page);
    bool TryGoBack();
    bool CanGoBack {get;}
}
