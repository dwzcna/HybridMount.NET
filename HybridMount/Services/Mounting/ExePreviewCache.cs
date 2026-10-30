using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>本地完整 EXE 的有界后台图标队列；查询仅查缓存，不读占位内容、不联网。</summary>
public sealed class ExePreviewCache : IAsyncDisposable
{
    public const int MaximumPngBytes=384*1024;
    private static readonly int[] Sizes=[32,64,128,256];
    private readonly string _directory;
    private readonly Func<MountDefinition,MountEntry,CancellationToken,Task<IDisposable?>>? _enter;
    private readonly Action<string>? _diagnostic;
    private readonly object _disk=new();
    private readonly ConcurrentDictionary<string,byte> _pending=new();
    private readonly Channel<(MountDefinition Mount,MountEntry Entry)> _queue=Channel.CreateBounded<(MountDefinition,MountEntry)>(new BoundedChannelOptions(128){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _worker;
    private Task? _dispose;
    public ExePreviewCache(string directory,Func<MountDefinition,MountEntry,CancellationToken,Task<IDisposable?>>? enter=null,Action<string>? diagnostic=null)
    {_directory=directory;_enter=enter;_diagnostic=diagnostic;_worker=Task.Run(ProcessAsync);}
    /// <summary>按挂载、条目、原始内容版本和尺寸区分；不把远端地址或令牌明文写入文件名。</summary>
    public static string CacheKey(MountDefinition mount,MountEntry entry,int size)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]{mount.Id.ToString("N"),entry.Id,entry.Version,Bucket(size).ToString(System.Globalization.CultureInfo.InvariantCulture)})));
    private static int Bucket(int size)=>Sizes.FirstOrDefault(candidate=>candidate>=Math.Clamp(size,16,256),256);
    private string PathFor(MountDefinition mount,MountEntry entry,int size)=>Path.Combine(_directory,CacheKey(mount,entry,size)+".cache");
    /// <summary>仅入队 EXE；取消或队列满时跳过，后续读取结束可再次触发。</summary>
    public void Queue(MountDefinition mount,MountEntry entry)
    {
        if(_stop.IsCancellationRequested||mount.RemovalPending||entry.Deleted||entry.Protected||entry.IsDirectory||!entry.LocalPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))return;
        var key=CacheKey(mount,entry,256);if(!_pending.TryAdd(key,0))return;
        if(!_queue.Writer.TryWrite((mount,entry)))_pending.TryRemove(key,out _);
    }
    /// <summary>有界本机查询；缓存不存在、失效或被回收均返回空值，不启动提取。</summary>
    public byte[]? Get(MountDefinition mount,MountEntry entry,int size)
    {
        if(entry.Deleted||entry.Protected||entry.IsDirectory||!entry.LocalPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))return null;
        lock(_disk)
        {
            var path=PathFor(mount,entry,size);
            try
            {
                var length=new FileInfo(path).Length;if(length is <8 or >MaximumPngBytes)return null;
                var bytes=File.ReadAllBytes(path);File.SetLastWriteTimeUtc(path,DateTime.UtcNow);return bytes;
            }
            catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
        }
    }
    private async Task ProcessAsync()
    {
        try
        {
            await foreach(var work in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    using var gate=_enter is null?null:await _enter(work.Mount,work.Entry,_stop.Token).ConfigureAwait(false);
                    if(_enter is null||gate is not null)Extract(work.Mount,work.Entry);
                }
                catch(Exception error)when(error is IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or UnauthorizedAccessException){_diagnostic?.Invoke("提取跳过："+error.GetType().Name+"；"+error.Message);}
                finally{_pending.TryRemove(CacheKey(work.Mount,work.Entry,256),out _);}
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
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
    public ValueTask DisposeAsync()=>new(_dispose??=StopAsync());
    private async Task StopAsync(){_queue.Writer.TryComplete();await _stop.CancelAsync().ConfigureAwait(false);await _worker.ConfigureAwait(false);_stop.Dispose();}
}
