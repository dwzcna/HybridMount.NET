using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace HybridMount.Models.Mounting.Synology;

public sealed partial class SynologyFileSource
{
    private async Task<HttpResponseMessage> DownloadAsync(SourceEntry entry,long? offset,int count,CancellationToken token)
    {
        for(var attempt=0;;attempt++)
        {
            var sid=await EnsureSessionAsync(token).ConfigureAwait(false);var api="SYNO.FileStation.Download";
            var fields=new Dictionary<string,string>{["api"]=api,["version"]=Number(Descriptor(api).Version),["method"]="download",["mode"]="download",["path"]=Paths(RemotePath(entry.Path)),["_sid"]=sid};
            using var query=new FormUrlEncodedContent(fields);
            using var request=new HttpRequestMessage(HttpMethod.Get,new Uri(ApiAddress(api)+"?"+await query.ReadAsStringAsync(token).ConfigureAwait(false)));
            // 待实测：FileStation.Download 在 DSM 7.x 及反向代理后的 Range/206 支持。
            if(offset is {} start)request.Headers.Range=new RangeHeaderValue(start,start+count-1);
            var response=await SendAsync(request,false,token).ConfigureAwait(false);
            if(response.Content.Headers.ContentDisposition is null&&(response.Content.Headers.ContentType?.MediaType is "application/json" or "text/json"))
            {
                try{await ParseResponseAsync(response,false,token).ConfigureAwait(false);throw new IOException("群晖下载返回了非文件响应。");}
                catch(SynologyApiException error)when(error.Code is 106 or 107 or 119)
                {
                    response.Dispose();if(attempt!=0)throw new SynologyAuthenticationException(error.Code);
                    await EnsureSessionAsync(token,sid).ConfigureAwait(false);continue;
                }
                finally{response.Dispose();}
            }
            if(!response.IsSuccessStatusCode&&!(offset is not null&&response.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable)){var code=(int)response.StatusCode;response.Dispose();throw new SourceRequestRejectedException(code,"群晖下载被拒绝。");}
            return response;
        }
    }
    private async Task CheckVersionAsync(SourceEntry entry,CancellationToken token)
    {var current=await GetMetadataAsync(entry.Path,token).ConfigureAwait(false);if(current.IsDirectory!=entry.IsDirectory||!IsSameVersion(entry.Version,current.Version))throw new SourceConflictException("群晖远端版本已变化，请刷新后重试。");}
    public async ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();ValidateRange(entry,offset);var count=(int)Math.Min(buffer.Length,entry.Size-offset);if(count==0)return 0;
        if(Capabilities.RangeRead!=CapabilitySupport.Unsupported)
        {
            var result=await TryRangeAsync(entry,offset,buffer[..count],token).ConfigureAwait(false);
            if(result.Supported)return result.Count;
        }
        return await _cache.ReadAsync(_endpoint.AbsoluteUri+_root,entry,offset,buffer[..count],
            (output,cancel)=>DownloadSnapshotAsync(entry,output,cancel),token).ConfigureAwait(false);
    }
    public async ValueTask<PreviewRangeResult> TryReadPreviewRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();ValidateRange(entry,offset);if(Capabilities.RangeRead==CapabilitySupport.Unsupported)return new(false,0);
        var count=(int)Math.Min(buffer.Length,entry.Size-offset);if(count==0)return new(true,0);
        return await TryRangeAsync(entry,offset,buffer[..count],token).ConfigureAwait(false);
    }
    private static void ValidateRange(SourceEntry entry,long offset)
    {ArgumentOutOfRangeException.ThrowIfNegative(offset);if(entry.IsDirectory||offset>entry.Size)throw new ArgumentException("群晖读取范围无效。");}
    private async ValueTask<PreviewRangeResult> TryRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token)
    {
        await CheckVersionAsync(entry,token).ConfigureAwait(false);
        using(var response=await DownloadAsync(entry,offset,buffer.Length,token).ConfigureAwait(false))
        {
            if(response.StatusCode!=HttpStatusCode.PartialContent){Capabilities=Capabilities with{RangeRead=CapabilitySupport.Unsupported};return new(false,0);}
            var range=response.Content.Headers.ContentRange;
            if(response.StatusCode!=HttpStatusCode.PartialContent||range?.Unit!="bytes"||range.From!=offset||range.To!=offset+buffer.Length-1||range.Length!=entry.Size||response.Content.Headers.ContentLength!=buffer.Length)
            {Capabilities=Capabilities with{RangeRead=CapabilitySupport.Unsupported};return new(false,0);}
            await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var filled=0;
            while(filled<buffer.Length){var count=await ReadIdleAsync(stream,buffer[filled..],token).ConfigureAwait(false);if(count==0)throw new EndOfStreamException();filled+=count;}
            if(await ReadIdleAsync(stream,new byte[1],token).ConfigureAwait(false)!=0)throw new IOException("群晖范围响应包含多余字节。");
        }
        await CheckVersionAsync(entry,token).ConfigureAwait(false);Capabilities=Capabilities with{RangeRead=CapabilitySupport.Supported};return new(true,buffer.Length);
    }
    private async Task<string> Md5Async(string path,CancellationToken token)
    {
        var started=await ApiAsync("SYNO.FileStation.MD5","start",new(){["file_path"]=RemotePath(path)},false,token).ConfigureAwait(false);
        var id=started.GetProperty("taskid").GetString()!;
        // 待实测：MD5 后台任务的状态字段及不同 DSM 7.x 版本对取消后任务的回收。
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(5));
        while(true)
        {
            var result=await ApiAsync("SYNO.FileStation.MD5","status",new(){["taskid"]=id},false,deadline.Token).ConfigureAwait(false);
            CheckTaskErrors(result);
            if(result.GetProperty("finished").GetBoolean())
            {
                var hash=result.GetProperty("md5").GetString();
                if(hash is not {Length:32}||hash.Any(c=>!char.IsAsciiHexDigit(c)))throw new IOException("群晖 MD5 响应无效。");
                return hash.ToUpperInvariant();
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500),deadline.Token).ConfigureAwait(false);
        }
    }
    private async Task<string> DownloadSnapshotAsync(SourceEntry entry,Stream output,CancellationToken token)
    {
        await CheckVersionAsync(entry,token).ConfigureAwait(false);var expected=await Md5Async(entry.Path,token).ConfigureAwait(false);
        using var md5=IncrementalHash.CreateHash(HashAlgorithmName.MD5);using var sha256=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using(var response=await DownloadAsync(entry,null,0,token).ConfigureAwait(false))
        {
            if(response.StatusCode!=HttpStatusCode.OK||response.Content.Headers.ContentLength is {} length&&length!=entry.Size)throw new IOException("群晖整文件响应无效。");
            await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes=new byte[65536];var remaining=entry.Size;
            while(remaining>0)
            {
                var read=await ReadIdleAsync(stream,bytes.AsMemory(0,(int)Math.Min(bytes.Length,remaining)),token).ConfigureAwait(false);
                if(read==0)throw new EndOfStreamException();md5.AppendData(bytes,0,read);sha256.AppendData(bytes,0,read);
                await output.WriteAsync(bytes.AsMemory(0,read),token).ConfigureAwait(false);remaining-=read;
            }
            if(await ReadIdleAsync(stream,bytes.AsMemory(0,1),token).ConfigureAwait(false)!=0)throw new IOException("群晖整文件响应过长。");
        }
        if(Convert.ToHexString(md5.GetHashAndReset())!=expected||await Md5Async(entry.Path,token).ConfigureAwait(false)!=expected)throw new SourceConflictException("群晖文件下载期间内容发生变化。");
        await CheckVersionAsync(entry,token).ConfigureAwait(false);var digest=Convert.ToHexString(sha256.GetHashAndReset());
        PublishBaseline(new(entry.Path,entry.Version,digest));return digest;
    }
    private static async ValueTask<int> ReadIdleAsync(Stream stream,Memory<byte> buffer,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try{return await stream.ReadAsync(buffer,deadline.Token).ConfigureAwait(false);}
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new IOException("群晖文件读取长时间没有进展。");}
    }
}
