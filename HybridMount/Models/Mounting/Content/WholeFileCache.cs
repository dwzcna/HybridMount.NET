using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.Content;

/// <summary>不可变整文件快照；同会话、路径和版本共享下载，等待者独立取消，读取与回收互斥。</summary>
public sealed class WholeFileCache(string directory) : IAsyncDisposable
{
    private static readonly SemaphoreSlim[] Locks=Enumerable.Range(0,64).Select(_=>new SemaphoreSlim(1,1)).ToArray();
    private static SemaphoreSlim Gate(string path)=>Locks[(StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(path))&int.MaxValue)%Locks.Length];
    private readonly string _session=Guid.NewGuid().ToString("N");
    private readonly SharedAsyncWork<string,string> _downloads=new();
    private readonly SharedAsyncWork<string,string> _verifiedDownloads=new(64);
    private readonly PriorityReadQueue _verifiedQueue=new(1);
    private readonly object _disposeGate=new();private Task? _dispose;
    internal int VerifiedWorkCount=>_verifiedDownloads.RunningCount;
    internal int VerifiedQueuedCount=>_verifiedQueue.WaitingCount;
    private sealed record VerifiedPolicy(TimeSpan Deadline,Func<int> Priority,Action<string,long> Record);
    /// <summary>群晖专用有界整文件任务：最多64个不同身份／版本，单项公平执行；缓存命中不排队。</summary>
    /// <param name="deadline">共享任务从排队至原子发布的总期限，独立于各等待者取消。</param>
    internal ValueTask<int> ReadVerifiedAsync(string scope,SourceEntry entry,long offset,Memory<byte> buffer,
        Func<Stream,CancellationToken,Task> download,CancellationToken token,TimeSpan deadline,Func<int> priority,Action<string,long> record,IDisposable? resource=null)
        =>ReadCoreAsync(scope,entry,offset,buffer,download,token,resource,_verifiedDownloads,new(deadline,priority,record));

    /// <summary>完整验证后发布缓存。resource 总是转交共享任务管理，不能再由请求调用方释放。</summary>
    public ValueTask<int> ReadAsync(string scope,SourceEntry entry,long offset,Memory<byte> buffer,
        Func<Stream,CancellationToken,Task> download,CancellationToken token,IDisposable? resource=null)
        =>ReadCoreAsync(scope,entry,offset,buffer,download,token,resource,_downloads,null);
    private async ValueTask<int> ReadCoreAsync(string scope,SourceEntry entry,long offset,Memory<byte> buffer,
        Func<Stream,CancellationToken,Task> download,CancellationToken token,IDisposable? resource,SharedAsyncWork<string,string> downloads,VerifiedPolicy? policy)
    {
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(policy is null?new[]{_session,scope,entry.Path,entry.Version}:new[]{_session,scope,entry.StableId,entry.Path,entry.Version,entry.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)}))));
        var path=Path.Combine(directory,key+".cache");var readFromWork=false;
        try
        {
            while(true)
            {
                if(policy is not null)
                {
                    downloads.ThrowIfStopping();
                    if(await TryReadPublishedAsync(path,entry,offset,buffer,token).ConfigureAwait(false) is {} cached)
                    {policy.Record("served",1);if(!readFromWork)policy.Record("cacheHits",1);return cached;}
                }
                var owned=resource;resource=null;
                await downloads.RunAsync(key,async cancel=>
                {
                    var started=Stopwatch.GetTimestamp();
                    using var deadline=policy is null?null:CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    if(deadline is not null)deadline.CancelAfter(policy!.Deadline);
                    var workToken=deadline?.Token??cancel;
                    try
                    {
                        Directory.CreateDirectory(directory);var gate=Gate(path);
                        if(policy is not null)
                        {
                            if(File.Exists(path))return path;
                            policy.Record("cacheMisses",1);
                        }
                        var queued=Stopwatch.GetTimestamp();
                        using var admission=policy is null?null:await _verifiedQueue.EnterFairAsync(policy.Priority,workToken).ConfigureAwait(false);
                        policy?.Record("queueMs",(long)Stopwatch.GetElapsedTime(queued).TotalMilliseconds);
                        await gate.WaitAsync(workToken).ConfigureAwait(false);
                        try
                        {
                            if(File.Exists(path))return path;
                            var temporary=path+".tmp";
                            try
                            {
                                await using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
                                {
                                    await download(output,workToken).ConfigureAwait(false);
                                    if(output.Length!=entry.Size)throw new IOException("整文件长度与快照不一致。");
                                    await output.FlushAsync(workToken).ConfigureAwait(false);
                                }
                                workToken.ThrowIfCancellationRequested();File.Move(temporary,path,true);policy?.Record("published",1);
                            }
                            finally{File.Delete(temporary);}
                            return path;
                        }
                        finally{gate.Release();}
                    }
                    catch(OperationCanceledException)when(deadline is not null&&!cancel.IsCancellationRequested)
                    {policy!.Record("timeouts",1);throw new TimeoutException("整文件快照超过总期限，未发布缓存。");}
                    catch{policy?.Record("failures",1);throw;}
                    finally{policy?.Record("totalMs",(long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);}
                },token,owned).ConfigureAwait(false);
                if(policy is not null){readFromWork=true;continue;}
                var readGate=Gate(path);await readGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    // 回收可能发生在发布与领取之间，此时重建快照，绝不读取半个临时文件。
                    if(!File.Exists(path))continue;
                    await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous);
                    stream.Position=offset;var read=await stream.ReadAsync(buffer,token).ConfigureAwait(false);
                    File.SetLastWriteTimeUtc(path,DateTime.UtcNow);return read;
                }
                finally{readGate.Release();}
            }
        }
        finally{resource?.Dispose();}
    }
    /// <summary>只打开已原子发布的不可变缓存，句柄允许回收删除；不等待其他文件持有的条带写锁。</summary>
    private static async ValueTask<int?> TryReadPublishedAsync(string path,SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token)
    {
        FileStream input;
        try{input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,65536,FileOptions.Asynchronous);}
        catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException){return null;}
        await using(input)
        {
            if(input.Length!=entry.Size)throw new IOException("已发布整文件快照长度不匹配，请重新核对缓存。");
            input.Position=offset;var count=await input.ReadAsync(buffer,token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            try{File.SetLastWriteTimeUtc(path,DateTime.UtcNow);}
            catch(Exception error)when(error is FileNotFoundException or DirectoryNotFoundException){/* 已打开句柄仍对应同一完整快照。 */}
            return count;
        }
    }
    /// <summary>取消并等候所有共享下载；应先停止上层读取，再释放网络连接。</summary>
    public ValueTask DisposeAsync(){lock(_disposeGate)return new(_dispose??=DisposeCoreAsync());}
    private async Task DisposeCoreAsync()=>await Task.WhenAll(_downloads.DisposeAsync().AsTask(),_verifiedDownloads.DisposeAsync().AsTask()).ConfigureAwait(false);
    /// <summary>只在旧会话已停止后清理未完成文件。</summary>
    public static void CleanupAbandoned(string directory)
    {
        if(!Directory.Exists(directory))return;
        foreach(var path in Directory.EnumerateFiles(directory,"*.tmp"))try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    /// <summary>仅清理已关闭的完整缓存；在途下载与读取得到条带锁保护。</summary>
    public static long Trim(string root,long bytesToFree)
    {
        if(!Directory.Exists(root))return 0;long freed=0;
        foreach(var file in new DirectoryInfo(root).EnumerateFiles("*.cache",SearchOption.AllDirectories).OrderBy(file=>file.LastWriteTimeUtc))
        {
            if(freed>=bytesToFree)break;
            var gate=Gate(file.FullName);if(!gate.Wait(0))continue;
            try{var length=file.Length;File.Delete(file.FullName);freed+=length;}catch(IOException){}catch(UnauthorizedAccessException){}
            finally{gate.Release();}
        }
        return freed;
    }
}
