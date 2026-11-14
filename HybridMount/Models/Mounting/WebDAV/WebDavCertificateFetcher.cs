using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>独立无凭据TLS连接，仅捕获叶证书后拒绝握手，绝不发送目标HTTP请求或依赖页面响应。</summary>
internal static class WebDavCertificateFetcher
{
    internal static async Task<WebDavCertificateInfo> FetchCertificateAsync(string address,CancellationToken token,TimeSpan? timeout=null)
    {
        var endpoint=FileSourceFactory.Endpoint(new(FileSourceKind.WebDav,address));
        if(endpoint.Scheme!="https")throw new ArgumentException(global::HybridMount.Services.Localization.UiText.Text("CertificateFetchHttpsRequired"));
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(timeout??TimeSpan.FromSeconds(15));
        var trace=new WebDavConnectionTrace();var connector=new WebDavTcpConnector();WebDavCertificateInfo? captured=null;
        using var handler=new SocketsHttpHandler
        {
            AllowAutoRedirect=false,UseCookies=false,ConnectTimeout=TimeSpan.FromSeconds(15),
            ConnectCallback=(context,cancel)=>connector.ConnectAsync(context.DnsEndPoint,trace,cancel),
            SslOptions=new SslClientAuthenticationOptions
            {
                // 只读获取不等待网络补链；此设置不进入正式WebDAV传输。
                CertificateChainPolicy=new X509ChainPolicy{RevocationMode=X509RevocationMode.NoCheck,DisableCertificateDownloads=true},
                RemoteCertificateValidationCallback=(sender,certificate,chain,errors)=>
                {
                    var expected=sender is SslStream ssl&&string.Equals(ssl.TargetHostName.Trim('[',']'),endpoint.IdnHost.Trim('[',']'),StringComparison.OrdinalIgnoreCase);
                    if(!expected)return errors==SslPolicyErrors.None; // 系统信任的HTTPS代理，不能误填其证书。
                    X509Certificate2? owned=null;
                    try
                    {
                        var leaf=certificate as X509Certificate2??(certificate is null?null:owned=X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()));
                        if(leaf is null)return false;
                        var flags=X509ChainStatusFlags.NoError;if(chain is not null)foreach(var status in chain.ChainStatus)flags|=status.Status;
                        captured=WebDavCertificateInfo.Capture(endpoint,leaf,errors,flags);trace.Record("TLS","CertificateCaptured",trace.TlsStartedAt);
                        return false; // 不完成目标TLS会话，后续HEAD不会发出。
                    }
                    finally{owned?.Dispose();}
                }
            }
        };
        using var client=new HttpClient(handler){Timeout=Timeout.InfiniteTimeSpan};
        using var request=new HttpRequestMessage(HttpMethod.Head,endpoint){Version=HttpVersion.Version11,VersionPolicy=HttpVersionPolicy.RequestVersionExact};
        try{using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false);}
        catch(Exception error)when(error is HttpRequestException or AuthenticationException or OperationCanceledException or IOException)
        {
            token.ThrowIfCancellationRequested();
            if(captured is not null&&!deadline.IsCancellationRequested)return captured;
            var failure=WebDavNetworkDiagnostics.Failure(error,"TLS certificate fetch",timeout:deadline.IsCancellationRequested);
            var diagnostic=failure.Diagnostic with{TransportStages=trace.Snapshot};
            if(trace.Started&&!trace.TcpConnected)diagnostic=diagnostic with{Explanation=diagnostic.Explanation+" "+global::HybridMount.Services.Localization.UiText.Text("WebDavBeforeCertificate")};
            throw new SourceConnectionException(diagnostic);
        }
        deadline.Token.ThrowIfCancellationRequested();
        return captured??throw new IOException(global::HybridMount.Services.Localization.UiText.Text("CertificateFetchUnavailable"));
    }
}
