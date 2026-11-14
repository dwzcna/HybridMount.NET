using System.Collections.Concurrent;
namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private readonly SemaphoreSlim _cacheUsageGate=new(1,1);
    private readonly ConcurrentDictionary<string,byte> _cacheUsageDirty=new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string,long> _cacheEntryUsage=new(StringComparer.OrdinalIgnoreCase);
    private long _cacheUsageBytes,_cacheStorageBytes,_cacheCalibrated;
    private bool _cacheUsageKnown;
    /// <summary>只记录脏路径；CFAPI通知不在此执行查询或递归扫描。</summary>
    private void InvalidateCacheUsage(string path)=>_cacheUsageDirty[path]=0;
    /// <summary>用户显式核对使用完整统计；后台周期只处理脏路径，五分钟校准一次。</summary>
    public Task<long> GetCacheUsageAsync(CancellationToken token=default)=>OwnAsync(cancel=>ReadCacheUsageAsync(true,cancel),token);
    private async Task<long> ReadCacheUsageAsync(bool force,CancellationToken token)
    {
        await _cacheUsageGate.WaitAsync(token).ConfigureAwait(false);string[] paths=[];
        try
        {
            var now=Environment.TickCount64;var full=force||!_cacheUsageKnown||now-_cacheCalibrated>=300000;
            var next=new Dictionary<string,long>(_cacheEntryUsage,StringComparer.OrdinalIgnoreCase);
            if(full)
            {
                var known=_roots.Values.SelectMany(root=>_repository.MaterializedFiles(root.Definition.Id).Select(x=>RelativePaths.ToLocal(root.Definition.LocalPath,x.LocalPath))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach(var stale in next.Keys.Where(x=>!known.Contains(x)).ToArray())next.Remove(stale);
                paths=known.ToArray();
            }
            else paths=_cacheUsageDirty.Keys.ToArray();
            foreach(var path in paths)_cacheUsageDirty.TryRemove(path,out _);
            foreach(var batch in paths.Chunk(64))
            {
                token.ThrowIfCancellationRequested();
                var response=await NativeRuntimeClient.Get().InvokeAsync("queryFiles",new{paths=batch},token:token).ConfigureAwait(false);
                foreach(var item in response.GetProperty("items").EnumerateArray())
                {
                    var path=item.GetProperty("path").GetString()!;var error=item.GetProperty("hresult").GetInt32();
                    if(error is unchecked((int)0x80070002) or unchecked((int)0x80070003)){next.Remove(path);continue;}
                    if(error<0)System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(error);
                    var info=NativeRuntimeProtocol.Read<NativeFileSnapshot>(item.GetProperty("snapshot"));next[path]=info.Placeholder?.OnDiskSize??0;
                }
                await Task.Yield();
            }
            var storage=_cacheStorageBytes;
            if(full)
            {
                storage=0;
                foreach(var name in new[]{"RemoteStaging","UploadStaging","RangeCache"})
                {
                    var directory=Path.Combine(_repository.DataDirectory,name);if(!Directory.Exists(directory))continue;
                    foreach(var file in new DirectoryInfo(directory).EnumerateFiles("*",SearchOption.AllDirectories))
                    {token.ThrowIfCancellationRequested();try{storage=checked(storage+file.Length);}catch(FileNotFoundException){}}
                }
            }
            var bytes=checked(_previews.UsageBytes+storage+next.Values.Sum());
            _cacheEntryUsage=next;_cacheStorageBytes=storage;if(full)_cacheCalibrated=now;
            Interlocked.Exchange(ref _cacheUsageBytes,bytes);_cacheUsageKnown=true;return bytes;
        }
        catch{foreach(var path in paths)_cacheUsageDirty[path]=0;throw;}
        finally{_cacheUsageGate.Release();}
    }
}
