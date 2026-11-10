using Microsoft.UI.Dispatching;
namespace HybridMount.Services;

/// <summary>WinUI 调度适配器，不拥有窗口；ViewModel 在回调中再次检查释放状态。</summary>
public sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool TryEnqueue(Action callback)=>queue.TryEnqueue(()=>callback());
}
