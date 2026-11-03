using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using WebDAVClient;
using WebDAVClient.Helpers;
using WebDAVClient.Model;
using WebDAVClient.Protocol;

namespace HybridMount.Services.Mounting;

public sealed partial class WebDavFileSource
{
    // 实例级截止时间供受控测试缩短；产品默认完整元数据 120 秒、文件流无进展 120 秒。
    internal TimeSpan MetadataDeadline { get; set; }=TimeSpan.FromSeconds(120);
    internal TimeSpan ReadIdleTimeout { get; set; }=TimeSpan.FromSeconds(120);
    private readonly AsyncLocal<WireObservation?> _wire=new();
    private sealed class WireObservation { public int Status; public bool Identity; public bool Sent; public string? ETag; }
    private sealed class DavRangeIgnoredException : Exception { }
    private sealed class DavMalformedRangeException : Exception { }

    /// <summary>认证仅由限定根的传输处理器注入；不允许重定向或把凭据带出配置根。</summary>
    private sealed class DavTransport(WebDavFileSource owner,SourceCredentials credentials,HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            owner.HrefPath(request.RequestUri!.AbsoluteUri,owner._endpoint);
            if(credentials.UserName.Length!=0)
                request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName+":"+credentials.Password)));
            var observation=owner._wire.Value;if(observation is not null)observation.Sent=true;
            var result=await base.SendAsync(request,token).ConfigureAwait(false);
            if(observation is not null){observation.Status=(int)result.StatusCode;observation.ETag=NormalizeTag(result.Headers.TryGetValues("ETag",out var tags)?tags.SingleOrDefault():null);observation.Identity=result.Content.Headers.ContentEncoding.All(x=>x.Equals("identity",StringComparison.OrdinalIgnoreCase));}
            return result;
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
        var common=new DavRequestOptions{IfMatch=Header(request,"If-Match"),IfNoneMatch=Header(request,"If-None-Match"),Conditions=Conditions(request)};
        var observation=new WireObservation();var previous=_wire.Value;_wire.Value=observation;
        try
        {
            DavResponse result;XElement? document=null;DavDownloadResult? download=null;
            switch(request.Method.Method)
            {
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
            if(result is DavMultiStatusResult multi&&multi.StatusCode==207&&request.Method.Method!="PROPFIND")
            {
                var failed=multi.Resources.SelectMany(resource=>resource.Hrefs.Select(href=>new MountItemResult(HrefPath(href,request.RequestUri!),
                    resource.StatusCode is >=400||resource.PropertyStatuses.Any(x=>x.StatusCode is null or <200 or >=300)?"Failed":"Completed","DAV 多状态结果"))).ToArray();
                if(failed.Length==0||failed.Any(x=>x.Status!="Completed"))
                    throw new SourcePartialMoveException(failed,"WebDAV");
                multi.EnsureSucceeded();
            }
            var response=new HttpResponseMessage((HttpStatusCode)result.StatusCode);
            response.Content=download is not null?new StreamContent(new DavBodyStream(download,ReadIdleTimeout)):
                new StringContent(document?.ToString(SaveOptions.DisableFormatting)??"",Encoding.UTF8,"application/xml");
            foreach(var header in result.Headers)
                if(!response.Headers.TryAddWithoutValidation(header.Key,header.Value))
                {response.Content.Headers.Remove(header.Key);response.Content.Headers.TryAddWithoutValidation(header.Key,header.Value);}
            return response;
        }
        finally{_wire.Value=previous;}
    }
    private static readonly PropertyName[] MetadataProperties=[new("DAV:","resourcetype"),new("DAV:","getcontentlength"),new("DAV:","getlastmodified"),new("DAV:","getetag")];

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
            catch(OperationCanceledException)when(!token.IsCancellationRequested){throw new IOException("WebDAV 文件正文无进展超时。");}
        }
        public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
        public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing){if(disposing)owner.Dispose();base.Dispose(disposing);}
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
