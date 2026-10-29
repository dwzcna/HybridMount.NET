using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>不可变整文件缓存；同键串行下载，未完成文件不可见，读取与回收互斥。</summary>
public sealed class WholeFileCache(string directory)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _session = Guid.NewGuid().ToString("N");
    public async ValueTask<int> ReadAsync(string scope, SourceEntry entry, long offset, Memory<byte> buffer,
        Func<Stream, CancellationToken, Task> download, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new[]{_session,scope,entry.Path,entry.Version}))));
        var path = Path.Combine(directory, key + ".cache");
        var gate = Locks.GetOrAdd(path, _ => new(1, 1)); await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                var temporary = path + ".tmp";
                try
                {
                    await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    {
                        await download(output, token).ConfigureAwait(false);
                        if (output.Length != entry.Size) throw new IOException("整文件长度与快照不一致。");
                        await output.FlushAsync(token).ConfigureAwait(false);
                    }
                    File.Move(temporary, path, true);
                }
                finally { File.Delete(temporary); }
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            stream.Position = offset; var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return read;
        }
        finally { gate.Release(); }
    }
    /// <summary>仅清理已关闭的完整缓存；在途临时下载及读取通过文件共享限制得到保护。</summary>
    public static long Trim(string root, long bytesToFree)
    {
        if (!Directory.Exists(root)) return 0; long freed = 0;
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.cache", SearchOption.AllDirectories).OrderBy(file => file.LastWriteTimeUtc))
        {
            if (freed >= bytesToFree) break;
            var gate=Locks.GetOrAdd(file.FullName,_=>new(1,1));if(!gate.Wait(0))continue;
            try { var length = file.Length; File.Delete(file.FullName); freed += length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            finally { gate.Release(); }
        }
        return freed;
    }
}
