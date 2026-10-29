using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>一个同步根的 CFAPI 连接；复制回调参数后在线程池读取，退出先取消／断开再释放委托。</summary>
public sealed class CloudMountSession : IAsyncDisposable
{
    private readonly MountDefinition _mount;
    private readonly IFileSource? _source;
    private readonly object _fetchGate=new();
    private bool _clearing;
    private readonly MountRepository _repository;
    private readonly LoggerService _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _reads = new(4,4);
    private readonly List<CloudFilesNative.Callback> _callbacks = [];
    private readonly ConcurrentDictionary<(long,long), (CancellationTokenSource Cancel,Task Work)> _fetches = new();
    private readonly ConcurrentDictionary<string,int> _open = new(StringComparer.Ordinal);
    private long _connection;
    private readonly ConcurrentDictionary<string,int> _reading=new();
    private readonly ConcurrentDictionary<string,DateTimeOffset> _lastRead=new();
    public CloudMountSession(MountDefinition mount,IFileSource? source,MountRepository repository,LoggerService logger,bool blockHydration=false)
    { _mount=mount; _source=source; _repository=repository; _logger=logger; _clearing=blockHydration; }
    public void Connect()
    {
        var registrations=new List<CloudFilesNative.CallbackRegistration>();
        foreach (var type in new uint[]{0,2,5,6,9,10,11,12})
        {
            var captured=type;
            CloudFilesNative.Callback callback=(in CloudFilesNative.CallbackInfo info,nint parameters)=>Dispatch(captured,info,parameters);
            _callbacks.Add(callback); registrations.Add(new(){Type=type,Callback=Marshal.GetFunctionPointerForDelegate(callback)});
        }
        registrations.Add(new(){Type=uint.MaxValue});
        CloudFilesNative.Check(CloudFilesNative.CfConnectSyncRoot(_mount.LocalPath,registrations.ToArray(),0,6,out _connection));
    }
    private static byte[] CopyIdentity(in CloudFilesNative.CallbackInfo info)
    {
        if (info.FileIdentityLength is < 1 or > 4096 || info.FileIdentity==0) return [];
        var bytes=new byte[info.FileIdentityLength]; Marshal.Copy(info.FileIdentity,bytes,0,bytes.Length); return bytes;
    }
    private string NativePath(in CloudFilesNative.CallbackInfo info,nint pointer)
    {
        var value=Marshal.PtrToStringUni(pointer) ?? throw new IOException("回调路径为空。");
        if (!Path.IsPathFullyQualified(value)) value=Path.GetPathRoot(_mount.LocalPath)!.TrimEnd('\\')+value;
        return Path.GetFullPath(value);
    }
    private void Dispatch(uint type,CloudFilesNative.CallbackInfo info,nint parameters)
    {
        try
        {
            var marker=MountPathGuard.ReadEntry(CopyIdentity(info));
            if (marker?.MountId!=_mount.Id || string.IsNullOrEmpty(marker.EntryId)) { if(type==0) Complete(info,0,0,unchecked((int)0xc000cf00)); return; }
            if (type==0)
            {
                lock(_fetchGate)
                {
                if(_clearing) { Complete(info,0,0,unchecked((int)0xc000cf00));return; }
                var offset=Marshal.ReadInt64(parameters,16); var length=Marshal.ReadInt64(parameters,24);
                var key=(info.TransferKey,info.RequestKey); var cancel=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var start=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var work=Task.Run(async()=>{ if(!await start.Task.ConfigureAwait(false)){cancel.Dispose();return;} try { await FetchAsync(info,marker.EntryId,offset,length,cancel.Token).ConfigureAwait(false); } finally { _fetches.TryRemove(key,out _); cancel.Dispose(); } });
                if (!_fetches.TryAdd(key,(cancel,work))) { start.SetResult(false); return; }
                start.SetResult(true); return;
                }
            }
            if (type==2) { if(_fetches.TryGetValue((info.TransferKey,info.RequestKey),out var fetch)) fetch.Cancel.Cancel(); return; }
            var self=info.ProcessInfo!=0 && Marshal.ReadInt32(info.ProcessInfo,4)==Environment.ProcessId;
            if (type==5 && !self) { _open.AddOrUpdate(marker.EntryId,1,(_,value)=>value+1); return; }
            if (type==6) { if(!self) _open.AddOrUpdate(marker.EntryId,0,(_,value)=>Math.Max(0,value-1)); return; }
            if (type==9) { Ack(info,6,0); return; }
            if (type==11)
            {
                if(Volatile.Read(ref _clearing)) { Ack(info,7,unchecked((int)0xc0000022));return; }
                var flags=Marshal.ReadInt32(parameters,8);
                var target=NativePath(info,Marshal.ReadIntPtr(parameters,16));
                if ((flags&4)==0 || !MountPathGuard.IsUnder(_mount.LocalPath,target)) { Ack(info,7,unchecked((int)0xc0000022)); return; }
                MountPathGuard.ValidateOperation(_mount,Path.GetDirectoryName(target)!); Ack(info,7,0); return;
            }
            if (type==10 || type==12)
            {
                var entries=_repository.Entries(_mount.Id); var entry=entries.FirstOrDefault(entry=>entry.Id==marker.EntryId);
                if(entry is null) return;
                if(type==10)
                    foreach(var child in entries.Where(item=>RelativePaths.Contains(entry.LocalPath,item.LocalPath))) _repository.SaveEntry(_mount.Id,child with{Deleted=true,Protected=true});
                else
                {
                    var target=NativePath(info,info.NormalizedPath);
                    if(!MountPathGuard.IsUnder(_mount.LocalPath,target)) return;
                    var relative=Path.GetRelativePath(_mount.LocalPath,target).Replace('\\','/');
                    foreach(var child in entries.Where(item=>RelativePaths.Contains(entry.LocalPath,item.LocalPath)))
                        _repository.SaveEntry(_mount.Id,child with{LocalPath=relative+child.LocalPath[entry.LocalPath.Length..],Protected=true});
                }
            }
        }
        catch(Exception error)
        {
            _logger.Queue("CFAPI","Error",$"回调 {type} 失败：{error.Message}");
            if(type==0) try{Complete(info,0,0,unchecked((int)0xc000cf00));}catch(Exception secondary){_logger.Queue("CFAPI","Warning",secondary.Message);}
            if(type==9 || type==11)try{Ack(info,type==9?6u:7u,unchecked((int)0xc0000022));}catch(Exception secondary){_logger.Queue("CFAPI","Warning",secondary.Message);}
        }
    }
    private static void Ack(CloudFilesNative.CallbackInfo callback,uint type,int status)
    {
        var info=new CloudFilesNative.OperationInfo{StructSize=(uint)Marshal.SizeOf<CloudFilesNative.OperationInfo>(),Type=type,ConnectionKey=callback.ConnectionKey,TransferKey=callback.TransferKey,RequestKey=callback.RequestKey};
        var parameters=new CloudFilesNative.OperationParameters{ParamSize=16,Status=status};
        CloudFilesNative.Check(CloudFilesNative.CfExecute(info,ref parameters));
    }
    private static void Complete(CloudFilesNative.CallbackInfo callback,long offset,long length,int status,nint buffer=0)
    {
        var info=new CloudFilesNative.OperationInfo{StructSize=(uint)Marshal.SizeOf<CloudFilesNative.OperationInfo>(),ConnectionKey=callback.ConnectionKey,TransferKey=callback.TransferKey,RequestKey=callback.RequestKey};
        var parameters=new CloudFilesNative.OperationParameters{ParamSize=40,Status=status,Offset=offset,Length=length,Buffer=buffer};
        CloudFilesNative.Check(CloudFilesNative.CfExecute(info,ref parameters));
    }
    private async Task FetchAsync(CloudFilesNative.CallbackInfo info,string id,long offset,long length,CancellationToken token)
    {
        var held=false;_reading.AddOrUpdate(id,1,(_,value)=>value+1);_lastRead[id]=DateTimeOffset.UtcNow;
        try
        {
            await _reads.WaitAsync(token).ConfigureAwait(false); held=true;
            var entry=_repository.Entries(_mount.Id).FirstOrDefault(item=>item.Id==id && !item.Deleted) ?? throw new FileNotFoundException("占位身份不存在。");
            var source=_source??throw new IOException("清空期间不提供内容读取。");
            var metadata=await source.GetMetadataAsync(entry.SourcePath,token).ConfigureAwait(false);
            if(metadata.Version!=entry.Version) throw new SourceConflictException("源版本已变化，拒绝混入旧缓存。");
            if(offset<0 || length<=0 || offset>metadata.Size || length>metadata.Size-offset) throw new IOException("系统请求范围越界。");
            var start=offset/4096*4096; var end=Math.Min(metadata.Size,checked((offset+length+4095)/4096*4096));
            var bytes=new byte[256*1024]; var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            try
            {
                while(start<end)
                {
                    token.ThrowIfCancellationRequested(); var count=(int)Math.Min(bytes.Length,end-start); var read=0;
                    while(read<count) { var n=await source.ReadRangeAsync(metadata,start+read,bytes.AsMemory(read,count-read),token).ConfigureAwait(false); if(n==0) throw new EndOfStreamException(); read+=n; }
                    token.ThrowIfCancellationRequested(); Complete(info,start,count,0,pin.AddrOfPinnedObject()); start+=count;
                }
            }
            finally { pin.Free(); }
        }
        catch(Exception error)
        {
            try { Complete(info,offset,length,error is OperationCanceledException?unchecked((int)0xc0000120):unchecked((int)0xc000cf00)); } catch(Exception completion) { _logger.Queue("CFAPI","Warning",$"请求结束：{completion.GetType().Name}"); }
            if(error is not OperationCanceledException) _logger.Queue("按需读取","Error",error.Message);
        }
        finally { if(held) _reads.Release();_reading.AddOrUpdate(id,0,(_,value)=>Math.Max(0,value-1)); }
    }
    /// <summary>阻止新增水合和重命名，取消并等候在途读取；保留删除确认回调。</summary>
    public async Task BeginClearAsync()
    {
        (CancellationTokenSource Cancel,Task Work)[] pending;
        lock(_fetchGate) { _clearing=true;pending=_fetches.Values.ToArray(); }
        foreach(var item in pending)try{item.Cancel.Cancel();}catch(ObjectDisposedException){}
        await Task.WhenAll(pending.Select(item=>item.Work)).ConfigureAwait(false);
    }
    public bool IsActive(string id) => (_open.TryGetValue(id,out var count) && count>0)||(_reading.TryGetValue(id,out var reads)&&reads>0);
    public DateTimeOffset LastRead(string id)=>_lastRead.GetValueOrDefault(id,DateTimeOffset.MinValue);
    /// <summary>在排他 oplock 下验证干净状态并以 CFAPI 更新/脱水；失败保留旧索引版本。</summary>
    public bool RefreshPlaceholder(MountEntry entry,SourceEntry metadata)
    {
        if(IsActive(entry.Id))return false;
        var path=RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath);MountPathGuard.ValidateOperation(_mount,path);
        using var handle=CloudFilesNative.Open(path,true);
        var info=CloudFilesNative.Inspect(path);var marker=info is null?null:MountPathGuard.ReadEntry(info.Value.Identity);
        if(info is null||marker?.MountId!=_mount.Id||marker.EntryId!=entry.Id||info.Value.Info.ModifiedSize!=0||info.Value.Info.InSyncState!=1||info.Value.Info.PinState is not(0 or 2))return false;
        var time=metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc();
        var fs=new CloudFilesNative.FsMetadata{Basic=new(){CreationTime=time,LastWriteTime=time,LastAccessTime=time,ChangeTime=time,FileAttributes=0x80},FileSize=metadata.Size};
        CloudFilesNative.Check(CloudFilesNative.CfUpdatePlaceholder(handle,fs,0,0,[new(){Offset=0,Length=-1}],1,7,0,0));
        return true;
    }
    /// <summary>创建真实占位，不使用 File.WriteAllBytes 伪装；每个已创建条目单独记录。</summary>
    public void CreatePlaceholder(MountEntry entry,SourceEntry metadata)
    {
        var full=RelativePaths.ToLocal(_mount.LocalPath,entry.LocalPath);
        MountPathGuard.ValidateOperation(_mount,Path.GetDirectoryName(full)!);
        var identity=JsonSerializer.SerializeToUtf8Bytes(new MountPathGuard.EntryMarker(_mount.Id,entry.Id));
        var pin=GCHandle.Alloc(identity,GCHandleType.Pinned);
        try
        {
            var time=metadata.ModifiedUtc.UtcDateTime.ToFileTimeUtc();
            var records=new[]{new CloudFilesNative.PlaceholderCreateInfo{RelativeName=Path.GetFileName(full),Metadata=new(){Basic=new(){CreationTime=time,LastWriteTime=time,LastAccessTime=time,ChangeTime=time,FileAttributes=metadata.IsDirectory?0x10u:0x80u},FileSize=metadata.Size},Identity=pin.AddrOfPinnedObject(),IdentityLength=(uint)identity.Length,Flags=metadata.IsDirectory?3u:2u}};
            CloudFilesNative.Check(CloudFilesNative.CfCreatePlaceholders(Path.GetDirectoryName(full)!,records,1,1,out var processed));
            CloudFilesNative.Check(records[0].Result); if(processed!=1) throw new IOException("占位创建未完成。");
            _repository.SaveEntry(_mount.Id,entry with{Materialized=true});
        }
        finally { pin.Free(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if(_connection!=0) { CloudFilesNative.Check(CloudFilesNative.CfDisconnectSyncRoot(_connection)); _connection=0; }
        await Task.WhenAll(_fetches.Values.Select(item=>item.Work)).ConfigureAwait(false);
        _callbacks.Clear(); _reads.Dispose(); _stop.Dispose();
    }
}
