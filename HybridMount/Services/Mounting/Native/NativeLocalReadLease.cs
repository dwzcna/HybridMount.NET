namespace HybridMount.Services.Mounting;
/// <summary>后台预览的原生保护租约；跨进程仅传GUID和有界正文，释放在重新等待根锁之前完成。</summary>
internal sealed class NativeLocalReadLease:IAsyncDisposable
{
    private readonly NativeRuntimeClient _runtime;private readonly Guid _mount,_id;private int _closed;
    internal LocalFileStamp Stamp{get;}
    private NativeLocalReadLease(NativeRuntimeClient runtime,Guid mount,Guid id,LocalFileStamp stamp){_runtime=runtime;_mount=mount;_id=id;Stamp=stamp;}
    internal static async Task<NativeLocalReadLease> OpenAsync(MountDefinition mount,MountEntry entry,CancellationToken token)
    {
        var runtime=await NativeRuntimeClient.GetAsync().ConfigureAwait(false);var id=Guid.NewGuid();var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);var stamp=LocalFileState.Read(path);
        token.ThrowIfCancellationRequested();var result=await runtime.InvokeAsync("openLocalRead",NativeRuntimeArguments.Entry(mount,entry,new{leaseId=id,expectedStamp=stamp.Key,expected=stamp})).ConfigureAwait(false);var lease=new NativeLocalReadLease(runtime,mount.Id,id,NativeRuntimeArguments.Stamp(result));if(token.IsCancellationRequested){await lease.DisposeAsync().ConfigureAwait(false);token.ThrowIfCancellationRequested();}return lease;
    }
    internal async ValueTask<PreviewRangeResult> ReadAsync(long offset,Memory<byte> buffer,CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed)!=0,this);var bytes=await _runtime.ReadLocalRangeAsync(_mount,_id,offset,buffer.Length,token).ConfigureAwait(false);bytes.CopyTo(buffer);return new(true,bytes.Length);
    }
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _closed,1)!=0||!_runtime.IsAvailable)return;await _runtime.InvokeAsync("closeLocalRead",new{mountId=_mount,leaseId=_id}).ConfigureAwait(false);}
}
