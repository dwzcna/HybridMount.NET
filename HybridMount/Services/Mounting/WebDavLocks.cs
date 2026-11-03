using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>独占写锁必须实际验证；锁令牌只在内存使用，不写入日志或配置。</summary>
public sealed partial class WebDavFileSource
{
    private async Task<DavLockLease> AcquireLockAsync(string path,CancellationToken token,bool mustExist=false)
    {
        using var request=new HttpRequestMessage(new("LOCK"),Address(path));
        if(mustExist)request.Headers.IfMatch.ParseAdd("*");
        request.Headers.Add("Depth","0");request.Headers.Add("Timeout","Second-300");
        request.Content=new StringContent("""
            <?xml version="1.0" encoding="utf-8"?><d:lockinfo xmlns:d="DAV:"><d:lockscope><d:exclusive/></d:lockscope><d:locktype><d:write/></d:locktype><d:owner><d:href>urn:hybridmount:upload</d:href></d:owner></d:lockinfo>
            """,Encoding.UTF8,"application/xml");
        using var response=await SendAsync(request,token,true).ConfigureAwait(false);
        if((int)response.StatusCode==423)throw new SourceConflictException("远端文件已被其他客户端锁定。");
        if(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)throw new NotSupportedException("服务器未实现 DAV 写锁。");
        Success(response);var info=await ParseLockAsync(response,token).ConfigureAwait(false);
        var lease=new DavLockLease(this,path,info.Token,info.Seconds,(int)response.StatusCode,token);
        if(mustExist&&lease.Status!=200)
        {
            // 不可靠服务忽略已验证条件时，仅在本次 201、仍持锁且确为空文件时回滚。
            try
            {
                var metadata=await GetMetadataAsync(path,token).ConfigureAwait(false);
                if(lease.Status==201&&!metadata.IsDirectory&&metadata.Size==0&&await ReadContentHashAsync(path,lease.Token,token).ConfigureAwait(false)==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])))
                {
                    using var rollback=new HttpRequestMessage(HttpMethod.Delete,Address(path));rollback.Headers.Add("If",lease.IfHeader);
                    if(Strong(Version(metadata).ETag))rollback.Headers.IfMatch.ParseAdd(NormalizeTag(Version(metadata).ETag)!);
                    using var removed=await SendAsync(rollback,token,true).ConfigureAwait(false);Success(removed);
                }
                else Warn("DAV 更新锁意外创建资源，身份或内容无法核对，保留现场。");
            }
            finally{await lease.DisposeAsync().ConfigureAwait(false);}
            throw new NotSupportedException("更新锁的存在性条件未被执行，已停止覆盖。");
        }
        return lease;
    }
    private static async Task<(string Token,int Seconds)> ParseLockAsync(HttpResponseMessage response,CancellationToken token)
    {
        await using var body=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader=XmlReader.Create(body,new XmlReaderSettings{Async=true,DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=262144});
        XDocument xml;
        try{xml=await XDocument.LoadAsync(reader,LoadOptions.None,token).ConfigureAwait(false);}
        catch(XmlException){throw new IOException("DAV 锁响应 XML 无效。");}
        var active=xml.Descendants(Dav+"activelock").FirstOrDefault()??throw new IOException("DAV 锁响应缺少活动锁。");
        if(active.Element(Dav+"lockscope")?.Element(Dav+"exclusive") is null||active.Element(Dav+"locktype")?.Element(Dav+"write") is null)
            throw new IOException("DAV 服务没有授予独占写锁。");
        var value=active.Element(Dav+"locktoken")?.Element(Dav+"href")?.Value.Trim();
        if(string.IsNullOrEmpty(value)||value.Length>2048||value.Any(c=>char.IsControl(c)||c is '<' or '>')||!Uri.TryCreate(value,UriKind.Absolute,out _))throw new IOException("DAV 锁令牌无效。");
        if(response.Headers.TryGetValues("Lock-Token",out var headers))
        {
            var header=headers.SingleOrDefault()?.Trim();
            if(header is not null&&header.StartsWith('<')&&header.EndsWith('>'))header=header[1..^1];
            // 部分 DAV 服务在响应头省略角括号；仅接受与 XML 令牌完全相同的值。
            if(header!=value)throw new IOException("DAV 锁令牌响应不一致。");
        }
        var timeout=active.Element(Dav+"timeout")?.Value??"Second-300";
        var seconds=300;
        if(timeout.StartsWith("Second-",StringComparison.OrdinalIgnoreCase)&&(!int.TryParse(timeout[7..],NumberStyles.None,CultureInfo.InvariantCulture,out seconds)||seconds<3))throw new IOException("DAV 锁有效期无效。");
        return(value,seconds);
    }
    private sealed class DavLockLease : IAsyncDisposable
    {
        private readonly WebDavFileSource _owner;
        private readonly string _path;
        private readonly CancellationTokenSource _stop;
        private Task _renew=Task.CompletedTask;
        private int _seconds,_disposed;
        public string Token { get; }
        public int Status { get; }
        public string IfHeader=>"(<"+Token+">)";
        public CancellationToken Cancellation=>_stop.Token;
        public DavLockLease(WebDavFileSource owner,string path,string token,int seconds,int status,CancellationToken parent)
        {_owner=owner;_path=path;Token=token;_seconds=seconds;Status=status;_stop=CancellationTokenSource.CreateLinkedTokenSource(parent);}
        public void Start()=>_renew=Task.Run(RenewLoopAsync);
        public async Task RefreshAsync(CancellationToken token)
        {
            using var request=new HttpRequestMessage(new("LOCK"),_owner.Address(_path));request.Headers.Add("If",IfHeader);request.Headers.Add("Timeout","Second-300");
            using var response=await _owner.SendAsync(request,token,true).ConfigureAwait(false);Success(response);
            var next=await ParseLockAsync(response,token).ConfigureAwait(false);
            if(next.Token!=Token)throw new IOException("DAV 续租改变了锁令牌。");_seconds=next.Seconds;
        }
        private async Task RenewLoopAsync()
        {
            try
            {
                while(true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1,Math.Min(30,_seconds/3))),_stop.Token).ConfigureAwait(false);
                    using var limit=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);limit.CancelAfter(TimeSpan.FromSeconds(Math.Max(1,_seconds/3)));
                    await RefreshAsync(limit.Token).ConfigureAwait(false);
                }
            }
            catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
            catch(Exception error)when(error is IOException or OperationCanceledException){_owner.Warn("DAV 锁续租失败，停止本次写入并保留结果核对状态。");await _stop.CancelAsync().ConfigureAwait(false);}
        }
        public async ValueTask DisposeAsync()
        {
            if(Interlocked.Exchange(ref _disposed,1)!=0)return;
            await _stop.CancelAsync().ConfigureAwait(false);await _renew.ConfigureAwait(false);
            try
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var request=new HttpRequestMessage(new("UNLOCK"),_owner.Address(_path));request.Headers.Add("Lock-Token","<"+Token+">");
                using var response=await _owner.SendAsync(request,cleanup.Token,true).ConfigureAwait(false);
                if(!response.IsSuccessStatusCode&&response.StatusCode!=HttpStatusCode.NotFound)_owner.Warn("DAV 写锁释放未确认，将等待服务端到期。");
            }
            catch(Exception error)when(error is IOException or OperationCanceledException){_owner.Warn("DAV 写锁释放未确认，将等待服务端到期。");}
            finally{_stop.Dispose();}
        }
    }
    /// <summary>用独立临时资源验证锁排他、无令牌写入拒绝、续租和锁创建状态。</summary>
    private async Task VerifyLocksAsync(string parent,CancellationToken token)
    {
        if(_lockVerified)
        {if(WriteCapabilities.ExclusiveLock!=CapabilitySupport.Supported)throw new NotSupportedException("服务器没有通过独占锁验证。");return;}
        await _writeProbe.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(_lockVerified)
            {if(WriteCapabilities.ExclusiveLock!=CapabilitySupport.Supported)throw new NotSupportedException("服务器没有通过独占锁验证。");return;}
            var path=ProbePath(parent);var empty=TrackTemporary(path+"-new");DavLockLease? lease=null;
            try
            {
                using(var create=new HttpRequestMessage(HttpMethod.Put,Address(path)){Content=new ByteArrayContent([17,31])})
                {create.Headers.IfNoneMatch.ParseAdd("*");using var response=await SendAsync(create,token,true).ConfigureAwait(false);Success(response);}
                // 缺失目标的 If-Match:* 必须被拒绝，防止最终更新 LOCK 复活已删除路径。
                var existence=false;
                try{await using var unexpected=await AcquireLockAsync(empty,token,true).ConfigureAwait(false);}
                catch(SourceConflictException){existence=await TryGetMetadataAsync(empty,token).ConfigureAwait(false) is null;}
                if(!existence)throw new NotSupportedException("DAV LOCK 未验证可靠的存在性保护，拒绝弱版本覆盖。");
                lease=await AcquireLockAsync(path,token,true).ConfigureAwait(false);
                if(lease.Status!=200)throw new NotSupportedException("已有 DAV 资源的锁状态不可靠。");
                var exclusive=false;
                try{await using var duplicate=await AcquireLockAsync(path,token).ConfigureAwait(false);}
                catch(SourceConflictException){exclusive=true;}
                if(!exclusive)throw new NotSupportedException("DAV 服务未强制执行独占锁。");
                using(var noToken=new HttpRequestMessage(HttpMethod.Put,Address(path)){Content=new ByteArrayContent([99])})
                {
                    using var response=await SendAsync(noToken,token,true).ConfigureAwait(false);
                    if((int)response.StatusCode is not(423 or 412))throw new NotSupportedException("DAV 服务允许没有锁令牌的写入。");
                }
                await lease.RefreshAsync(token).ConfigureAwait(false);
                var digest=await ReadContentHashAsync(path,lease.Token,token).ConfigureAwait(false);
                if(digest!=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[]{17,31})))throw new NotSupportedException("锁验证期间内容被改变。");
                await lease.DisposeAsync().ConfigureAwait(false);lease=null;
                await using(var created=await AcquireLockAsync(empty,token).ConfigureAwait(false))
                {WriteCapabilities=WriteCapabilities with{LockCreate=created.Status==201?CapabilitySupport.Supported:CapabilitySupport.Unsupported};}
                _lockVerified=true;WriteCapabilities=WriteCapabilities with{ExclusiveLock=CapabilitySupport.Supported};
            }
            catch(NotSupportedException){_lockVerified=true;WriteCapabilities=WriteCapabilities with{ExclusiveLock=CapabilitySupport.Unsupported,LockCreate=CapabilitySupport.Unsupported};throw;}
            finally
            {if(lease is not null)await lease.DisposeAsync().ConfigureAwait(false);await CleanupProbesAsync(path,empty).ConfigureAwait(false);}
        }
        finally{_writeProbe.Release();}
    }
}
