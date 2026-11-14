using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>只从结构化异常、Socket 码和证书策略结果分类；不读取消息、URL、响应或凭据。</summary>
internal static class WebDavNetworkDiagnostics
{
    public static SourceConnectionException Failure(Exception error,string stage,SslPolicyErrors certificate=SslPolicyErrors.None,
        X509ChainStatusFlags chain=X509ChainStatusFlags.NoError,bool timeout=false,int attempts=1)
    {
        var errors=new List<Exception>();for(var item=error;item is not null&&errors.Count<8;item=item.InnerException)errors.Add(item);
        var http=errors.OfType<HttpRequestException>().FirstOrDefault();var socket=errors.OfType<SocketException>().FirstOrDefault();
        var transport=http?.HttpRequestError??errors.OfType<HttpIOException>().FirstOrDefault()?.HttpRequestError;
        var tls=transport==HttpRequestError.SecureConnectionError||errors.Any(x=>x is AuthenticationException);
        var reset=socket?.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
        var kind=certificate!=SslPolicyErrors.None?ConnectionFailureKind.Certificate:timeout?ConnectionFailureKind.Timeout:tls?ConnectionFailureKind.Tls:ConnectionFailureKind.Network;
        var transient=certificate==SslPolicyErrors.None&&(timeout||tls&&reset||!tls&&
            (socket?.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.ConnectionRefused or SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.TimedOut or SocketError.TryAgain||
             socket is null&&transport is HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded));
        var explanation=kind switch
        {
            ConnectionFailureKind.Certificate when (chain & X509ChainStatusFlags.NotTimeValid) != 0 => "服务器证书已过期或尚未生效，请核对系统时间及证书有效期；可在连接设置核验证书详情。",
            ConnectionFailureKind.Certificate when (certificate & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 => "服务器证书名称与地址不匹配，请在连接设置核验预期服务器及证书详情。",
            ConnectionFailureKind.Certificate=>"服务器证书验证失败，请在连接设置核验信任链、名称、有效期及证书指纹。",
            ConnectionFailureKind.Timeout=>stage.Contains("正文",StringComparison.Ordinal)?"WebDAV 文件正文无进展超时，请检查网络及服务响应。":"WebDAV 请求超时，请检查网络及服务响应。",
            ConnectionFailureKind.Tls when reset=>"TLS 握手时远端重置了连接，请检查反向代理、TLS 配置和网络；短暂故障可重试。",
            ConnectionFailureKind.Tls=>"TLS 握手未完成，请检查服务 TLS 配置及证书；现有证据不足以确定具体原因。",
            _ when transport==HttpRequestError.NameResolutionError=>"无法解析服务器名称，请检查地址和 DNS。",
            _=>"WebDAV 网络连接失败，请检查服务是否可达及网络状态。"
        };
        return new(new(stage,kind,null,null,null,null,explanation,transport?.ToString(),socket?.SocketErrorCode.ToString(),
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
    /// <summary>规范化当前挂载的 SHA-256 指纹；空值沿用系统信任，格式错误或 HTTP 地址在连接前失败。</summary>
    internal static string? NormalizeCertificatePin(FileSourceConfiguration options)
    {
        if(string.IsNullOrWhiteSpace(options.PinnedCertificateSha256))return null;
        var pin=options.PinnedCertificateSha256.Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();
        if(pin.Length!=64||pin.Any(c=>!Uri.IsHexDigit(c)))throw new ArgumentException("请输入服务器证书的 SHA-256 指纹（64 位十六进制）。");
        if(FileSourceFactory.Endpoint(options).Scheme!="https")throw new ArgumentException("证书指纹仅适用于 HTTPS WebDAV 地址。");
        return pin;
    }
    private WebDavCertificateInfo? _rejectedCertificate;
    internal WebDavCertificateInfo? RejectedCertificate => Volatile.Read(ref _rejectedCertificate);
    /// <summary>系统验证成功正常接受；系统失败时只允许此挂载明确确认的叶证书SHA256。</summary>
    internal static bool AcceptCertificate(X509Certificate2? certificate,SslPolicyErrors errors,string? pin)
        =>certificate is not null&&(errors==SslPolicyErrors.None || pin is not null &&
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.RawData))==pin);
    /// <summary>信任仅属于此客户端；禁止重定向及跨挂载共享，不修改系统证书存储。</summary>
    private HttpMessageHandler CreateHttpHandler(string? pin)=>new HttpClientHandler
    {
        AllowAutoRedirect=false,UseCookies=false,MaxConnectionsPerServer=4,AutomaticDecompression=System.Net.DecompressionMethods.None,
        ServerCertificateCustomValidationCallback=(request,certificate,chain,errors)=>
        {
            var expected=request.RequestUri is { Scheme: "https" } address && address.Authority == _endpoint.Authority;
            var accepted=expected && AcceptCertificate(certificate,errors,pin);
            var flags=X509ChainStatusFlags.NoError;
            if(chain is not null)foreach(var status in chain.ChainStatus)flags |= status.Status;
            if(!accepted && expected && certificate is not null)
                Volatile.Write(ref _rejectedCertificate, WebDavCertificateInfo.Capture(_endpoint,certificate,errors,flags));
            if(request.Options.TryGetValue(NetworkAttemptKey,out var attempt))
            {
                // 只保留结构化拒绝原因；已收到证书拒绝时不以超时覆盖，且不自动重试。
                attempt.Certificate=accepted?SslPolicyErrors.None:errors==SslPolicyErrors.None?SslPolicyErrors.RemoteCertificateChainErrors:errors;
                if(!accepted)attempt.Chain=flags;
            }
            return accepted;
        }
    };
}
