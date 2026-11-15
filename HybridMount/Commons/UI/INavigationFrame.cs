using HybridMount.Commons.UI;
using HybridMount.Models;
namespace HybridMount.Commons.UI;

/// <summary>可验证的 Frame 边界；生产适配器使用系统导航和历史，不自行创建页面。</summary>
internal interface INavigationFrame : IDisposable
{
    event Action<Type,object?>? Navigated;
    event Action<Exception>? Failed;
    bool Navigate(Type pageType,object? parameter);
    bool GoBack();
    bool CanGoBack {get;}
}
