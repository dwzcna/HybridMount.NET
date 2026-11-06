using Amazon.S3.Model;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class S3FileSource
{
    private async Task<ObjectVersion> MultipartCopyAsync(SourceEntry entry,string key,CancellationToken token)
    {
        var partSize=Math.Max(512L*1024*1024,(entry.Size-1)/10000+1);
        if(partSize>5L*1024*1024*1024)throw new NotSupportedException("S3 对象超过分片复制的大小限制。");
        var version=Version(entry);var etag=RequiredETag(version);
        var metadata=await Call(()=>_client.GetObjectMetadataAsync(new(){BucketName=_bucket,Key=Key(entry.Path),VersionId=version.VersionId,EtagToMatch=etag},token),token).ConfigureAwait(false);
        if(metadata.ContentLength!=entry.Size||metadata.ETag!=etag||version.VersionId is not null&&ImmutableVersion(metadata.VersionId)!=version.VersionId)
            throw new SourceConflictException("S3 分片复制前源元数据或版本变化。");
        await VerifyMultipartAsync(key[..(key.LastIndexOf('/')+1)],token).ConfigureAwait(false);
        var (record,journal)=await BeginUploadAsync(key,token,metadata).ConfigureAwait(false);var completed=false;
        try
        {
            var parts=new List<PartETag>();
            for(long offset=0;offset<entry.Size;)
            {
                var count=Math.Min(partSize,entry.Size-offset);var number=parts.Count+1;
                var part=await Call(()=>_client.CopyPartAsync(new(){SourceBucket=_bucket,SourceKey=Key(entry.Path),SourceVersionId=version.VersionId,
                    ETagToMatch=[etag],DestinationBucket=_bucket,DestinationKey=key,UploadId=record.UploadId,PartNumber=number,FirstByte=offset,LastByte=offset+count-1},token),token,true).ConfigureAwait(false);
                var tag=RequiredETag(new(part.ETag,null));
                if(version.VersionId is not null&&part.CopySourceVersionId is not null&&part.CopySourceVersionId!=version.VersionId)
                    throw new SourceConflictException("S3 复制分片的源版本不匹配。");
                parts.Add(new(number,tag));offset+=count;
            }
            var receipt=await Call(()=>_client.CompleteMultipartUploadAsync(new(){BucketName=_bucket,Key=key,UploadId=record.UploadId,PartETags=parts,IfNoneMatch="*"},token),token,true).ConfigureAwait(false);
            completed=true;File.Delete(journal);return new(receipt.ETag,ImmutableVersion(receipt.VersionId));
        }
        finally
        {
            if(!completed)
            {
                try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(20));await AbortAsync(record,cleanup.Token).ConfigureAwait(false);File.Delete(journal);}
                catch(Exception error)when(error is IOException or OperationCanceledException or UnauthorizedAccessException){ }
            }
        }
    }
}
