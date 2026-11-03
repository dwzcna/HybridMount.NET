using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>只从结构化异常、Socket 码和证书策略结果分类；不读取消息、URL、响应或凭据。</summary>
internal static class WebDavNetworkDiagnostics
{
    public static SourceConnectionException Failure(Exception error,string stage,SslPolicyErrors certificate=SslPolicyErrors.None,
        X509ChainStatusFlags chain=X509ChainStatusFlags.NoError,bool timeout=false,int attempts=1)
    {
        var errors=new List<Exception>();for(var item=error;item is not null&&errors.Count<8;item=item.InnerException)errors.Add(item);
        var http=errors.OfType<HttpRequestException>().FirstOrDefault();var socket=errors.OfType<SocketException>().FirstOrDefault();
        var tls=http?.HttpRequestError==HttpRequestError.SecureConnectionError||errors.Any(x=>x is AuthenticationException);
        var reset=socket?.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
        var kind=certificate!=SslPolicyErrors.None?ConnectionFailureKind.Certificate:timeout?ConnectionFailureKind.Timeout:tls?ConnectionFailureKind.Tls:ConnectionFailureKind.Network;
        var transient=certificate==SslPolicyErrors.None&&(timeout||tls&&reset||!tls&&
            (socket?.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.ConnectionRefused or SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.TimedOut or SocketError.TryAgain||
             socket is null&&http?.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded));
        var explanation=kind switch
        {
            ConnectionFailureKind.Certificate=>"服务器证书验证失败，请检查信任链、服务器域名及有效期；修正后重新连接。",
            ConnectionFailureKind.Timeout=>stage.Contains("正文",StringComparison.Ordinal)?"WebDAV 文件正文无进展超时，请检查网络及服务响应。":"WebDAV 请求超时，请检查网络及服务响应。",
            ConnectionFailureKind.Tls when reset=>"TLS 握手时远端重置了连接，请检查反向代理、TLS 配置和网络；短暂故障可重试。",
            ConnectionFailureKind.Tls=>"TLS 握手未完成，请检查服务 TLS 配置及证书；现有证据不足以确定具体原因。",
            _ when http?.HttpRequestError==HttpRequestError.NameResolutionError=>"无法解析服务器名称，请检查地址和 DNS。",
            _=>"WebDAV 网络连接失败，请检查服务是否可达及网络状态。"
        };
        return new(new(stage,kind,null,null,null,null,explanation,http?.HttpRequestError.ToString(),socket?.SocketErrorCode.ToString(),
            socket?.NativeErrorCode??errors.OfType<System.ComponentModel.Win32Exception>().FirstOrDefault()?.NativeErrorCode,certificate==SslPolicyErrors.None?null:certificate+(chain==X509ChainStatusFlags.NoError?"":" / "+chain),
            error.GetType().Name,error.HResult,transient,attempts,string.Join(" > ",errors.Select(x=>x.GetType().Name+"/0x"+x.HResult.ToString("X8")))));
    }
}

public sealed partial class WebDavFileSource
{
    private sealed class NetworkAttempt
    {public SslPolicyErrors Certificate;public X509ChainStatusFlags Chain;}
    private static readonly HttpRequestOptionsKey<NetworkAttempt> NetworkAttemptKey=new("HybridMount.WebDav.NetworkAttempt");
    internal TimeSpan ReadRetryDelay {get;set;}=TimeSpan.FromMilliseconds(200);
    /// <summary>使用请求关联的证书回调仅记录安全枚举；严格沿用平台链与域名验证，任一策略错误均拒绝。</summary>
    private static HttpMessageHandler CreateHttpHandler()=>new HttpClientHandler
    {
        AllowAutoRedirect=false,UseCookies=false,MaxConnectionsPerServer=4,AutomaticDecompression=System.Net.DecompressionMethods.None,
        ServerCertificateCustomValidationCallback=(request,certificate,chain,errors)=>
        {
            if(request.Options.TryGetValue(NetworkAttemptKey,out var attempt))
            {
                attempt.Certificate=errors;
                if(chain is not null)foreach(var status in chain.ChainStatus)attempt.Chain|=status.Status;
            }
            return errors==SslPolicyErrors.None;
        }
    };
}
