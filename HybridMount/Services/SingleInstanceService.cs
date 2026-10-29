namespace HybridMount.Services;

/// <summary>会话隔离的单实例租约；互斥量由创建它的 UI 线程释放。</summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owned;
    /// <summary>当前进程是否持有实例租约。</summary>
    public bool IsPrimary => _owned;
    /// <summary>原生崩溃遗留的互斥量可以由下一实例接管。</summary>
    public SingleInstanceService(string? name = null)
    {
        _mutex = new Mutex(false, name ?? $"Local\\{IpcProtocol.PipeName}.Instance");
        try { _owned = _mutex.WaitOne(0); }
        catch (AbandonedMutexException) { _owned = true; }
    }
    /// <summary>只释放自己持有的租约，可重复调用。</summary>
    public void Dispose()
    {
        if (_owned) { _mutex.ReleaseMutex(); _owned = false; }
        _mutex.Dispose();
    }
}
