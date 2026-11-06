using System.Net;
using Amazon.S3.Model;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>S3 写入能力逐项验证，普通上传不依赖删除、复制和分片权限。</summary>
public sealed partial class S3FileSource
{
    private bool _deleteVerified,_copyProbeVerified;
    public SourceWriteCapabilities WriteCapabilities { get; private set; }=new();
    public event Action<SourceContentBaseline>? ContentBaseline;
    public event Action<string>? WriteWarning;
    private void Warn(string message){try{WriteWarning?.Invoke(message);}catch{ /* 通知不改变已提交结果。 */ }}
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> _sessionProbeKeys=new(StringComparer.Ordinal);
    /// <summary>写入前登记精确探测键，仅过滤本会话已知路径；不按名称前缀认领外部对象。</summary>
    private string TrackProbeKey(string key){_sessionProbeKeys.TryAdd(key,0);return key;}
    private SourceConnectionException? _rootProbeWriteRejection;
    /// <summary>默认限定配置根；仅缓存根写权限拒绝后改用本操作的目标父目录，仍不越过挂载前缀。</summary>
    private string ProbeKey(string parent="",string kind="")=>TrackProbeKey(_prefix+(_rootProbeWriteRejection is not null&&parent.Length>0?parent.TrimEnd('/')+"/":"")+".hybridmount-probe-"+kind+Guid.NewGuid().ToString("N"));
    private static bool ProbePermissionRejected(SourceConnectionException error)=>error.Diagnostic.HttpStatus is 401 or 403||error.Diagnostic.ServiceCode=="AccessDenied";
    /// <summary>条件创建探测项；明确拒绝时撤销认领，网络结果不明继续保留记录。</summary>
    private async Task PutProbeAsync(string key,byte[] bytes,CancellationToken token)
    {
        try{await PutAsync(key,new MemoryStream(bytes),null,token).ConfigureAwait(false);}
        catch(SourceConflictException){_sessionProbeKeys.TryRemove(key,out _);throw;}
        catch(SourceConnectionException error)when(ProbePermissionRejected(error)){_sessionProbeKeys.TryRemove(key,out _);throw;}
    }
    /// <summary>同一连接在根写权限被明确拒绝后最多回退到目标父目录；其他错误或取消不更换位置。</summary>
    private async Task<string> CreateProbeAsync(string parent,byte[] bytes,CancellationToken token,string kind="",bool cleanupOnFailure=true)
    {
        if(_rootProbeWriteRejection is not null&&parent.Length==0)throw _rootProbeWriteRejection;
        var atRoot=_rootProbeWriteRejection is null;var key=ProbeKey(parent,kind);
        try
        {
            try{await PutProbeAsync(key,bytes,token).ConfigureAwait(false);}
            catch(SourceConnectionException error)when(atRoot&&ProbePermissionRejected(error))
            {
                _rootProbeWriteRejection=error;if(parent.Length==0)throw;
                key=ProbeKey(parent,kind);await PutProbeAsync(key,bytes,token).ConfigureAwait(false);
            }
            return key;
        }
        catch{if(cleanupOnFailure)await CleanupProbeKeysAsync(key).ConfigureAwait(false);throw;}
    }
    private async Task CleanupProbeKeysAsync(params string[] keys)
    {
        using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach(var key in keys)
            if(_sessionProbeKeys.ContainsKey(key))
            try{await DeleteKeyAsync(key,null,cleanup.Token).ConfigureAwait(false);}
            catch(Exception error)when(error is IOException or OperationCanceledException){Warn("S3 探测临时对象清理未确认："+key);}
    }
    private async Task VerifyPutCapabilitiesAsync(string parent,CancellationToken token)
    {
        if(_writesVerified)return;await _probeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(_writesVerified)return;var key=await CreateProbeAsync(parent,[17,31],token,cleanupOnFailure:false).ConfigureAwait(false);
            try
            {
                async Task<CapabilitySupport> Check(string? expected)
                {
                    return await ProbePutAsync(key,new MemoryStream([99]),expected,token).ConfigureAwait(false) is null?CapabilitySupport.Supported:CapabilitySupport.Unsupported;
                }
                var create=await Check(null).ConfigureAwait(false);
                var update=await Check("\"hybridmount-never-match-"+Guid.NewGuid().ToString("N")+"\"").ConfigureAwait(false);
                WriteCapabilities=WriteCapabilities with{CreateOnly=create,ConditionalUpdate=update};
                Capabilities=Capabilities with{ConditionalWrite=create==CapabilitySupport.Supported&&update==CapabilitySupport.Supported?CapabilitySupport.Supported:CapabilitySupport.Unsupported};
                _writesVerified=true;
            }
            finally{await CleanupProbeKeysAsync(key).ConfigureAwait(false);}
        }
        finally{_probeGate.Release();}
    }
    private void RequirePut(bool create)
    {
        if((create?WriteCapabilities.CreateOnly:WriteCapabilities.ConditionalUpdate)!=CapabilitySupport.Supported)
            throw new NotSupportedException(create?"S3 服务不支持可靠的仅创建上传。":"S3 服务不支持可靠的条件更新。");
    }
    private async Task VerifyDeleteAsync(string parent,CancellationToken token)
    {
        await _probeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(!_deleteVerified)
            {
                var key=await CreateProbeAsync(parent,[17,31],token).ConfigureAwait(false);
                try
                {
                    var supported=await CallCore(()=>_client.DeleteObjectAsync(new(){BucketName=_bucket,Key=key,IfMatch="\"hybridmount-never-match\""},token),token,true,ExpectedResult.PreconditionRejected).ConfigureAwait(false) is null;
                    WriteCapabilities=WriteCapabilities with{ConditionalDelete=supported?CapabilitySupport.Supported:CapabilitySupport.Unsupported};
                    _deleteVerified=true;
                }
                finally{await CleanupProbeKeysAsync(key).ConfigureAwait(false);}
            }
            if(WriteCapabilities.ConditionalDelete!=CapabilitySupport.Supported)throw new NotSupportedException("S3 服务未通过条件删除验证。");
        }
        finally{_probeGate.Release();}
    }
    private async Task VerifyCopyAsync(string parent,CancellationToken token)
    {
        if(_copyProbeVerified)return;await _probeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(_copyProbeVerified)return;var first=await CreateProbeAsync(parent,[17,31],token).ConfigureAwait(false);var second=TrackProbeKey(first+"-destination");
            try
            {
                await PutProbeAsync(second,[47],token).ConfigureAwait(false);
                var head=await HeadAsync(first,token).ConfigureAwait(false);
                try
                {
                    _copyVerified=await CallCore(()=>_client.CopyObjectAsync(new(){SourceBucket=_bucket,SourceKey=first,DestinationBucket=_bucket,DestinationKey=second,
                        ETagToMatch=head.ETag,IfNoneMatch="*"},token),token,true,ExpectedResult.PreconditionRejected).ConfigureAwait(false) is null;
                }
                catch(IOException){_copyVerified=false;}
                _copyProbeVerified=true;
                WriteCapabilities=WriteCapabilities with{ConditionalMove=_copyVerified?CapabilitySupport.Supported:CapabilitySupport.Unsupported};
            }
            finally{await CleanupProbeKeysAsync(first,second).ConfigureAwait(false);}
        }
        finally{_probeGate.Release();}
    }
    /// <summary>发送前按连接验证条件写与所需回退能力；可取消，探测失败保留用户目标和原上传意图。</summary>
    public async Task PrepareUploadAsync(string path,UploadPrecondition condition,long contentLength,CancellationToken token=default)
    {
        path=NonRoot(path);await VerifyPutCapabilitiesAsync(RelativePaths.Parent(path),token).ConfigureAwait(false);RequirePut(condition.ExpectedVersion is null);
        await CheckTargetAsync(path,condition.ExpectedVersion,token).ConfigureAwait(false);
        if(condition.ExpectedVersion is not null)_=RequiredETag(Version(condition.ExpectedVersion));
        if(contentLength>=_multipartThreshold){var key=Key(path);await VerifyMultipartAsync(key[..(key.LastIndexOf('/')+1)],token).ConfigureAwait(false);}
    }
    /// <summary>兼容旧调用，结果必须与 PUT 或分片完成回执对应。</summary>
    public Task<SourceEntry> UploadAsync(string path,Stream content,string? expectedVersion,CancellationToken token=default)
        =>UploadWithPreconditionAsync(path,content,new(expectedVersion),null,token);
    /// <summary>条件上传及字节进度；SDK 不自动重试可能已经提交的写入。</summary>
    public async Task<SourceEntry> UploadWithPreconditionAsync(string path,Stream content,UploadPrecondition condition,
        IProgress<UploadProgress>? progress=null,CancellationToken token=default)
    {
        path=NonRoot(path);await VerifyPutCapabilitiesAsync(RelativePaths.Parent(path),token).ConfigureAwait(false);
        RequirePut(condition.ExpectedVersion is null);
        return await UploadCoreAsync(path,content,condition.ExpectedVersion,progress,token).ConfigureAwait(false);
    }
}
