namespace HybridMount.Models.Mounting.Synology;

/// <summary>只读探测结果；会话只在当前应用内一次性交接，不序列化或显示认证材料。</summary>
public sealed record SynologyProbeResult(bool Success,string Message,IReadOnlyList<SynologySharedFolder> Shares,
    [property:System.Text.Json.Serialization.JsonIgnore] SourceCredentials? Credentials=null,string? CertificateSha256=null,int? AuthenticationCode=null):IAsyncDisposable
{
    internal SynologySessionTicket? Session {get;init;}
    public override string ToString()=>"群晖只读连接测试结果";
    /// <summary>关闭表单或退出应用时归还尚未交接的登录会话；重复关闭幂等。</summary>
    public ValueTask DisposeAsync()=>Session?.DisposeAsync()??ValueTask.CompletedTask;
}
/// <summary>五分钟、单所有者的内存会话票据；只允许相同服务器、证书策略和账号凭据接收。</summary>
internal sealed class SynologySessionTicket:IAsyncDisposable
{
    private static readonly AsyncLocal<SynologySessionTicket?> Ambient=new();
    private readonly object _gate=new();
    private SynologyFileSource? _owner;
    private readonly Timer _expiry;
    private Task? _close;
    internal bool IsReleased {get{lock(_gate)return _owner is null&&(_close?.IsCompleted??true);}}
    internal SynologySessionTicket(SynologyFileSource owner,TimeSpan? lifetime=null)
    {_owner=owner;_expiry=new(_=>Discard(),null,lifetime??TimeSpan.FromMinutes(5),Timeout.InfiniteTimeSpan);}
    internal static IDisposable Use(SynologySessionTicket? ticket)
    {var before=Ambient.Value;Ambient.Value=ticket;return new Scope(before);}
    private sealed class Scope(SynologySessionTicket? before):IDisposable
    {public void Dispose()=>Ambient.Value=before;}
    internal static Task AdoptCurrentAsync(SynologyFileSource source)=>Ambient.Value?.ApplyAsync(source)??Task.CompletedTask;
    internal async Task ApplyAsync(SynologyFileSource destination)
    {
        SynologyFileSource? owner;
        lock(_gate)
        {
            if(_owner is null||!destination.CanReceiveSession(_owner))return;
            owner=_owner;_owner=null;_expiry.Dispose();
        }
        try{destination.ReceiveSession(owner);}finally{await owner.DisposeAsync().ConfigureAwait(false);}
    }
    internal void Discard()
    {
        var work=DisposeAsync().AsTask();
        _=work.ContinueWith(done=>{_ = done.Exception;},CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    public ValueTask DisposeAsync()
    {
        lock(_gate)
        {
            if(_close is not null)return new(_close);
            var owner=_owner;_owner=null;_expiry.Dispose();return new(_close=owner?.DisposeAsync().AsTask()??Task.CompletedTask);
        }
    }
}
public sealed partial class SynologyFileSource
{
    internal bool CanReceiveSession(SynologyFileSource owner)=>_endpoint==owner._endpoint&&_options.AllowHttp==owner._options.AllowHttp&&
        NormalizeFingerprint(_options.PinnedCertificateSha256)==NormalizeFingerprint(owner._options.PinnedCertificateSha256)&&
        _credentials.UserName==owner._credentials.UserName&&_credentials.Password==owner._credentials.Password;
    internal void ReceiveSession(SynologyFileSource owner)
    {
        lock(owner._authenticationSync)
        {
            if(owner._authenticationFailure is not null||owner._sid is null)return;
            foreach(var api in owner._apis)_apis.Add(api.Key,api.Value);
            _sid=owner._sid;_synoToken=owner._synoToken;_credentials=owner._credentials;_otpCode=null;
            owner._sid=null;owner._synoToken=null;
        }
    }
}
