namespace HybridMount.Common;

/// <summary>仅在 UI 线程请求登记的内部路由；返回是否接受导航，停止或非法目标返回 false。</summary>
public interface INavigationService
{
    bool Navigate(Uri target);
}
