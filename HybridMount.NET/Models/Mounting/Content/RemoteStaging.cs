using System.Security.Cryptography;
using HybridMount.Commons;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.Content;

/// <summary>远端不可变暂存的正文下载与摘要核对；不持根锁，失败关闭流后由任务所有者删除残文件。</summary>
internal static class RemoteStaging
{
    /// <summary>取消及空间校验先于建文件和正文网络读取，单次顺序传输后重验版本／长度并返回摘要；失败不返回已验证结果。</summary>
    internal static async Task<string> DownloadAsync(IFileSource source,SourceEntry metadata,string path,CancellationToken token,Action<long>? progress=null,Func<string,ulong>? availableBytes=null)
    {
        token.ThrowIfCancellationRequested();StagingDiskSpace.Require(path,metadata.Size,availableBytes);
        await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,262144,FileOptions.Asynchronous|FileOptions.WriteThrough);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long offset=0;
        await source.ReadRangeSequentialAsync(metadata,0,metadata.Size,async(bytes,cancel)=>
        {
            hash.AppendData(bytes.Span);await output.WriteAsync(bytes,cancel).ConfigureAwait(false);offset+=bytes.Length;progress?.Invoke(offset);
        },262144,token).ConfigureAwait(false);
        var after=await source.GetMetadataAsync(metadata.Path,token).ConfigureAwait(false);
        if(!source.IsSameVersion(metadata.Version,after.Version)||metadata.Size!=after.Size)throw new SourceConflictException("远端快照下载期间版本变化，未应用本地。");
        await output.FlushAsync(token).ConfigureAwait(false);output.Flush(true);token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
