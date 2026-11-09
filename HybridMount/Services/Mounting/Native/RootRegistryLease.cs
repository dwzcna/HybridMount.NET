using System.Security.Principal;
namespace HybridMount.Services.Mounting;
/// <summary>只串行本实验产品的根注册与精确Shell收尾；跨进程及部署模式共用，避免WinRT枚举遇到正在删除的键。</summary>
internal sealed class RootRegistryLease:IDisposable
{
    private readonly Mutex _mutex;private bool _owned;
    private RootRegistryLease(){_mutex=new(false,@"Global\HybridMount.NativePreview.RootRegistry."+WindowsIdentity.GetCurrent().User!.Value);}
    /// <summary>同步事务必须在同一线程释放；超时不执行后续注册写入，放弃互斥量仍须重新核对所有根证据。</summary>
    internal static RootRegistryLease Acquire()
    {
        var lease=new RootRegistryLease();try{try{lease._owned=lease._mutex.WaitOne(TimeSpan.FromSeconds(30));}catch(AbandonedMutexException){lease._owned=true;}if(!lease._owned)throw new IOException("同步根注册正在收尾，请稍后重试。");return lease;}catch{lease.Dispose();throw;}
    }
    public void Dispose(){if(_owned){_owned=false;_mutex.ReleaseMutex();}_mutex.Dispose();}
}
