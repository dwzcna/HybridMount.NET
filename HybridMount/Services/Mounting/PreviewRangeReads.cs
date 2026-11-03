using System.Net;
using System.Net.Http.Headers;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

public sealed partial class WebDavFileSource
{
    /// <summary>验证过的单范围响应才可用于预览；200、弱版本及缺失版本直接回退，不调用整文件缓存。</summary>
    public async ValueTask<PreviewRangeResult> TryReadPreviewRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);if(entry.IsDirectory||offset>entry.Size)throw new ArgumentException("预览范围无效。");
        var count=(int)Math.Min(buffer.Length,entry.Size-offset);if(count==0)return new(true,0);
        var tag=NormalizeTag(Version(entry).ETag);if(!Strong(tag))return new(false,0);
        using var request=new HttpRequestMessage(HttpMethod.Get,Address(entry.Path));request.Headers.Range=new RangeHeaderValue(offset,offset+count-1);request.Headers.IfMatch.ParseAdd(tag!);
        HttpResponseMessage response;
        try { response=await SendAsync(request,token).ConfigureAwait(false); }
        catch(DavRangeIgnoredException){return new(false,0);}
        catch(DavMalformedRangeException){throw new SourceConflictException("预览范围无效，未读取完整文件。");}
        using var owned=response;Success(response);
        var range=response.Content.Headers.ContentRange;var receivedTag=NormalizeTag(response.Headers.TryGetValues("ETag",out var tags)?tags.SingleOrDefault():null);
        if(response.StatusCode!=HttpStatusCode.PartialContent)return new(false,0);
        if(range?.Unit!="bytes"||range.From!=offset||range.To!=offset+count-1||range.Length!=entry.Size||response.Content.Headers.ContentLength!=count||receivedTag!=tag)throw new SourceConflictException("预览范围或版本变化，未发布缓存。");
        await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);await stream.ReadExactlyAsync(buffer[..count],token).ConfigureAwait(false);return new(true,count);
    }
}
public sealed partial class S3FileSource
{
    /// <summary>S3 原读取始终校验范围和版本且从不整文件退化；缺少可靠版本时声明不支持预览。</summary>
    public async ValueTask<PreviewRangeResult> TryReadPreviewRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
    {
        try{_ = RequiredETag(Version(entry));}catch(NotSupportedException){return new(false,0);}
        return new(true,await ReadRangeAsync(entry,offset,buffer,token).ConfigureAwait(false));
    }
}
