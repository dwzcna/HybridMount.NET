using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HybridMount.Models.Mounting;
using HybridMount.Models.Mounting.Sources;
using HybridMount.Models.Mounting.WebDAV;
using HybridMount.Models.Persistence;

namespace HybridMount.Tests;

/// <summary>HTTPS 预设、挂载级证书信任和备份兼容验证；真实 TLS 仅使用本机随机端口。</summary>
public sealed class SynologyWebDavHttpsTests
{
    private static FileSourceConfiguration Options=>new(FileSourceKind.WebDav,"https://nas.example.test:5006/共享 文件/");
    [Fact]
    public void Synology_https_port_and_share_path_are_preserved()
    {
        var endpoint=FileSourceFactory.Endpoint(Options);
        Assert.Equal(5006,endpoint.Port);
        Assert.Equal("/共享 文件/",Uri.UnescapeDataString(endpoint.AbsolutePath));
        Assert.Equal("https",endpoint.Scheme);
        Assert.Null(WebDavFileSource.NormalizeCertificatePin(Options));
    }
    [Fact]
    public void Legacy_http_remains_explicitly_opt_in()
    {
        var options=Options with{Endpoint="http://nas.example.test:5005/share/"};
        Assert.Throws<ArgumentException>(()=>FileSourceFactory.Endpoint(options));
        Assert.Equal(5005,FileSourceFactory.Endpoint(options with{AllowHttp=true}).Port);
        Assert.Throws<ArgumentException>(()=>WebDavFileSource.NormalizeCertificatePin(options with{AllowHttp=true,PinnedCertificateSha256=new string('A',64)}));
    }
    [Fact]
    public void Fingerprints_are_normalized_and_malformed_values_rejected()
    {
        Assert.Equal(new string('A',64),WebDavFileSource.NormalizeCertificatePin(Options with{PinnedCertificateSha256=string.Join(":",Enumerable.Repeat("aa",32))}));
        foreach(var invalid in new[]{"1234",new string('G',64),new string('A',65)})
            Assert.Throws<ArgumentException>(()=>FileSourceFactory.ValidateProtocolOptions(Options with{PinnedCertificateSha256=invalid}));
    }
    [Fact]
    public void Confirmed_exception_is_mount_local_and_preserves_default_system_trust()
    {
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN=nas.example.test",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        var pin=Convert.ToHexString(SHA256.HashData(certificate.RawData));
        Assert.False(WebDavFileSource.AcceptCertificate(certificate,SslPolicyErrors.RemoteCertificateChainErrors,null));
        Assert.True(WebDavFileSource.AcceptCertificate(certificate,SslPolicyErrors.None,null));
        Assert.True(WebDavFileSource.AcceptCertificate(certificate,SslPolicyErrors.RemoteCertificateChainErrors,pin));
        Assert.True(WebDavFileSource.AcceptCertificate(certificate,SslPolicyErrors.None,new string('0',64)));
        Assert.False(WebDavFileSource.AcceptCertificate(certificate,SslPolicyErrors.RemoteCertificateChainErrors,new string('0',64)));
        Assert.False(WebDavFileSource.AcceptCertificate(null,SslPolicyErrors.None,pin));
    }
    [Fact]
    public void PinnedWebDavBackupUsesHmxbk()
    {
        var item=new MountBackupItem("WebDAV(HTTPS)","old-path",Options with{PinnedCertificateSha256=new string('A',64)},new("user","password"));
        var bytes=MountBackupCodec.Encode(new(1,DateTimeOffset.UtcNow,[item]),"test-password");
        var decoded=MountBackupCodec.Decode(bytes,"test-password");
        Assert.Equal(item.Source,Assert.Single(decoded.Mounts).Source);
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(new(5,DateTimeOffset.UtcNow,[item]),"test-password"));
    }
    [Fact(Timeout=20000)]
    public async Task Real_pinned_tls_probe_is_read_only_and_does_not_trust_other_mounts_or_replacements()
    {
        await using var server=new TlsFixture(davResponses:true);
        var options=Options with{Endpoint=server.Address.AbsoluteUri,PinnedCertificateSha256=server.CertificatePin};
        var result=await new FileSourceFactory().TestConnectionAsync(options,new("test-user","test-password"));
        Assert.True(result.Success,result.Message);Assert.Equal(new[]{"OPTIONS","PROPFIND"},server.Methods);
        var count=server.Connections;
        await using(var source=new WebDavFileSource(options with{PinnedCertificateSha256=null},new(),new WholeFileCache("unused")))
        {
            var error=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync());
            Assert.Equal(ConnectionFailureKind.Certificate,error.Diagnostic.Kind);Assert.False(error.Diagnostic.Transient);
        }
        Assert.Equal(count+1,server.Connections);Assert.Equal(2,server.Methods.Count);
        await using var replacement=new TlsFixture(davResponses:true);
        await using(var source=new WebDavFileSource(options with{Endpoint=replacement.Address.AbsoluteUri},new(),new WholeFileCache("unused")))
        {
            var error=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync());
            Assert.Equal(ConnectionFailureKind.Certificate,error.Diagnostic.Kind);Assert.False(error.Diagnostic.Transient);
        }
        Assert.Equal(1,replacement.Connections);Assert.Empty(replacement.Methods);
    }
}
