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
    private string TrackProbeKey(string key){_sessionProbeKeys.TryAdd(key,0);return key;}
    private string ProbeKey(string parent="")=>TrackProbeKey(_prefix+".hybridmount-probe-"+Guid.NewGuid().ToString("N"));
    private async Task PutProbeAsync(string key,byte[] bytes,CancellationToken token)
    {
        try{await PutAsync(key,new MemoryStream(bytes),null,token).ConfigureAwait(false);}
        catch(SourceConflictException){_sessionProbeKeys.TryRemove(key,out _);throw;}
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
            if(_writesVerified)return;var key=ProbeKey(parent);var created=false;
            try
            {
                await PutProbeAsync(key,[17,31],token).ConfigureAwait(false);created=true;
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
            finally{if(created)await CleanupProbeKeysAsync(key).ConfigureAwait(false);}
        }
        finally{_probeGate.Release();}
    }
    private void RequirePut(bool create)
    {
        if((create?WriteCapabilities.CreateOnly:WriteCapabilities.ConditionalUpdate)!=CapabilitySupport.Supported)
            throw new NotSupportedException(create?"S3 服务不支持可靠的仅创建上传。":"S3 服务不支持可靠的条件更新。");
    }
    private async Task VerifyDeleteAsync(CancellationToken token)
    {
        await _probeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(!_deleteVerified)
            {
                var key=ProbeKey();
                try
                {
                    await PutProbeAsync(key,[17,31],token).ConfigureAwait(false);
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
    private async Task VerifyCopyAsync(CancellationToken token)
    {
        if(_copyProbeVerified)return;await _probeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(_copyProbeVerified)return;var first=ProbeKey();var second=TrackProbeKey(first+"-destination");
            try
            {
                await PutProbeAsync(first,[17,31],token).ConfigureAwait(false);
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
