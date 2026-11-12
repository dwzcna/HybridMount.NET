using System.Net;
using System.Net.Http.Headers;
using HybridMount.Models.Mounting;
namespace HybridMount.Models.Mounting.WebDAV;

public sealed partial class WebDavFileSource : IPreviewReadDiagnostics
{
    private string _previewRangeDiagnostic="尚未探测";
    string IPreviewReadDiagnostics.PreviewReadDiagnostic=>Volatile.Read(ref _previewRangeDiagnostic);
    /// <summary>只为预览GET构造候选；裸元数据不因此获得写入强验证能力，实际响应仍须提供匹配的合法强标签。</summary>
    private static string? PreviewReadCandidate(string? tag)
    {
        tag=NormalizeTag(tag);if(Strong(tag))return tag;
        if(tag is null||tag.Length is <1 or >512||tag.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not '-' and not '_' and not '.' and not '='))return null;
        return "\""+tag+"\"";
    }
    /// <summary>验证过的单范围响应才可用于预览；200、弱／裸HTTP响应及缺失版本直接回退，不读取整文件。</summary>
    public async ValueTask<PreviewRangeResult> TryReadPreviewRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
    {
        using var sourceContext=MountingHttpScheduler.EnterSourceOperation(_queueBinding,CloudOperationChannel.Thumbnail,entry.Path,entry.StableId,entry.Version);
        using var sourceWorkflow=_queueBinding is null?null:await _queueBinding.Queue.AcquireCurrentWorkflowAsync(token).ConfigureAwait(false);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);if(entry.IsDirectory||offset>entry.Size)throw new ArgumentException("预览范围无效。");
        var count=(int)Math.Min(buffer.Length,entry.Size-offset);if(count==0)return new(true,0);
        var tag=PreviewReadCandidate(Version(entry).ETag);if(tag is null){Volatile.Write(ref _previewRangeDiagnostic,"元数据没有可验证的预览版本");return new(false,0);}
        using var request=new HttpRequestMessage(HttpMethod.Get,Address(entry.Path));request.Headers.Range=new RangeHeaderValue(offset,offset+count-1);request.Headers.IfMatch.ParseAdd(tag!);
        HttpResponseMessage response;
        try { response=await SendAsync(request,token,strictRead:true).ConfigureAwait(false); }
        catch(DavRangeIgnoredException){Volatile.Write(ref _previewRangeDiagnostic,"服务器未遵守范围请求");return new(false,0);}
        catch(DavMalformedRangeException){throw new SourceConflictException("预览范围无效，未读取完整文件。");}
        using var owned=response;Success(response);
        var range=response.Content.Headers.ContentRange;var receivedTag=NormalizeTag(response.Headers.TryGetValues("ETag",out var tags)?tags.SingleOrDefault():null);
        if(response.StatusCode!=HttpStatusCode.PartialContent){Volatile.Write(ref _previewRangeDiagnostic,"服务器未返回206范围响应");return new(false,0);}
        if(!Strong(receivedTag)){Volatile.Write(ref _previewRangeDiagnostic,"HTTP响应没有合法强版本标签");return new(false,0);}
        if(range?.Unit!="bytes"||range.From!=offset||range.To!=offset+count-1||range.Length!=entry.Size||response.Content.Headers.ContentLength!=count||receivedTag!=tag)throw new SourceConflictException("预览范围或版本变化，未发布缓存。");
        Volatile.Write(ref _previewRangeDiagnostic,"严格范围与版本已验证");await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);await stream.ReadExactlyAsync(buffer[..count],token).ConfigureAwait(false);return new(true,count);
    }
}
