using HybridMount.Commons.UI;
using Microsoft.UI.Dispatching;
namespace HybridMount.UI.Dispatch;

/// <summary>WinUI 调度适配器，不拥有窗口；ViewModel 在回调中再次检查释放状态。</summary>
public sealed class UIDispatcher(DispatcherQueue queue) : IUIDispatcher
{
    public bool TryEnqueue(Action callback)=>queue.TryEnqueue(()=>callback());
}
