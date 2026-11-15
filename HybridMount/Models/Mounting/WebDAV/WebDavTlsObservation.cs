using System.Net.Security;
using System.Security.Authentication;

namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>只接受已完成且属于HTTPS目标的握手；代理外层TLS、证书捕获和未认证流均不产生成功证据。</summary>
internal static class WebDavTlsObservation
{
    /// <summary>借用传输流，不发请求或改变所有权；仅在完整握手后读取SslProtocol。</summary>
    internal static ProtocolTransportEvidence? Observe(Uri endpoint, Stream stream)
    {
        if (endpoint.Scheme != "https" || stream is not SslStream { IsAuthenticated: true, IsEncrypted: true } tls ||
            !string.Equals(tls.TargetHostName.Trim('[', ']'), endpoint.IdnHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            return null;
        var version = tls.SslProtocol switch
        {
            SslProtocols.Tls13 => ProtocolTransportVersion.Tls13,
            SslProtocols.Tls12 => ProtocolTransportVersion.Tls12,
            _ => ProtocolTransportVersion.OtherTls
        };
        return new(version, DateTimeOffset.UtcNow);
    }
}
