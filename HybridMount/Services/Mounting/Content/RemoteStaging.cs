using System.Security.Cryptography;
using HybridMount.Common;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

internal static class RemoteStaging
{
    internal static async Task<string> DownloadAsync(IFileSource source,SourceEntry metadata,string path,CancellationToken token,Action<long>? progress=null,Func<string,ulong>? availableBytes=null)
    {
        token.ThrowIfCancellationRequested();StagingDiskSpace.Require(path,metadata.Size,availableBytes);
        await using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,262144,FileOptions.Asynchronous|FileOptions.WriteThrough);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var bytes=new byte[262144];long offset=0;
        while(offset<metadata.Size)
        {
            var read=await source.ReadRangeAsync(metadata,offset,bytes.AsMemory(0,(int)Math.Min(bytes.Length,metadata.Size-offset)),token).ConfigureAwait(false);
            if(read<=0)throw new EndOfStreamException();hash.AppendData(bytes,0,read);await output.WriteAsync(bytes.AsMemory(0,read),token).ConfigureAwait(false);offset+=read;progress?.Invoke(offset);
        }
        var after=await source.GetMetadataAsync(metadata.Path,token).ConfigureAwait(false);
        if(!source.IsSameVersion(metadata.Version,after.Version)||metadata.Size!=after.Size)throw new SourceConflictException("远端快照下载期间版本变化，未应用本地。");
        await output.FlushAsync(token).ConfigureAwait(false);output.Flush(true);token.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
