using System.Security.Cryptography;
using System.Text;
namespace HybridMount.Models.Thumbnails;

/// <summary>按当前SID及NTFS身份协调原生／托管解码；专用线程拥有并归还Mutex，异步调用者不跨线程释放。</summary>
internal sealed class PreviewDecodeLease : IAsyncDisposable
{
    private readonly ManualResetEvent _release=new(false);
    private Task _owner=Task.CompletedTask;
    private int _disposed;
    /// <summary>在正文保护租约前等待；取消只影响未取得的所有权，取得后由调用者显式释放。</summary>
    internal static async Task<PreviewDecodeLease> AcquireAsync(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var identity=LocalFileState.Read(path).Identity;
        var digest=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ThumbnailCacheReader.CurrentOwner+"|"+identity))).ToLowerInvariant();
        var result=new TaskCompletionSource<PreviewDecodeLease>(TaskCreationOptions.RunContinuationsAsynchronously);var lease=new PreviewDecodeLease();
        lease._owner=Task.Factory.StartNew(()=>
        {
            try
            {
                using var mutex=new Mutex(false,"Local\\HybridMount.Preview.Decode."+digest);var acquired=false;
                try
                {
                    try{var selected=WaitHandle.WaitAny([mutex,token.WaitHandle]);if(selected!=0)token.ThrowIfCancellationRequested();acquired=true;}
                    catch(AbandonedMutexException){acquired=true;}
                    token.ThrowIfCancellationRequested();result.TrySetResult(lease);lease._release.WaitOne();
                }
                finally{if(acquired)mutex.ReleaseMutex();}
            }
            catch(OperationCanceledException){result.TrySetCanceled(token);}
            catch(Exception error){result.TrySetException(error);}
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        try{return await result.Task.ConfigureAwait(false);}
        catch{await lease._owner.ConfigureAwait(false);lease._release.Dispose();throw;}
    }
    /// <summary>正文租约已归还后通知原拥有线程释放Mutex；重复释放不重复关闭句柄。</summary>
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;_release.Set();await _owner.ConfigureAwait(false);_release.Dispose();}
}
