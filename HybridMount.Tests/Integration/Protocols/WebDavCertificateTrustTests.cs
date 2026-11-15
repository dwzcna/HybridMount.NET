using HybridMount.Tests.Fixtures.Protocols;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Infrastructure.Protocols.WebDAV;
using HybridMount.Models.Mounting;
using HybridMount.Models.Protocols.WebDAV;
using HybridMount.Tests.Integration.Protocols;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
namespace HybridMount.Tests.Integration.Protocols;

/// <summary>本机隔离TLS及确认代次验证；不安装信任、不创建挂载，不访问生产服务器。</summary>
public sealed class WebDavCertificateTrustTests
{
    [Theory(Timeout=20000)]
    [InlineData(false)] [InlineData(true)]
    public async Task RejectedCertificateExposesDetailsAndRequiresExplicitScopedConfirmation(bool expired)
    {
        await using var server=new TlsFixture(wrongHost:!expired,davResponses:true,expired:expired);
        var options=new FileSourceConfiguration(FileSourceKind.WebDav,server.Address.AbsoluteUri);
        var result=await new FileSourceFactory().TestConnectionAsync(options,new("private-user","private-password"));
        Assert.False(result.Success);Assert.Equal(ConnectionFailureKind.Certificate,result.Diagnostic?.Kind);
        var certificate=Assert.IsType<WebDavCertificateInfo>(result.Certificate);
        Assert.Equal(server.CertificatePin,certificate.Sha256);Assert.Equal(server.Address.GetLeftPart(UriPartial.Authority),certificate.Server);
        Assert.NotEmpty(certificate.Subject);Assert.NotEmpty(certificate.Issuer);Assert.True(certificate.NotBeforeUtc<certificate.NotAfterUtc);
        if(expired){Assert.True(certificate.NotAfterUtc<DateTimeOffset.UtcNow);Assert.Contains("有效期",result.Message);}
        else Assert.True((certificate.PolicyErrors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0);
        Assert.False(result.Diagnostic!.Transient);Assert.Equal(1,server.Connections);Assert.Empty(server.Methods);
        var trust=new WebDavCertificateTrustSession();trust.Begin("draft");trust.Offer("draft",certificate);
        Assert.Null(trust.Confirm("different-draft",server.Address));
        Assert.Null(trust.Confirm("draft",new Uri("https://unrelated.example.test/")));
        var pin=Assert.IsType<string>(trust.Confirm("draft",server.Address));
        Assert.Null(trust.Confirm("draft",server.Address));
        var accepted=await new FileSourceFactory().TestConnectionAsync(options with {PinnedCertificateSha256=pin},new());
        Assert.True(accepted.Success,accepted.Message);Assert.Equal(new[]{"PROPFIND","OPTIONS"},server.Methods);
        Assert.Null(accepted.Certificate);Assert.DoesNotContain("private-",JsonSerializer.Serialize(result));
        Assert.DoesNotContain("Sha256",JsonSerializer.Serialize(result));
        var other=await new FileSourceFactory().TestConnectionAsync(options,new());
        Assert.Equal(ConnectionFailureKind.Certificate,other.Diagnostic?.Kind);Assert.Equal(2,server.Methods.Count);
    }
    [Theory(Timeout=15000)]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)]
    public async Task Fetch_captures_public_leaf_without_sending_any_http(bool expired,bool wrongHost)
    {
        await using var server=new TlsFixture(wrongHost:wrongHost,davResponses:true,expired:expired);
        var certificate=await WebDavCertificateFetcher.FetchCertificateAsync(server.Address.AbsoluteUri,default);
        Assert.Equal(server.CertificatePin,certificate.Sha256);
        Assert.Equal(64,certificate.Sha256.Length);Assert.Empty(server.Methods);Assert.Equal(1,server.Connections);
    }
    [Theory(Timeout=15000)]
    [InlineData(false)] [InlineData(true)]
    public async Task Fetch_stalled_tls_obeys_its_deadline_and_caller_cancellation(bool caller)
    {
        await using var server=new TlsFixture(stall:true);using var cancel=new CancellationTokenSource();
        if(caller)cancel.CancelAfter(50);
        var work=WebDavCertificateFetcher.FetchCertificateAsync(server.Address.AbsoluteUri,cancel.Token,TimeSpan.FromMilliseconds(150));
        if(caller)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);
        else Assert.Equal(ConnectionFailureKind.Timeout,(await Assert.ThrowsAsync<SourceConnectionException>(()=>work)).Diagnostic.Kind);
        Assert.Empty(server.Methods);
    }
    [Fact(Timeout=15000)]
    public async Task Concurrent_rejections_keep_each_requests_certificate()
    {
        await using var first=new TlsFixture();await using var second=new TlsFixture(wrongHost:true);
        var factory=new FileSourceFactory();
        var tasks=new[]{factory.TestConnectionAsync(new(FileSourceKind.WebDav,first.Address.AbsoluteUri),new()),
            factory.TestConnectionAsync(new(FileSourceKind.WebDav,second.Address.AbsoluteUri),new())};
        var results=await Task.WhenAll(tasks);
        Assert.Equal(first.CertificatePin,results[0].Certificate?.Sha256);Assert.Equal(second.CertificatePin,results[1].Certificate?.Sha256);
        Assert.Empty(first.Methods);Assert.Empty(second.Methods);
    }
    [Fact]
    public void DraftChangesAndReturningToOriginalRetireCertificateCandidate()
    {
        using var rsa=RSA.Create(2048);
        var request=new CertificateRequest("CN=test",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var leaf=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2),DateTimeOffset.UtcNow.AddDays(-1));
        var endpoint=new Uri("https://example.test:5006/");
        var certificate=WebDavCertificateInfo.Capture(endpoint,leaf,SslPolicyErrors.RemoteCertificateChainErrors,X509ChainStatusFlags.NotTimeValid);
        var trust=new WebDavCertificateTrustSession();trust.Begin("original");trust.Invalidate();trust.Invalidate();trust.Offer("original",certificate);
        Assert.Null(trust.Candidate);Assert.Null(trust.Confirm("original",endpoint));
        trust.Begin("original");trust.Offer("original",certificate);Assert.Same(certificate,trust.Candidate);
        trust.Invalidate();Assert.Null(trust.Confirm("original",endpoint));
        trust.Begin("current");trust.Offer("current",certificate);Assert.Null(trust.Confirm("current",new Uri("http://example.test:5006/")));
    }
    [Fact]
    public void CertificateFailurePrecedesTimeoutButTlsWithoutCertificateRemainsTls()
    {
        var failure=WebDavNetworkDiagnostics.Failure(new TaskCanceledException(),"测试连接",SslPolicyErrors.RemoteCertificateChainErrors,X509ChainStatusFlags.NotTimeValid,timeout:true);
        Assert.Equal(ConnectionFailureKind.Certificate,failure.Diagnostic.Kind);Assert.False(failure.Diagnostic.Transient);Assert.Contains("有效期",failure.Message);
        var tls=WebDavNetworkDiagnostics.Failure(new System.Security.Authentication.AuthenticationException(),"测试连接");
        Assert.Equal(ConnectionFailureKind.Tls,tls.Diagnostic.Kind);
        Assert.Equal(ConnectionFailureKind.Timeout,WebDavNetworkDiagnostics.Failure(new TaskCanceledException(),"测试连接",timeout:true).Diagnostic.Kind);
    }
}
