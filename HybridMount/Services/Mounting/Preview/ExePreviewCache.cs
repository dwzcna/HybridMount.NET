using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>每挂载可选的有界预览队列与缓存；查询仅查缓存，解码委托在独立进程执行。</summary>
public sealed class ExePreviewCache : IAsyncDisposable
{
    public const int MaximumPngBytes=384*1024;
    private static readonly int[] Sizes=[32,64,128,256];
    private readonly string _directory;
    private readonly Func<MountDefinition,MountEntry,CancellationToken,Task<IDisposable?>>? _enter;
    private readonly Action<string>? _diagnostic;
    private readonly object _disk=new();
    private sealed class Work(MountDefinition mount,MountEntry entry,bool remote,CancellationToken stopping)
    {
        public readonly MountDefinition Mount=mount;public readonly MountEntry Entry=entry;public readonly bool Remote=remote;
        public readonly CancellationTokenSource Cancel=CancellationTokenSource.CreateLinkedTokenSource(stopping);
        public readonly TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly object Sync=new();public bool Disposed;
        public Task RequestCancel(){lock(Sync)return Disposed?Task.CompletedTask:Cancel.CancelAsync();}
        public void Dispose(){lock(Sync){if(Disposed)return;Disposed=true;Cancel.Dispose();}}
    }
    private readonly ConcurrentDictionary<string,Work> _pending=new();
    private readonly ConcurrentDictionary<Guid,SemaphoreSlim> _mounts=new();
    private readonly ConcurrentDictionary<Guid,byte> _blocked=new();
    private readonly ConcurrentDictionary<string,(Guid Mount,DateTime Until)> _failed=new();
    private readonly SemaphoreSlim _slots=new(2,2);
    private readonly object _schedule=new();
    private readonly Channel<Work> _queue=Channel.CreateBounded<Work>(new BoundedChannelOptions(128){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _worker;
    private readonly Func<MountDefinition,MountEntry,bool,CancellationToken,Task<Dictionary<int,byte[]>?>>? _render;
    private Task? _dispose,_cancellation;
    public ExePreviewCache(string directory,Func<MountDefinition,MountEntry,CancellationToken,Task<IDisposable?>>? enter=null,Action<string>? diagnostic=null,
        Func<MountDefinition,MountEntry,bool,CancellationToken,Task<Dictionary<int,byte[]>?>>? render=null)
    {_directory=directory;_enter=enter;_diagnostic=diagnostic;_render=render;_worker=Task.Run(ProcessAsync);}
    /// <summary>按挂载、条目、原始内容版本和尺寸区分；不把远端地址或令牌明文写入文件名。</summary>
    public static string CacheKey(MountDefinition mount,MountEntry entry,int size)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]{mount.Id.ToString("N"),entry.Id,entry.Version,Bucket(size).ToString(System.Globalization.CultureInfo.InvariantCulture)})));
    private static int Bucket(int size)=>Sizes.FirstOrDefault(candidate=>candidate>=Math.Clamp(size,16,256),256);
    private string PathFor(MountDefinition mount,MountEntry entry,int size)=>Path.Combine(_directory,CacheKey(mount,entry,size)+".cache");
    /// <summary>普通完成通知只处理完整本地文件；仅显式预览请求可读取远端，队列与挂载设置均有界。</summary>
    public void Queue(MountDefinition mount,MountEntry entry,bool allowRemote=false)
    {
        if(_stop.IsCancellationRequested||!mount.ThumbnailsEnabled||mount.RemovalPending||entry.Deleted||entry.Protected||entry.RemotePresence==RemotePresence.Deleted||entry.IsDirectory||!entry.Materialized||!PreviewWorker.Supports(entry.LocalPath)||_blocked.ContainsKey(mount.Id))return;
        if(!allowRemote)
        {
            var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);
            try{if(!File.Exists(path)||!CloudFilesNative.HasFullContent(path,new FileInfo(path).Length))return;}catch(IOException){return;}catch(UnauthorizedAccessException){return;}
        }
        var key=CacheKey(mount,entry,256);if(!allowRemote)_failed.TryRemove(key,out _);if(_failed.TryGetValue(key,out var failure)&&failure.Until>DateTime.UtcNow)return;
        lock(_schedule)
        {
            if(_stop.IsCancellationRequested||_pending.Count>=128||_pending.ContainsKey(key)||Sizes.All(size=>Get(mount,entry,size) is not null))return;
            var work=new Work(mount,entry,allowRemote,_stop.Token);if(!_pending.TryAdd(key,work)||!_queue.Writer.TryWrite(work)){_pending.TryRemove(key,out _);work.Dispose();}
        }
    }
    /// <summary>关闭／断开先阻止新任务，再取消并等待此挂载所有工作；不持有根锁等待其他挂载。</summary>
    public async Task CancelMountAsync(Guid mount)
    {
        _blocked[mount]=0;var pending=_pending.Values.Where(work=>work.Mount.Id==mount).ToArray();
        await Task.WhenAll(pending.Select(work=>work.RequestCancel())).ConfigureAwait(false);
        await Task.WhenAll(pending.Select(work=>work.Completion.Task)).ConfigureAwait(false);
    }
    public void ResumeMount(Guid mount){_blocked.TryRemove(mount,out _);foreach(var row in _failed.Where(row=>row.Value.Mount==mount))_failed.TryRemove(row.Key,out _);}
    public string State(MountDefinition mount,MountEntry entry)=>!mount.ThumbnailsEnabled?"disabled":_pending.ContainsKey(CacheKey(mount,entry,256))?"queued":_failed.TryGetValue(CacheKey(mount,entry,256),out var failed)&&failed.Until>DateTime.UtcNow?"unsupported":"notFound";
    /// <summary>有界本机查询；缓存不存在、失效或被回收均返回空值，不启动提取。</summary>
    public byte[]? Get(MountDefinition mount,MountEntry entry,int size)
    {
        if(!mount.ThumbnailsEnabled||entry.Deleted||entry.Protected||entry.RemotePresence==RemotePresence.Deleted||entry.IsDirectory||!PreviewWorker.Supports(entry.LocalPath))return null;
        lock(_disk)
        {
            var path=PathFor(mount,entry,size);
            try
            {
                var metadata=LocalFileMetadata.TryRead(path);if(metadata is null||metadata.Value.Length is <8 or >MaximumPngBytes)return null;
                var bytes=File.ReadAllBytes(path);File.SetLastWriteTimeUtc(path,DateTime.UtcNow);return bytes;
            }
            catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
        }
    }
    private async Task ProcessAsync()
    {
        try{await foreach(var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false))_ = ProcessWorkAsync(work);}
        finally{await Task.WhenAll(_pending.Values.Select(work=>work.Completion.Task)).ConfigureAwait(false);}
    }
    private async Task ProcessWorkAsync(Work work)
    {
        var mount=work.Mount;var entry=work.Entry;var key=CacheKey(mount,entry,256);var gate=_mounts.GetOrAdd(mount.Id,_=>new(1,1));var entered=false;var slot=false;var token=work.Cancel.Token;
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);entered=true;await _slots.WaitAsync(token).ConfigureAwait(false);slot=true;token.ThrowIfCancellationRequested();
            if(_render is not null)
            {
                var frames=await _render(mount,entry,work.Remote,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
                if(frames is null){_failed[key]=(mount.Id,DateTime.UtcNow.AddMinutes(5));return;}
                lock(_disk)
                {
                    Directory.CreateDirectory(_directory);
                    foreach(var frame in frames){if(!Sizes.Contains(frame.Key)||frame.Value.Length>MaximumPngBytes)throw new IOException("预览缓存大小无效。");var path=PathFor(mount,entry,frame.Key);var temporary=path+".tmp";try{File.WriteAllBytes(temporary,frame.Value);File.Move(temporary,path,true);}finally{File.Delete(temporary);}}
                }
                CloudFilesNative.NotifyItemUpdated(RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath));
            }
            else
            {
                using var lease=_enter is null?null:await _enter(mount,entry,token).ConfigureAwait(false);if(_enter is null||lease is not null)Extract(mount,entry);
            }
        }
        catch(OperationCanceledException){if(!token.IsCancellationRequested)_failed[key]=(mount.Id,DateTime.UtcNow.AddMinutes(5));}
        catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or NotSupportedException or System.Text.Json.JsonException)
        {_failed[key]=(mount.Id,DateTime.UtcNow.AddMinutes(5));_diagnostic?.Invoke("预览回退："+error.GetType().Name);}
        finally{if(slot)_slots.Release();if(entered)gate.Release();_pending.TryRemove(key,out _);work.Completion.TrySetResult();work.Dispose();}
    }
    private void Extract(MountDefinition mount,MountEntry entry)
    {
        var path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(mount,path);
        // 排他 oplock 阻止并发脱水；只有完整、未修改且身份一致的内容才允许加载资源。
        bool Valid()
        {
            var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
            return marker?.MountId==mount.Id&&marker.EntryId==entry.Id&&info!.Value.Info.ModifiedSize==0&&info.Value.Info.InSyncState==1&&CloudFilesNative.HasFullContent(path,new FileInfo(path).Length);
        }
        // 冷占位必须在任何内容句柄打开前跳过，避免打开行为本身引发水合。
        if(!Valid())return;
        var changed=false;
        using var handle=CloudFilesNative.OpenRead(path);
        if(!CloudFilesNative.CfReferenceProtectedHandle(handle)){_diagnostic?.Invoke("保护句柄不可引用。");return;}
        try
        {
        if(!Valid())return;
        // 使用同一受保护句柄读取 PE 资源，不再次打开原文件，不触发 oplock 自身失效。
        using var content=new ProtectedResourceStream(CloudFilesNative.CfGetWin32HandleFromProtectedHandle(handle),new FileInfo(path).Length);
        foreach(var size in Sizes)
        {
            _stop.Token.ThrowIfCancellationRequested();if(Get(mount,entry,size) is not null)continue;
            var png=ExeIconExtractor.Extract(content,size);if(png is null||png.Length>MaximumPngBytes){_diagnostic?.Invoke("未取得有界图标资源。");continue;}
            MountPathGuard.ValidateOperation(mount,path);if(!Valid())return;
            lock(_disk)
            {
                Directory.CreateDirectory(_directory);var target=PathFor(mount,entry,size);var temporary=target+".tmp";
                try{File.WriteAllBytes(temporary,png);File.Move(temporary,target,true);changed=true;}finally{File.Delete(temporary);}
            }
        }

        }
        finally{CloudFilesNative.CfReleaseProtectedHandle(handle);}
        handle.Dispose();
        if(changed){_diagnostic?.Invoke("图标缓存已发布。");CloudFilesNative.NotifyItemUpdated(path);}
    }
    public long UsageBytes
    {
        get{lock(_disk){if(!Directory.Exists(_directory))return 0;return new DirectoryInfo(_directory).EnumerateFiles("*.cache").Sum(file=>file.Length);}}
    }
    /// <summary>图标纳入同一缓存软上限，按最久未使用顺序回收；查询与写入互斥。</summary>
    public long Trim(long needed)
    {
        lock(_disk)
        {
            if(!Directory.Exists(_directory))return 0;long freed=0;
            foreach(var file in new DirectoryInfo(_directory).EnumerateFiles("*.cache").OrderBy(file=>file.LastWriteTimeUtc))
            {
                if(freed>=needed)break;try{var length=file.Length;file.Delete();freed+=length;}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            return freed;
        }
    }
    /// <summary>只发信号；宿主同时取消其他工作之后再等待本队列结束。</summary>
    public Task RequestStop(){lock(_schedule){_queue.Writer.TryComplete();return _cancellation??=_stop.CancelAsync();}}
    public ValueTask DisposeAsync(){lock(_schedule)return new(_dispose??=StopAsync());}
    private async Task StopAsync(){await RequestStop().ConfigureAwait(false);await _worker.ConfigureAwait(false);_slots.Dispose();foreach(var gate in _mounts.Values)gate.Dispose();_stop.Dispose();}
}
