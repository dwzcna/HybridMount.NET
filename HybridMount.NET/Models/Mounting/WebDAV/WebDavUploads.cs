using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>DAV 写入按操作验证；不将可选 ETag 或其他方法的限制传递给上传。</summary>
public sealed partial class WebDavFileSource
{
    private bool _deleteVerified, _moveVerified, _lockVerified, _createMoveVerified;
    public SourceWriteCapabilities WriteCapabilities { get; private set; } = new();
    public event Action<SourceContentBaseline>? ContentBaseline;
    public event Action<string>? WriteWarning;
    private void Warn(string message) { try { WriteWarning?.Invoke(message); } catch { /* 通知不能改变已提交的网络结果。 */ } }
    /// <summary>使用普通文件名兼容拒绝隐藏路径的服务；创建前仍登记精确意图并验证不存在。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> _sessionProbePaths=new(StringComparer.Ordinal);
    /// <summary>只过滤精确登记的内部临时项，Foreign仍可见；已清理探测路径供迟到REPORT排除。</summary>
    private bool IsInternalTemporary(string path)=>_temporary.TryGetValue(path,out var item)?item.Stage!="Foreign":_sessionProbePaths.ContainsKey(path);
    private SourceRequestRejectedException? _rootProbeWriteRejection;
    private string ProbePath(string parent = "") => TrackTemporary((_rootProbeWriteRejection is not null&&parent.Length>0?parent+"/":"")+"hybridmount-probe-"+Guid.NewGuid().ToString("N"));
    /// <summary>在写入前记录意图，根明确拒绝写入时一次回退目标父目录；只读预检失败不回退。</summary>
    private async Task<string> CreateProbeAsync(string parent,byte[] bytes,CancellationToken token)
    {
        if(_rootProbeWriteRejection is not null&&parent.Length==0)throw _rootProbeWriteRejection;
        var atRoot=_rootProbeWriteRejection is null;var path=ProbePath(parent);
        try
        {
            while(true)
            {
                using var request=new HttpRequestMessage(HttpMethod.Put,Address(path)){Content=new ByteArrayContent(bytes)};
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                using var response=await SendAsync(request,token,true).ConfigureAwait(false);
                if(atRoot&&response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    try{Success(response);}catch(SourceRequestRejectedException error){_rootProbeWriteRejection=error;}
                    if(parent.Length==0)throw _rootProbeWriteRejection!;
                    path=ProbePath(parent);atRoot=false;continue;
                }
                Success(response);return path;
            }
        }
        catch{await CleanupProbesAsync(path).ConfigureAwait(false);throw;}
    }
    private async Task CleanupProbesAsync(params string[] paths)
    {
        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach(var path in paths)await CleanupTemporaryAsync(path,cleanup.Token).ConfigureAwait(false);
    }
    /// <summary>只验证 PUT 创建和更新，不要求 DELETE 或 MOVE 支持条件修改。</summary>
    private async Task VerifyPutCapabilitiesAsync(string parent,CancellationToken token)
    {
        if(_writesVerified)return;await _writeProbe.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(_writesVerified)return;var path=await CreateProbeAsync(parent,[17,31],token).ConfigureAwait(false);
            try
            {
                foreach(var create in new[]{true,false})
                {
                    using var request=new HttpRequestMessage(HttpMethod.Put,Address(path)){Content=new ByteArrayContent([99])};
                    if(create)request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                    else request.Headers.IfMatch.ParseAdd("\"hybridmount-never-match-"+Guid.NewGuid().ToString("N")+"\"");
                    using var response=await SendAsync(request,token,true).ConfigureAwait(false);
                    if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)Success(response);
                    var support=response.StatusCode==HttpStatusCode.PreconditionFailed?CapabilitySupport.Supported:CapabilitySupport.Unsupported;
                    WriteCapabilities=create?WriteCapabilities with{CreateOnly=support}:WriteCapabilities with{ConditionalUpdate=support};
                }
                _writesVerified=true;
                Capabilities=Capabilities with{ConditionalWrite=WriteCapabilities.CreateOnly==CapabilitySupport.Supported&&WriteCapabilities.ConditionalUpdate==CapabilitySupport.Supported?CapabilitySupport.Supported:CapabilitySupport.Unsupported};
            }
            finally{await CleanupProbesAsync(path).ConfigureAwait(false);}
        }
        finally{_writeProbe.Release();}
    }
    /// <summary>删除、移动各自用独立临时项验证；结果不会禁用已经验证的 PUT。</summary>
    private async Task VerifyMutationAsync(string method,string parent,CancellationToken token)
    {
        if(method=="DELETE"?_deleteVerified:_moveVerified)
        {if((method=="DELETE"?WriteCapabilities.ConditionalDelete:WriteCapabilities.ConditionalMove)!=CapabilitySupport.Supported)throw new NotSupportedException("服务器不支持此方法的可靠条件写。");return;}
        await _writeProbe.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(method=="DELETE"?_deleteVerified:_moveVerified){if((method=="DELETE"?WriteCapabilities.ConditionalDelete:WriteCapabilities.ConditionalMove)!=CapabilitySupport.Supported)throw new NotSupportedException("服务器不支持此方法的可靠条件写。");return;}
            var path=await CreateProbeAsync(parent,[17,31],token).ConfigureAwait(false);var destination=TrackTemporary(path+"-destination");
            try
            {
                using var request=new HttpRequestMessage(new(method),Address(path));
                request.Headers.IfMatch.ParseAdd("\"hybridmount-never-match-"+Guid.NewGuid().ToString("N")+"\"");
                if(method=="MOVE"){request.Headers.Add("Destination",Address(destination).AbsoluteUri);request.Headers.Add("Overwrite","F");}
                using var result=await SendAsync(request,token,true).ConfigureAwait(false);
                var supported=result.StatusCode==HttpStatusCode.PreconditionFailed;
                if(supported&&method=="MOVE")supported=await VerifyMoveNoOverwriteAsync(path,parent,token).ConfigureAwait(false);
                if(method=="DELETE"){_deleteVerified=true;WriteCapabilities=WriteCapabilities with{ConditionalDelete=supported?CapabilitySupport.Supported:CapabilitySupport.Unsupported};}
                else{_moveVerified=true;WriteCapabilities=WriteCapabilities with{ConditionalMove=supported?CapabilitySupport.Supported:CapabilitySupport.Unsupported};}
                if(!supported)throw new NotSupportedException("服务器未通过此方法的条件写验证，已保留用户内容。");
            }
            finally{await CleanupProbesAsync(path,destination).ConfigureAwait(false);}
        }
        finally{_writeProbe.Release();}
        if((method=="DELETE"?WriteCapabilities.ConditionalDelete:WriteCapabilities.ConditionalMove)!=CapabilitySupport.Supported)
            throw new NotSupportedException("服务器不支持此方法的可靠条件写。");
    }
    /// <summary>源版本条件与目标禁止覆盖分别验证；只操作两个精确登记的独有临时项。</summary>
    private async Task<bool> VerifyMoveNoOverwriteAsync(string path,string parent,CancellationToken token)
    {
        var target=await CreateProbeAsync(parent,[47],token).ConfigureAwait(false);
        try
        {
            var before=await GetMetadataAsync(path,token).ConfigureAwait(false);var occupied=await GetMetadataAsync(target,token).ConfigureAwait(false);
            using var request=new HttpRequestMessage(new("MOVE"),Address(path));Condition(request,before.Version);request.Headers.Add("Destination",Address(target).AbsoluteUri);request.Headers.Add("Overwrite","F");
            using var response=await SendAsync(request,token,true).ConfigureAwait(false);
            if(response.StatusCode!=HttpStatusCode.PreconditionFailed)return false;
            var after=await TryGetMetadataAsync(path,token).ConfigureAwait(false);var destination=await TryGetMetadataAsync(target,token).ConfigureAwait(false);
            return after is not null&&destination is not null&&IsSameVersion(after.Version,before.Version)&&IsSameVersion(destination.Version,occupied.Version);
        }
        finally{await CleanupProbesAsync(target).ConfigureAwait(false);}
    }
    /// <summary>发送前按连接验证条件写与所需回退能力；可取消，探测失败保留用户目标和原上传意图。</summary>
    public async Task PrepareUploadAsync(string path,UploadPrecondition condition,long contentLength,CancellationToken token=default)
    {
        path=NonRoot(path);var strong=condition.ExpectedVersion is not null&&Strong(System.Text.Json.JsonSerializer.Deserialize<VersionInfo>(condition.ExpectedVersion)?.ETag);
        if(condition.ExpectedVersion is not null&&!strong&&string.IsNullOrEmpty(condition.BaselineSha256))throw new SourceConflictException("缺少原始远端内容基线，已保留本地修改；请另存副本。");
        var parent=RelativePaths.Parent(path);await VerifyPutCapabilitiesAsync(parent,token).ConfigureAwait(false);
        if(condition.ExpectedVersion is null?WriteCapabilities.CreateOnly==CapabilitySupport.Supported:strong&&WriteCapabilities.ConditionalUpdate==CapabilitySupport.Supported)return;
        try{await VerifyLocksAsync(parent,token).ConfigureAwait(false);}
        catch(NotSupportedException)when(condition.ExpectedVersion is null){await VerifyCreateMoveAsync(parent,token).ConfigureAwait(false);return;}
        if(condition.ExpectedVersion is null&&WriteCapabilities.LockCreate!=CapabilitySupport.Supported)throw new NotSupportedException("服务器不能可靠地仅创建新文件。");
    }
    /// <summary>兼容旧上传调用；缺少可靠 ETag 时，更新必须提供原始内容基线。</summary>
    public Task<SourceEntry> UploadAsync(string path,Stream content,string? expectedVersion,CancellationToken token=default)
        =>UploadWithPreconditionAsync(path,content,new(expectedVersion),null,token);
    /// <summary>上传调用者的不可变流；条件失败保留远端，提交后的核对失败报告结果不明。</summary>
    public async Task<SourceEntry> UploadWithPreconditionAsync(string path,Stream content,UploadPrecondition condition,
        IProgress<UploadProgress>? progress=null,CancellationToken token=default)
    {
        path=NonRoot(path);
        var strong=condition.ExpectedVersion is not null&&Strong(System.Text.Json.JsonSerializer.Deserialize<VersionInfo>(condition.ExpectedVersion)?.ETag);
        if(condition.ExpectedVersion is not null&&!strong&&string.IsNullOrEmpty(condition.BaselineSha256))
            throw new SourceConflictException("缺少原始远端内容基线，已保留本地修改；请另存副本。");
        await VerifyPutCapabilitiesAsync(RelativePaths.Parent(path),token).ConfigureAwait(false);
        var direct=condition.ExpectedVersion is null?WriteCapabilities.CreateOnly==CapabilitySupport.Supported:
            strong&&WriteCapabilities.ConditionalUpdate==CapabilitySupport.Supported;
        DavLockLease? lease=null;var weakUpdate=condition.ExpectedVersion is not null&&!strong;
        try
        {
            if(!direct)
            {
                try{await VerifyLocksAsync(RelativePaths.Parent(path),token).ConfigureAwait(false);}
                catch(NotSupportedException)when(condition.ExpectedVersion is null)
                {return await UploadViaCreateMoveAsync(path,content,progress,token).ConfigureAwait(false);}
                if(condition.ExpectedVersion is null&&WriteCapabilities.LockCreate!=CapabilitySupport.Supported)
                    throw new NotSupportedException("服务器不能可靠地仅创建新文件。");
                lease=await AcquireLockAsync(path,token,condition.ExpectedVersion is not null).ConfigureAwait(false);
                if((condition.ExpectedVersion is null&&lease.Status!=201)||(condition.ExpectedVersion is not null&&lease.Status!=200))
                    throw new SourceConflictException("锁定目标的存在状态变化，已拒绝覆盖。");
                lease.Start();
                if(condition.ExpectedVersion is not null)
                {
                    // 弱标签、长度和时间相同仍可能是锁建立前的外部编辑，必须核对原始摘要。
                    var actual=await ReadContentHashAsync(path,lease.Token,lease.Cancellation).ConfigureAwait(false);
                    if(!string.Equals(actual,condition.BaselineSha256,StringComparison.OrdinalIgnoreCase))throw new SourceConflictException("远端内容已变化，保留两端文件，请处理冲突。");
                }
            }
            using var input=new UploadProgressStream(content,progress);
            using var request=new HttpRequestMessage(HttpMethod.Put,Address(path)){Content=new StreamContent(new BorrowedStream(input))};
            if(lease is null)Condition(request,condition.ExpectedVersion);else request.Headers.Add("If",lease.IfHeader);
            using var response=await SendAsync(request,lease?.Cancellation??token,true).ConfigureAwait(false);Success(response);
            var digest=input.Digest;
            var receipt=NormalizeTag(response.Headers.TryGetValues("ETag",out var tags)?tags.SingleOrDefault():null);
            try
            {
                var metadata=await GetMetadataAsync(path,lease?.Cancellation??token).ConfigureAwait(false);
                if(metadata.IsDirectory||metadata.Size!=input.BytesRead)throw new IOException("上传后类型或长度不符。");
                if(weakUpdate&&lease is not null)lease.Cancellation.ThrowIfCancellationRequested();
                if(Strong(receipt))
                {
                    if(NormalizeTag(Version(metadata).ETag)!=receipt)throw new IOException("上传回执与当前远端版本不同。");
                }
                else if(!(weakUpdate&&lease is not null&&WriteCapabilities.ExclusiveLock==CapabilitySupport.Supported&&
                    Strong(Version(metadata).ETag)))
                {
                    var readback=await ReadContentHashAsync(path,lease?.Token,lease?.Cancellation??token).ConfigureAwait(false);
                    if(readback!=digest)throw new IOException("上传后内容摘要不符。");
                    var after=await GetMetadataAsync(path,lease?.Cancellation??token).ConfigureAwait(false);
                    if(!IsSameVersion(metadata.Version,after.Version))throw new IOException("上传核对期间远端版本变化。");
                }
                try{ContentBaseline?.Invoke(new(path,metadata.Version,digest));}catch{ /* 观察者失败不能导致重复写入。 */ }
                return metadata;
            }
            catch(Exception error)when(error is IOException or OperationCanceledException)
            {throw new SourceOutcomeUnknownException("DAV 上传后核对",new IOException("写入已响应，但未能确认对应内容与版本。"));}
        }
        finally{if(lease is not null)await lease.DisposeAsync().ConfigureAwait(false);}
    }
    private async Task<string> ReadContentHashAsync(string path,string? lockToken,CancellationToken token,string? expectedTag=null)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,Address(path));
        if(expectedTag is not null)request.Headers.IfMatch.ParseAdd(expectedTag);
        if(lockToken is not null)request.Headers.Add("If","(<"+lockToken+">)");
        using var response=await SendAsync(request,token).ConfigureAwait(false);Success(response);
        if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("内容核对需要完整响应。");
        await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream,token).ConfigureAwait(false));
    }
}
