using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace HybridMount.Models.Mounting.Synology;

/// <summary>先完整核验、后提供本地分块；没有服务器条件快照时绝不直接拼接远端范围。</summary>
public sealed partial class SynologyFileSource
{
    private async Task<HttpResponseMessage> DownloadAsync(SourceEntry entry,CancellationToken token)
    {
        for(var attempt=0;;attempt++)
        {
            var sid=await EnsureSessionAsync(token).ConfigureAwait(false);
            var fields=Fields("SYNO.FileStation.Download","download",new(){["path"]=Paths(RemotePath(entry.Path)),["mode"]="download"},sid);
            using var request=new HttpRequestMessage(HttpMethod.Post,ApiAddress("SYNO.FileStation.Download")){Content=new FormUrlEncodedContent(fields)};
            var response=await SendAsync(request,false,token).ConfigureAwait(false);
            try
            {
                if(response.Content.Headers.ContentType?.MediaType is "application/json" or "text/json")
                {
                    try{await ParseResponseAsync(response,false,token).ConfigureAwait(false);throw new IOException("群晖下载返回了非文件响应。");}
                    catch(SynologyApiException error)when(error.Code is 106 or 107 or 119)
                    {response.Dispose();if(attempt!=0)throw AuthenticationFailure(error.Code);await EnsureSessionAsync(token,sid).ConfigureAwait(false);continue;}
                    catch(SynologyApiException error){throw ApiFailure(error.Code);}
                }
                if(response.StatusCode!=HttpStatusCode.OK)throw new SourceRequestRejectedException((int)response.StatusCode,"群晖完整下载被拒绝。");
                return response;
            }
            catch{response.Dispose();throw;}
        }
    }
    private static string ContentMd5(SourceEntry entry)
    {
        var marker=entry.Version.LastIndexOf(";md5:",StringComparison.Ordinal);var digest=marker<0?"":entry.Version[(marker+5)..];
        if(digest.Length!=32||digest.Any(c=>!char.IsAsciiHexDigit(c)))throw new SourceConflictException("群晖文件缺少已核对的内容版本，请刷新后重试。");
        return digest.ToUpperInvariant();
    }
    /// <summary>首块需要完整快照；缓存版本包含摘要，后续分块只读同一不可变快照。</summary>
    public async ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();ValidateRange(entry,offset);var count=(int)Math.Min(buffer.Length,entry.Size-offset);if(count==0)return 0;
        _=ContentMd5(entry);using var shared=Share("snapshot:"+entry.Path+":"+entry.Version);
        return await _cache.ReadAsync(_endpoint.AbsoluteUri+_root,entry,offset,buffer[..count],
            (output,cancel)=>DownloadSnapshotAsync(entry,output,cancel),token,Share("snapshot:"+entry.Path+":"+entry.Version)).ConfigureAwait(false);
    }
    /// <summary>严格预览直接返回不支持；不登录、不做 MD5、不下载，也不触发水合。</summary>
    public ValueTask<PreviewRangeResult> TryReadPreviewRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
    {token.ThrowIfCancellationRequested();ValidateRange(entry,offset);return ValueTask.FromResult(new PreviewRangeResult(false,0));}
    private static void ValidateRange(SourceEntry entry,long offset)
    {ArgumentOutOfRangeException.ThrowIfNegative(offset);if(entry.IsDirectory||offset>entry.Size)throw new ArgumentException("群晖读取范围无效。");}
    private async Task<string> Md5Async(string path,CancellationToken token)
    {
        using var shared=Share("md5:"+path);
        return await _hashes.RunAsync(path,cancel=>Md5CoreAsync(path,cancel),token,Share("md5:"+path)).ConfigureAwait(false);
    }
    /// <summary>入队可取消；开始请求一旦发出，以五秒独立令牌领取任务标识，再由所有者执行停止。</summary>
    private async Task<JsonElement> StartMd5Async(string path,CancellationToken token)
    {
        for(var attempt=0;;attempt++)
        {
            var sid=await EnsureSessionAsync(token).ConfigureAwait(false);
            using var receipt=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var request=new HttpRequestMessage(HttpMethod.Post,ApiAddress("SYNO.FileStation.MD5"))
                {Content=new FormUrlEncodedContent(Fields("SYNO.FileStation.MD5","start",new(){["file_path"]=RemotePath(path)},sid))};
            try
            {
                using var response=await SendAsync(request,false,receipt.Token,token).ConfigureAwait(false);
                return await ParseResponseAsync(response,false,receipt.Token).ConfigureAwait(false);
            }
            catch(SynologyApiException error)when(error.Code is 106 or 107 or 119)
            {if(attempt!=0)throw AuthenticationFailure(error.Code);await EnsureSessionAsync(token,sid).ConfigureAwait(false);}
            catch(SynologyApiException error){throw ApiFailure(error.Code);}
        }
    }

    private async Task<string> Md5CoreAsync(string path,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromMinutes(5));
        string? id=null;var completed=false;
        try
        {
            var started=await StartMd5Async(path,deadline.Token).ConfigureAwait(false);
            id=started.GetProperty("taskid").GetString();if(string.IsNullOrEmpty(id))throw new IOException("群晖 MD5 任务未返回标识。");
            deadline.Token.ThrowIfCancellationRequested();
            while(true)
            {
                var result=await ApiAsync("SYNO.FileStation.MD5","status",new(){["taskid"]=id},false,deadline.Token).ConfigureAwait(false);
                if(result.GetProperty("finished").GetBoolean())
                {
                    var hash=result.GetProperty("md5").GetString();
                    if(hash is not{Length:32}||hash.Any(c=>!char.IsAsciiHexDigit(c)))throw new IOException("群晖 MD5 响应无效。");
                    completed=true;return hash.ToUpperInvariant();
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500),deadline.Token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw Failure("MD5",ConnectionFailureKind.Timeout,"群晖内容核对超时，未发布缓存。",transient:true);}
        finally
        {
            if(!completed&&id is not null)
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try{await ApiAsync("SYNO.FileStation.MD5","stop",new(){["taskid"]=id},false,cleanup.Token).ConfigureAwait(false);}
                catch(Exception error)when(error is IOException or OperationCanceledException){Warn("群晖内容核对任务停止未确认，请检查 NAS 后台任务。");}
            }
        }
    }
    private async Task DownloadSnapshotAsync(SourceEntry entry,Stream output,CancellationToken token)
    {
        var expected=ContentMd5(entry);var before=await GetMetadataAsync(entry.Path,token).ConfigureAwait(false);
        if(!IsSameVersion(entry.Version,before.Version))throw new SourceConflictException("群晖远端内容版本已变化，请刷新后重试。");
        using var md5=IncrementalHash.CreateHash(HashAlgorithmName.MD5);using var sha256=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using(var response=await DownloadAsync(entry,token).ConfigureAwait(false))
        {
            if(response.Content.Headers.ContentLength is {} length&&length!=entry.Size)throw new IOException("群晖整文件长度无效。");
            await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes=new byte[65536];var remaining=entry.Size;
            while(remaining>0)
            {
                var count=await ReadIdleAsync(stream,bytes.AsMemory(0,(int)Math.Min(bytes.Length,remaining)),token).ConfigureAwait(false);
                if(count==0)throw new EndOfStreamException("群晖完整快照提前结束。");
                md5.AppendData(bytes,0,count);sha256.AppendData(bytes,0,count);await output.WriteAsync(bytes.AsMemory(0,count),token).ConfigureAwait(false);remaining-=count;
            }
            if(await ReadIdleAsync(stream,bytes.AsMemory(0,1),token).ConfigureAwait(false)!=0)throw new IOException("群晖整文件响应过长。");
        }
        if(Convert.ToHexString(md5.GetHashAndReset())!=expected||!IsSameVersion(entry.Version,(await GetMetadataAsync(entry.Path,token).ConfigureAwait(false)).Version))
            throw new SourceConflictException("群晖文件下载期间内容发生变化，未发布快照。");
        token.ThrowIfCancellationRequested();PublishBaseline(new(entry.Path,entry.Version,Convert.ToHexString(sha256.GetHashAndReset())));
    }
    private static async ValueTask<int> ReadIdleAsync(Stream stream,Memory<byte> buffer,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try{return await stream.ReadAsync(buffer,deadline.Token).ConfigureAwait(false);}
        catch(OperationCanceledException)when(!token.IsCancellationRequested){throw Failure("文件读取",ConnectionFailureKind.Timeout,"群晖文件读取长时间没有进展。",transient:true);}
    }
}
