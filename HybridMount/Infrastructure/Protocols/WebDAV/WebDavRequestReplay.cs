using System.Net;

namespace HybridMount.Infrastructure.Protocols.WebDAV;

/// <summary>整条链共用只读正文快照；XML有界缓冲，PUT借用不可变可寻址快照，响应提前返回时先排空旧正文才重放。</summary>
internal sealed class WebDavRequestReplay:IAsyncDisposable
{
    private readonly HttpRequestMessage _original;private readonly byte[]? _xml;private readonly Stream? _body;private readonly long _start;
    private readonly SemaphoreSlim _bodyGate=new(1,1);private readonly object _sync=new();private readonly List<ReplayContent> _contents=[];
    private TaskCompletionSource _idle=new(TaskCreationOptions.RunContinuationsAsynchronously);private int _active;private bool _disposed;
    private WebDavRequestReplay(HttpRequestMessage request,byte[]? xml,Stream? body)
    {_original=request;_xml=xml;_body=body;_start=body?.Position??0;_idle.TrySetResult();}
    internal static async Task<WebDavRequestReplay> CreateAsync(HttpRequestMessage request,CancellationToken token)
    {
        if(request.Content is null)return new(request,null,null);
        var body=await request.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        if(request.Method==HttpMethod.Put)
        {
            if(!body.CanSeek)throw new IOException("WebDAV上传缺少可重放的不可变快照。");
            return new(request,null,body);
        }
        using var bytes=new MemoryStream();var buffer=new byte[8192];
        while(true)
        {
            var count=await body.ReadAsync(buffer,token).ConfigureAwait(false);if(count==0)break;
            if(bytes.Length+count>1048576)throw new IOException("WebDAV请求XML超过重定向缓冲限制。");
            bytes.Write(buffer,0,count);
        }
        return new(request,bytes.ToArray(),null);
    }
    internal HttpRequestMessage Request(Uri uri,HttpRequestMessage? previous=null)
    {
        var source=previous??_original;var result=new HttpRequestMessage(source.Method,uri){Version=source.Version,VersionPolicy=source.VersionPolicy};
        try
        {
            Uri? destination=null;
            if(source.Headers.TryGetValues("Destination",out var values))destination=new Uri(values.Single());
            foreach(var field in source.Headers)
            {
                var value=field.Key.Equals("If",StringComparison.OrdinalIgnoreCase)&&source.RequestUri!=uri?
                    field.Value.Select(item=>WebDavRedirectPolicy.RebindIf(item,source.RequestUri!,uri,destination)):field.Value;
                result.Headers.TryAddWithoutValidation(field.Key,value);
            }
            foreach(var option in source.Options)result.Options.Set(new HttpRequestOptionsKey<object?>(option.Key),option.Value);
            if(_original.Content is not null)
            {
                var content=new ReplayContent(this);
                lock(_sync){ObjectDisposedException.ThrowIf(_disposed,this);_contents.Add(content);}
                foreach(var field in _original.Content.Headers)if(!field.Key.Equals("Content-Length",StringComparison.OrdinalIgnoreCase))content.Headers.TryAddWithoutValidation(field.Key,field.Value);
                result.Content=content;
            }
            return result;
        }
        catch{result.Dispose();throw;}
    }
    internal Task DrainAsync(){lock(_sync)return _idle.Task;}
    public async ValueTask DisposeAsync()
    {
        Task idle;lock(_sync){if(_disposed)return;_disposed=true;foreach(var content in _contents)content.Retired=true;idle=_idle.Task;}
        await idle.ConfigureAwait(false);_bodyGate.Dispose();
    }
    private sealed class ReplayContent(WebDavRequestReplay owner):HttpContent
    {
        internal bool Retired;
        protected override bool TryComputeLength(out long length){length=owner._xml?.LongLength??(owner._body is null?0:owner._body.Length-owner._start);return true;}
        protected override Task SerializeToStreamAsync(Stream target,TransportContext? context)=>SerializeToStreamAsync(target,context,default);
        protected override async Task SerializeToStreamAsync(Stream target,TransportContext? context,CancellationToken token)
        {
            lock(owner._sync)
            {
                if(Retired||owner._disposed)throw new OperationCanceledException(token);
                if(owner._active++==0)owner._idle=new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var held=false;
            try
            {
                await owner._bodyGate.WaitAsync(token).ConfigureAwait(false);held=true;
                lock(owner._sync)if(Retired)throw new OperationCanceledException(token);
                if(owner._xml is {} xml)await target.WriteAsync(xml,token).ConfigureAwait(false);
                else if(owner._body is {} body){body.Position=owner._start;await body.CopyToAsync(target,65536,token).ConfigureAwait(false);}
            }
            finally
            {
                if(held)owner._bodyGate.Release();
                lock(owner._sync)if(--owner._active==0)owner._idle.TrySetResult();
            }
        }
        protected override void Dispose(bool disposing){if(disposing)lock(owner._sync)Retired=true;base.Dispose(disposing);}
    }
}

/// <summary>仅不可寻址的兼容调用先落本地受控快照；正文不整份进入内存，取消／失败／退出由句柄删除，不关闭调用者流。</summary>
internal sealed class WebDavUploadReplay(Stream content,FileStream? owned):IAsyncDisposable
{
    internal Stream Content=>content;
    internal static async Task<WebDavUploadReplay> OpenAsync(Stream content,string directory,CancellationToken token)
    {
        if(content.CanSeek)return new(content,null);
        var area=Path.Combine(directory,"dav-replay");Directory.CreateDirectory(area);
        var file=new FileStream(Path.Combine(area,Guid.NewGuid().ToString("N")+".tmp"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.DeleteOnClose);
        try{await content.CopyToAsync(file,65536,token).ConfigureAwait(false);await file.FlushAsync(token).ConfigureAwait(false);file.Position=0;return new(file,file);}
        catch{await file.DisposeAsync().ConfigureAwait(false);throw;}
    }
    public ValueTask DisposeAsync()=>owned?.DisposeAsync()??ValueTask.CompletedTask;
}
