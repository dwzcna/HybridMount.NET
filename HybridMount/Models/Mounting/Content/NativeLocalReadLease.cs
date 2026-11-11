namespace HybridMount.Models.Mounting.Content;
/// <summary>后台预览的原生保护租约；跨进程仅传GUID和有界正文，释放在重新等待根锁之前完成。</summary>
internal sealed class NativeLocalReadLease:IAsyncDisposable
{
    private readonly NativeRuntimeClient _runtime;private readonly Guid _mount,_id;private int _closed;
    internal LocalFileStamp Stamp{get;private set;}
    private NativeLocalReadLease(NativeRuntimeClient runtime,Guid mount,Guid id,LocalFileStamp stamp){_runtime=runtime;_mount=mount;_id=id;Stamp=stamp;}
    internal static async Task<NativeLocalReadLease> OpenAsync(MountDefinition mount,MountEntry entry,CancellationToken token)
    {
        var runtime=await NativeRuntimeClient.GetAsync().ConfigureAwait(false);var id=Guid.NewGuid();var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);var stamp=LocalFileState.Read(path);
        token.ThrowIfCancellationRequested();NativeLocalReadLease? lease=null;
        try
        {
            var result=await runtime.InvokeAsync("openLocalRead",NativeRuntimeArguments.Entry(mount,entry,new{leaseId=id,expectedStamp=stamp.Key,expected=stamp})).ConfigureAwait(false);
            lease=new(runtime,mount.Id,id,NativeRuntimeArguments.Stamp(result));token.ThrowIfCancellationRequested();
            await runtime.InvokeAsync("confirmLocalRead",new{mountId=mount.Id,leaseId=id}).ConfigureAwait(false);token.ThrowIfCancellationRequested();return lease;
        }
        catch
        {
            if(lease is not null)await lease.DisposeAsync().ConfigureAwait(false);
            else if(runtime.IsAvailable)try{await runtime.InvokeAsync("closeLocalRead",new{mountId=mount.Id,leaseId=id}).ConfigureAwait(false);}catch(System.Runtime.InteropServices.COMException error)when(error.HResult==unchecked((int)0x80070490)){/* 未创建或计时器已归还；原始打开失败继续上报。 */}
            throw;
        }
    }
    internal async ValueTask<PreviewRangeResult> ReadAsync(long offset,Memory<byte> buffer,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed)!=0,this);var bytes=await _runtime.ReadLocalRangeAsync(_mount,_id,offset,buffer.Length,token).ConfigureAwait(false);ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed)!=0,this);bytes.CopyTo(buffer);return new(true,bytes.Length);
    }
    /// <summary>关闭成功才提供最终戳；通道丢失或业务校验失败不能作为可发布图片的成功释放。</summary>
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _closed,1)!=0)return;if(!_runtime.IsAvailable)throw new IOException("本地预览租约最终状态未确认，禁止发布。");var result=await _runtime.InvokeAsync("closeLocalRead",new{mountId=_mount,leaseId=_id}).ConfigureAwait(false);Stamp=NativeRuntimeArguments.Stamp(result);}
}
