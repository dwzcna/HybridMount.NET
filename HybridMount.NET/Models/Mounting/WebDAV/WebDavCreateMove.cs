using System.Net;
using System.Security.Cryptography;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>不支持条件 PUT 的服务可用经验证的 MOVE Overwrite:F 提交新文件；更新仍要求可靠条件。</summary>
public sealed partial class WebDavFileSource
{
    private async Task MoveNoOverwriteAsync(string source,string destination,CancellationToken token)
    {
        using var request=new HttpRequestMessage(new("MOVE"),Address(source));request.Headers.Add("Destination",Address(destination).AbsoluteUri);request.Headers.Add("Overwrite","F");
        using var response=await SendAsync(request,token,true).ConfigureAwait(false);Success(response);
    }
    private async Task VerifyCreateMoveAsync(string parent,CancellationToken token)
    {
        await _writeProbe.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(_createMoveVerified)
            {if(WriteCapabilities.MoveCreate!=CapabilitySupport.Supported)throw new NotSupportedException("服务器没有可验证的防覆盖创建方式。");return;}
            var first=await CreateProbeAsync(parent,[17,31],token).ConfigureAwait(false);var second=TrackTemporary(first+"-destination");var third=TrackTemporary(first+"-new");
            try
            {
                foreach(var item in new[]{(Path:second,Bytes:new byte[]{47})})
                {
                    using var request=new HttpRequestMessage(HttpMethod.Put,Address(item.Path)){Content=new ByteArrayContent(item.Bytes)};request.Headers.IfNoneMatch.ParseAdd("*");
                    using var response=await SendAsync(request,token,true).ConfigureAwait(false);Success(response);
                }
                var rejected=false;
                try{await MoveNoOverwriteAsync(first,second,token).ConfigureAwait(false);}catch(SourceConflictException){rejected=true;}
                if(!rejected||await ReadContentHashAsync(second,null,token).ConfigureAwait(false)!=Convert.ToHexString(SHA256.HashData(new byte[]{47})))
                    throw new NotSupportedException("DAV MOVE 没有保护已有目标，已拒绝用户文件上传。");
                await MoveNoOverwriteAsync(first,third,token).ConfigureAwait(false);
                if(await ReadContentHashAsync(third,null,token).ConfigureAwait(false)!=Convert.ToHexString(SHA256.HashData(new byte[]{17,31})))throw new NotSupportedException("DAV MOVE 创建后的内容核对失败。");
                WriteCapabilities=WriteCapabilities with{MoveCreate=CapabilitySupport.Supported};_createMoveVerified=true;
            }
            catch(NotSupportedException){WriteCapabilities=WriteCapabilities with{MoveCreate=CapabilitySupport.Unsupported};_createMoveVerified=true;throw;}
            finally{await CleanupProbesAsync(first,second,third).ConfigureAwait(false);}
        }
        finally{_writeProbe.Release();}
    }
    private async Task<SourceEntry> UploadViaCreateMoveAsync(string path,Stream content,IProgress<UploadProgress>? progress,CancellationToken token)
    {
        await VerifyCreateMoveAsync(RelativePaths.Parent(path),token).ConfigureAwait(false);
        await using var staged=content.CanSeek?null:new FileStream(Path.Combine(Path.GetTempPath(),"HybridMount-upload-"+Guid.NewGuid().ToString("N")),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,131072,FileOptions.Asynchronous|FileOptions.DeleteOnClose);
        if(staged is not null){await content.CopyToAsync(staged,token).ConfigureAwait(false);staged.Position=0;content=staged;}
        var position=content.Position;var expected=Convert.ToHexString(await SHA256.HashDataAsync(content,token).ConfigureAwait(false));content.Position=position;
        var temporary=TrackTemporary((RelativePaths.Parent(path).Length==0?"":RelativePaths.Parent(path)+"/")+"hybridmount-upload-"+Guid.NewGuid().ToString("N"),"upload",[expected]);
        try
        {
            using var input=new UploadProgressStream(content,progress);
            using(var request=new HttpRequestMessage(HttpMethod.Put,Address(temporary)){Content=new StreamContent(new BorrowedStream(input))})
            {request.Headers.IfNoneMatch.ParseAdd("*");using var response=await SendAsync(request,token,true).ConfigureAwait(false);Success(response);}
            await MoveNoOverwriteAsync(temporary,path,token).ConfigureAwait(false);
            try
            {
                var metadata=await GetMetadataAsync(path,token).ConfigureAwait(false);var digest=input.Digest;
                if(metadata.IsDirectory||metadata.Size!=input.BytesRead||await ReadContentHashAsync(path,null,token).ConfigureAwait(false)!=digest)throw new IOException("MOVE 提交后内容不匹配。");
                var after=await GetMetadataAsync(path,token).ConfigureAwait(false);
                if(!IsSameVersion(metadata.Version,after.Version))throw new IOException("MOVE 提交核对期间远端版本变化。");
                try{ContentBaseline?.Invoke(new(path,metadata.Version,digest));}catch{ /* 不重复已经提交的创建。 */ }
                return metadata;
            }
            catch(Exception error)when(error is IOException or OperationCanceledException){throw new SourceOutcomeUnknownException("DAV 新文件提交核对",new IOException("写入后未能核对内容。"));}
        }
        finally{await CleanupProbesAsync(temporary).ConfigureAwait(false);}
    }
}
