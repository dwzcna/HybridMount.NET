using HybridMount.Infrastructure.Files.Content;
using HybridMount.Models.Mounting;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using WebDAVClient;
using WebDAVClient.Helpers;
using WebDAVClient.Model;
using WebDAVClient.Protocol;

namespace HybridMount.Infrastructure.Protocols.WebDAV;

public sealed partial class WebDavFileSource
{
    // 实例级截止时间供受控测试缩短；产品默认完整元数据 120 秒、文件流无进展 120 秒。
    internal TimeSpan MetadataDeadline { get; set; }=TimeSpan.FromSeconds(120);
    internal TimeSpan ReadIdleTimeout { get; set; }=TimeSpan.FromSeconds(120);
    /// <summary>实例级回归屏障；仅在退避计时和取消通知均已建立后调用，不接触请求内容。</summary>
    internal Action? ReadRetryWaiting { get; set; }
    /// <summary>退避以普通结果报告停止，避免把正常唤醒建立成异常；计时器和取消注册由本次等待释放。</summary>
    /// <returns>等待结束且令牌仍有效时为 true；主动停止或所有者期限到达时为 false。</returns>
    internal async ValueTask<bool> WaitReadRetryAsync(TimeSpan delay,CancellationToken token)
    {
        if(token.IsCancellationRequested)return false;
        if(delay<=TimeSpan.Zero)return true;
        var wake=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var canceled=token.UnsafeRegister(static state=>((TaskCompletionSource<bool>)state!).TrySetResult(false),wake);
        using var timer=new Timer(static state=>((TaskCompletionSource<bool>)state!).TrySetResult(true),wake,delay,Timeout.InfiniteTimeSpan);
        ReadRetryWaiting?.Invoke();
        return await wake.Task.ConfigureAwait(false)&&!token.IsCancellationRequested;
    }
    private readonly AsyncLocal<WireObservation?> _wire=new();
    private readonly AsyncLocal<bool> _strictRead=new();
    private sealed class WireObservation { public int Status; public bool Identity; public bool Sent; public bool Compressed; public string? ETag; }
    private sealed class DavRangeIgnoredException : Exception { }
    private sealed class DavMalformedRangeException : Exception { }

    /// <summary>认证仅由限定根的传输处理器注入；不允许重定向或把凭据带出配置根。</summary>
    private sealed class DavTransport(WebDavFileSource owner,SourceCredentials credentials,HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        /// <summary>把正常停止映射为已取消任务；不在传输层重新抛出退避取消，真实失败仍原样交给 HTTP 所有者。</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var completion=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _=CompleteAsync(request,token,completion);
            return completion.Task;
        }
        /// <summary>观察所有异步结果，确保内部工作无遗失异常；空值只在内部表示停止，绝不作为 HTTP 成功响应返回。</summary>
        private async Task CompleteAsync(HttpRequestMessage request,CancellationToken token,TaskCompletionSource<HttpResponseMessage> completion)
        {
            try
            {
                var response=await SendCoreAsync(request,token).ConfigureAwait(false);
                if(response is null)completion.TrySetCanceled(token);else completion.TrySetResult(response);
            }
            catch(OperationCanceledException)when(token.IsCancellationRequested){completion.TrySetCanceled(token);}
            catch(Exception error){completion.TrySetException(error);}
        }
        /// <summary>每次发送仍使用关联令牌；只有尚未重发的只读退避可正常结束，已发送写入的未知结果由外层保留。</summary>
        private async Task<HttpResponseMessage?> SendCoreAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(token.IsCancellationRequested)return null;
            owner.HrefPath(request.RequestUri!.AbsoluteUri,owner._endpoint);
            request.Version=HttpVersion.Version11;request.VersionPolicy=HttpVersionPolicy.RequestVersionExact;
            if(credentials.UserName.Length!=0)
                request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName+":"+credentials.Password)));
            if(request.Method.Method is "GET" or "HEAD")
            {
                request.Headers.Remove("Accept-Encoding");request.Headers.TryAddWithoutValidation("Accept-Encoding","identity");
                request.Headers.TryAddWithoutValidation("Want-Repr-Digest","sha-256=10, sha-512=5");
            }
            if(request.Method.Method is "PROPFIND" or "REPORT")
            {request.Headers.Remove("Accept-Encoding");request.Headers.TryAddWithoutValidation("Accept-Encoding","gzip");}
            var readOnly=request.Method.Method is "OPTIONS" or "HEAD" or "GET" or "PROPFIND" or "REPORT";
            // 未知DAV修改方法也必须有明确正文，阻止底层把零响应的无正文请求当作可自动重发。
            if(!readOnly){request.Content??=new ByteArrayContent([]);request.Headers.ExpectContinue=false;}
            var observation=owner._wire.Value;
            var attempts=readOnly&&!owner._strictRead.Value?3:1;var attempt=0;var hops=0;
            var visited=new HashSet<string>(StringComparer.Ordinal){request.RequestUri.AbsoluteUri};
            await using var replay=await WebDavRequestReplay.CreateAsync(request,token).ConfigureAwait(false);
            var current=replay.Request(request.RequestUri);
            try
            {
                while(true)
                {
                    if(token.IsCancellationRequested)return null;
                    if(current.Headers.TryGetValues("Destination",out var targets))
                    {var values=targets.ToArray();if(values.Length==1)WebDavRedirectPolicy.ValidateReference(values[0]);if(values.Length!=1||!Uri.TryCreate(values[0],UriKind.Absolute,out var target))throw WebDavRedirectPolicy.Rejected();WebDavRedirectPolicy.ValidateTarget(owner._endpoint,target);}
                    var network=new NetworkAttempt();current.Options.Set(NetworkAttemptKey,network);
                    if(owner._probing.Value)Volatile.Write(ref owner._lastProbeAttempt,network);
                    var began=System.Diagnostics.Stopwatch.GetTimestamp();if(observation is not null)observation.Sent=true;
                    HttpResponseMessage? owned=null;
                    try
                    {
                        var result=owned=await MountingHttpScheduler.SendAsync(owner._queueBinding,readOnly?MountingPriority.Observation:MountingPriority.Transaction,async cancel=>
                        {
                            var previous=owner._networkAttempt.Value;owner._networkAttempt.Value=network;
                            try{return await base.SendAsync(current,cancel).ConfigureAwait(false);}
                            finally{owner._networkAttempt.Value=previous;}
                        },token).ConfigureAwait(false);
                        result.RequestMessage??=current;
                        network.Trace.Record("HTTP","Response",began,httpStatus:(int)result.StatusCode);
                        if(result.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.PermanentRedirect)
                        {
                            HttpRequestMessage? following=null;
                            try
                            {
                                var target=WebDavRedirectPolicy.Follow(owner._endpoint,current.RequestUri!,result.Headers.TryGetValues("Location",out var locations)?locations:[],visited,hops);
                                following=replay.Request(target,current);
                            }
                            catch{following?.Dispose();throw;}
                            finally{result.Dispose();owned=null;}
                            current.Dispose();await replay.DrainAsync().ConfigureAwait(false);current=following;hops++;continue;
                        }
                        if(current.Method.Method is "PROPFIND" or "REPORT"&&result.Content.Headers.ContentEncoding.Any(value=>!value.Equals("identity",StringComparison.OrdinalIgnoreCase)))
                        {
                            try{result.Content=new WebDavMetadataContent(result.Content);if(observation is not null)observation.Compressed=true;}
                            catch{result.Dispose();throw;}
                        }
                        if(current.Method==HttpMethod.Get&&result.StatusCode==HttpStatusCode.OK)
                        {
                            try{result.Content=new WebDavDigestContent(result,current.RequestUri!);}
                            catch{result.Dispose();throw;}
                        }
                        if(observation is not null)
                        {
                            observation.Status=(int)result.StatusCode;observation.ETag=NormalizeTag(result.Headers.TryGetValues("ETag",out var tags)?tags.SingleOrDefault():null);
                            observation.Identity=result.Content.Headers.ContentEncoding.All(value=>value.Equals("identity",StringComparison.OrdinalIgnoreCase));
                        }
                        if(hops>0&&result.IsSuccessStatusCode)
                        {
                            Volatile.Write(ref owner._lastRedirect,new(request.RequestUri,current.RequestUri!,request.Method.Method,hops));
                            owner._protocolEvidence.Record("dav.redirect",ProtocolCapabilityStatus.Supported,owner.HrefPath(request.RequestUri.AbsoluteUri,owner._endpoint));
                        }
                        owner.ObserveCapabilityHeaders(request,result);if(request.Method==HttpMethod.Options)owner.CaptureOptions(result);
                        owned=null;return result;
                    }
                    catch(WebDavRedirectException){throw;}
                    catch(WebDavMetadataEncodingException){throw;}
                    catch(WebDavDigestException){throw;}
                    catch(Exception error)when(error is HttpRequestException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException or System.Net.Sockets.SocketException)
                    {
                        if(error is OperationCanceledException&&token.IsCancellationRequested&&network.Certificate==System.Net.Security.SslPolicyErrors.None)return null;
                        var failure=error as SourceConnectionException??owner.NetworkFailure(error,request.Method.Method+" 响应头／连接",network,error is OperationCanceledException,attempt+1);
                        if(!failure.Diagnostic.Transient||failure.Diagnostic.Kind==ConnectionFailureKind.Certificate||++attempt>=attempts)throw failure;
                    }
                    finally{owned?.Dispose();}
                    var next=replay.Request(current.RequestUri!,current);current.Dispose();await replay.DrainAsync().ConfigureAwait(false);current=next;
                    var delay=TimeSpan.FromMilliseconds(Math.Min(2000,Math.Max(0,owner.ReadRetryDelay.TotalMilliseconds)*(1<<(attempt-1))));
                    if(!await owner.WaitReadRetryAsync(delay,token).ConfigureAwait(false))return null;
                }
            }
            finally{current.Dispose();await replay.DrainAsync().ConfigureAwait(false);}
        }
    }

    private DavPath LibraryPath(Uri uri)
    {
        var path=HrefPath(uri.AbsoluteUri,_endpoint);
        var escaped=string.Join('/',path.Split('/',StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return escaped.Length==0?DavPath.Root:DavPath.Parse(escaped+(uri.AbsolutePath.EndsWith('/')?"/":""));
    }
    private static string? Header(HttpRequestMessage request,string name)=>request.Headers.TryGetValues(name,out var values)?values.Single():null;
    private static string LockValue(HttpRequestMessage request,string name)
    {
        var text=Header(request,name)??throw new IOException("缺少 DAV 锁条件。");
        var begin=text.IndexOf('<');var end=text.IndexOf('>');
        if(begin<0||end<=begin)throw new IOException("DAV 锁条件格式无效。");
        return text[(begin+1)..end];
    }
    private static DavIfConditions? Conditions(HttpRequestMessage request)=>Header(request,"If") is null?null:
        DavIfConditions.Untagged(new DavConditionList(DavCondition.StateToken(LockValue(request,"If"))));

    /// <summary>应用策略请求转换成类型化 DavClient API；所有实际 HTTP 均由库执行，不保留旁路。</summary>
    private async Task<HttpResponseMessage> ExecuteDavAsync(HttpRequestMessage request,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(MetadataDeadline);
        var cancel=deadline.Token;var path=LibraryPath(request.RequestUri!);
        var common=new DavRequestOptions{IfMatch=Header(request,"If-Match"),IfNoneMatch=Header(request,"If-None-Match"),IfUnmodifiedSince=request.Headers.IfUnmodifiedSince,Conditions=Conditions(request)};
        var observation=new WireObservation();var previous=_wire.Value;_wire.Value=observation;
        try
        {
            DavResponse result;XElement? document=null;DavDownloadResult? download=null;
            switch(request.Method.Method)
            {
                case "HEAD": result=await _dav.HeadAsync(path,common,cancel).ConfigureAwait(false);break;
                case "OPTIONS": result=await _dav.GetCapabilitiesAsync(path,common,cancel).ConfigureAwait(false);break;
                case "PROPFIND":
                    var prop=await _dav.PropFindAsync(path,new PropFindOptions{Depth=Header(request,"Depth")=="1"?DavDepth.One:DavDepth.Zero,
                        Selection=DavPropertySelection.Selected,Properties=MetadataProperties},cancel).ConfigureAwait(false);
                    result=prop;document=prop.Document?.ToElement();break;
                case "GET":
                    try { download=await _dav.DownloadAsync(path,new DownloadOptions{Range=request.Headers.Range,IfMatch=common.IfMatch,Conditions=common.Conditions,RequireIdentityEncoding=true},cancel).ConfigureAwait(false); }
                    catch(WebDAVException)when(request.Headers.Range is not null&&observation.Status==200&&observation.Identity)
                    {
                        if(common.IfMatch!=observation.ETag)throw new SourceConflictException("忽略 Range 的响应版本已变化，未重试完整读取。");
                        throw new DavRangeIgnoredException();
                    }
                    catch(WebDAVException)when(request.Headers.Range is not null&&observation.Status==206){throw new DavMalformedRangeException();}
                    result=download;break;
                case "PUT":
                {
                    deadline.CancelAfter(ReadIdleTimeout);
                    using var content=new DavUploadBody(await request.Content!.ReadAsStreamAsync(cancel).ConfigureAwait(false),()=>deadline.CancelAfter(ReadIdleTimeout));
                    result=await _dav.UploadAsync(path,content,new UploadOptions{IfMatch=common.IfMatch,IfNoneMatch=common.IfNoneMatch,Conditions=common.Conditions},cancel).ConfigureAwait(false);break;
                }
                case "MKCOL": result=await _dav.CreateCollectionAsync(path,common,cancel).ConfigureAwait(false);break;
                case "DELETE": result=await _dav.DeleteAsync(path,common,cancel).ConfigureAwait(false);break;
                case "MOVE":
                    var destination=LibraryPath(new Uri(Header(request,"Destination")!));
                    result=await _dav.MoveAsync(path,destination,new MoveOptions{Overwrite=Header(request,"Overwrite")=="T",IfMatch=common.IfMatch,IfNoneMatch=common.IfNoneMatch,Conditions=common.Conditions},cancel).ConfigureAwait(false);break;
                case "LOCK":
                    var locked=request.Content is null?
                        await _dav.RefreshLockAsync(path,LockValue(request,"If"),[DavLockTimeout.Finite(TimeSpan.FromMinutes(5))],cancel).ConfigureAwait(false):
                        await _dav.AcquireLockAsync(path,new LockOptions{IfMatch=common.IfMatch,IfNoneMatch=common.IfNoneMatch,Scope=DavLockScope.Exclusive,Depth=DavDepth.Zero,
                            Timeouts=[DavLockTimeout.Finite(TimeSpan.FromMinutes(5))],Owner=new DavXmlValue(new XElement(Dav+"owner",new XElement(Dav+"href","urn:hybridmount:upload")))},cancel).ConfigureAwait(false);
                    result=locked;
                    if(locked.StatusCode is 200 or 201)
                    {
                        locked.EnsureSucceeded();
                        document=new XElement(Dav+"prop",new XElement(Dav+"lockdiscovery",locked.Locks.Select(value=>new XElement(Dav+"activelock",
                            new XElement(Dav+"lockscope",new XElement(XName.Get(value.Scope.LocalName,value.Scope.Namespace))),
                            new XElement(Dav+"locktype",new XElement(XName.Get(value.Type.LocalName,value.Type.Namespace))),
                            new XElement(Dav+"depth",value.Depth),new XElement(Dav+"timeout",value.Timeout.IsInfinite?"Infinite":"Second-"+(int)(value.Timeout.Duration?.TotalSeconds??0)),
                            new XElement(Dav+"locktoken",new XElement(Dav+"href",value.Token))))));
                    }
                    break;
                case "UNLOCK": result=await _dav.UnlockAsync(path,LockValue(request,"Lock-Token"),cancel).ConfigureAwait(false);break;
                default: throw new NotSupportedException("未迁移的 DAV 方法。");
            }
            if(result.StatusCode==507)throw StorageRejected(result);
            if(result is DavMultiStatusResult multi&&multi.StatusCode==207&&request.Method.Method!="PROPFIND")
            {
                var failed=multi.Resources.SelectMany(resource=>resource.Hrefs.Select(href=>new MountItemResult(HrefPath(href,request.RequestUri!),
                    resource.StatusCode is >=400||resource.PropertyStatuses.Any(x=>x.StatusCode is null or <200 or >=300)?"Failed":"Completed","DAV 多状态结果"))).ToArray();
                if(failed.Length==0||failed.Any(x=>x.Status!="Completed"))
                    throw new SourcePartialMoveException(failed,"WebDAV");
                multi.EnsureSucceeded();
            }
            if(observation.Compressed)request.Options.Set(MetadataCompressedKey,true);
            var response=new HttpResponseMessage((HttpStatusCode)result.StatusCode){RequestMessage=new HttpRequestMessage(request.Method,result.EffectiveUri)};
            response.Content=download is not null?new StreamContent(new DavBodyStream(download,ReadIdleTimeout)):
                new StringContent(document?.ToString(SaveOptions.DisableFormatting)??"",Encoding.UTF8,"application/xml");
            foreach(var header in result.Headers)
                if(!response.Headers.TryAddWithoutValidation(header.Key,header.Value))
                {response.Content.Headers.Remove(header.Key);response.Content.Headers.TryAddWithoutValidation(header.Key,header.Value);}
            return response;
        }
        catch(WebDAVException)when(observation.Status==507&&request.Method.Method is "PUT" or "MKCOL" or "MOVE" or "DELETE" or "LOCK" or "UNLOCK")
        {throw StorageRejected();}
        finally{_wire.Value=previous;}
    }
    private static readonly HttpRequestOptionsKey<bool> MetadataCompressedKey=new("HybridMount.WebDav.MetadataCompressed");
    private static readonly PropertyName[] MetadataProperties=[new("DAV:","resourcetype"),new("DAV:","getcontentlength"),new("DAV:","getlastmodified"),new("DAV:","getetag"),new("DAV:","current-user-privilege-set")];

    /// <summary>输入读取续期无进展计时器；只借用流，退出仍由原上传所有者释放快照。</summary>
    private sealed class DavUploadBody(Stream inner,Action progressed) : Stream
    {
        public override bool CanRead=>inner.CanRead;public override bool CanSeek=>inner.CanSeek;public override bool CanWrite=>false;
        public override long Length=>inner.Length;public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override int Read(byte[] buffer,int offset,int count){var n=inner.Read(buffer,offset,count);if(n>0)progressed();return n;}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default){var n=await inner.ReadAsync(buffer,token).ConfigureAwait(false);if(n>0)progressed();return n;}
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
        public override long Seek(long offset,SeekOrigin origin)=>inner.Seek(offset,origin);public override void Flush(){}
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    /// <summary>每次正文读取有独立无进展截止；释放同时关闭库响应，取消只影响所属共享工作。</summary>
    private sealed class DavBodyStream(DavDownloadResult owner,TimeSpan idle) : Stream
    {
        private readonly Stream _inner=owner.Content??Stream.Null;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {
            using var limit=CancellationTokenSource.CreateLinkedTokenSource(token);limit.CancelAfter(idle);
            try{return await _inner.ReadAsync(buffer,limit.Token).ConfigureAwait(false);}
            catch(OperationCanceledException error)when(!token.IsCancellationRequested){throw WebDavNetworkDiagnostics.Failure(error,"GET 正文",timeout:true);}
            catch(WebDavDigestException){throw;}
            catch(Exception error)when(error is HttpRequestException or IOException){throw WebDavNetworkDiagnostics.Failure(error,"GET 正文");}
        }
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
        public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing){if(disposing)owner.Dispose();base.Dispose(disposing);}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
