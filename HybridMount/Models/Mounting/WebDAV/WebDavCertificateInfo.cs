using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>异常证书的只读公共信息；只保存叶证书摘要，不保存私钥、原始证书、请求或认证材料。</summary>
public sealed record WebDavCertificateInfo(string Server, string Subject, string Issuer, DateTimeOffset NotBeforeUtc,
    DateTimeOffset NotAfterUtc, string Sha256, SslPolicyErrors PolicyErrors, X509ChainStatusFlags ChainErrors)
{
    /// <summary>在TLS回调中立即复制证书信息；返回后不再借用系统证书或链对象。</summary>
    internal static WebDavCertificateInfo Capture(Uri expected, X509Certificate2 certificate, SslPolicyErrors errors, X509ChainStatusFlags chain)
        => new(expected.GetLeftPart(UriPartial.Authority), Safe(certificate.Subject), Safe(certificate.Issuer),
            certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(),
            Convert.ToHexString(SHA256.HashData(certificate.RawData)), errors, chain);
    private static string Safe(string value) => new(value.Where(c => !char.IsControl(c)).Take(2048).ToArray());
}

/// <summary>UI线程拥有一次证书确认；任何输入变化推进代次，修改后改回也不能确认旧测试的证书。</summary>
public sealed class WebDavCertificateTrustSession
{
    private long _revision, _pendingRevision;
    private string? _identity, _pending;
    public WebDavCertificateInfo? Candidate { get; private set; }
    public void Invalidate() { _revision++; Candidate = null; _identity = null; }
    public void Begin(string identity) { Candidate = null; _pending = identity; _pendingRevision = _revision; }
    public void Offer(string identity, WebDavCertificateInfo? certificate)
    {
        if (_pending is null || identity != _pending || _pendingRevision != _revision) return;
        _pending = null; Candidate = certificate; _identity = identity;
    }
    /// <summary>只有已展示、身份未变化且对应预期HTTPS服务器的候选可由用户确认；失败不返回指纹。</summary>
    public string? Confirm(string identity, Uri expected)
    {
        if (Candidate is not { } candidate || _identity != identity || _pendingRevision != _revision ||
            expected.Scheme != "https" || candidate.Server != expected.GetLeftPart(UriPartial.Authority)) return null;
        var pin = candidate.Sha256; Invalidate(); return pin;
    }
}
