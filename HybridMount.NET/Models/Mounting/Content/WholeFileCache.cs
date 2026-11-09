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

    /// <summary>完整验证后发布缓存。resource 总是转交共享任务管理，不能再由请求调用方释放。</summary>
    public async ValueTask<int> ReadAsync(string scope,SourceEntry entry,long offset,Memory<byte> buffer,
        Func<Stream,CancellationToken,Task> download,CancellationToken token,IDisposable? resource=null)
    {
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[]{_session,scope,entry.Path,entry.Version}))));
        var path=Path.Combine(directory,key+".cache");
        try
        {
            while(true)
            {
                var owned=resource;resource=null;
                await _downloads.RunAsync(key,async cancel=>
                {
                    Directory.CreateDirectory(directory);
                    var gate=Gate(path);await gate.WaitAsync(cancel).ConfigureAwait(false);
                    try
                    {
                        if(File.Exists(path))return path;
                        var temporary=path+".tmp";
                        try
                        {
                            await using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,65536,FileOptions.Asynchronous))
                            {
                                await download(output,cancel).ConfigureAwait(false);
                                if(output.Length!=entry.Size)throw new IOException("整文件长度与快照不一致。");
                                await output.FlushAsync(cancel).ConfigureAwait(false);
                            }
                            cancel.ThrowIfCancellationRequested();File.Move(temporary,path,true);
                        }
                        finally{File.Delete(temporary);}
                        return path;
                    }
                    finally{gate.Release();}
                },token,owned).ConfigureAwait(false);
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
    /// <summary>取消并等候所有共享下载；应先停止上层读取，再释放网络连接。</summary>
    public ValueTask DisposeAsync()=>_downloads.DisposeAsync();
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
