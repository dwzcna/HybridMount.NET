namespace HybridMount.Commons;

/// <summary>创建线程的 UI 派发边界；测试可延迟执行，关闭后拒绝或忽略迟到通知。</summary>
public interface IUiDispatcher
{
    bool TryEnqueue(Action callback);
}
