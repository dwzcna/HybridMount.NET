using Amazon.S3.Model;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class S3FileSource
{
    /// <summary>手动上限统计整桶当前对象；故意不使用挂载前缀，不遍历历史版本或未完成分片。</summary>
    public async Task<RemoteUsage> GetRemoteUsageAsync(CancellationToken token=default)
    {
        var keys=new HashSet<string>(StringComparer.Ordinal);var tokens=new HashSet<string>(StringComparer.Ordinal);string? continuation=null;long used=0;
        do
        {
            var page=await Call(()=>_client.ListObjectsV2Async(new(){BucketName=_bucket,ContinuationToken=continuation,MaxKeys=1000},token),token).ConfigureAwait(false);
            if(page.CommonPrefixes?.Count>0||page.IsTruncated is null)throw new IOException("S3 整桶容量分页不完整。");
            foreach(var item in page.S3Objects??[])
            {
                if(item.Key is null||item.Size is null or <0||!keys.Add(item.Key))throw new IOException("S3 容量统计含重复或无效对象。");
                if(keys.Count>1_000_000)throw new IOException("S3 容量统计超过安全条目限制，未采用部分结果。");
                try{used=checked(used+item.Size.Value);}catch(OverflowException){throw new IOException("S3 整桶用量超出字节范围。");}
            }
            continuation=page.IsTruncated.Value?page.NextContinuationToken:null;
            if(page.IsTruncated.Value&&(string.IsNullOrEmpty(continuation)||!tokens.Add(continuation)))throw new IOException("S3 容量分页令牌缺失或重复。");
        }while(continuation is not null);
        token.ThrowIfCancellationRequested();return new("S3 整个桶的当前对象（不含历史版本及未完成分片）",used);
    }
}
