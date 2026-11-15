using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using HybridMount.Models.Mounting.WebDAV;

namespace HybridMount.Tests;

/// <summary>本机真实TLS与纯草稿回归；不改变系统信任、TLS策略或产品挂载，不发送写请求。</summary>
public sealed class WebDavTransportTests
{
    [Theory(Timeout = 20000)]
    [InlineData(SslProtocols.Tls12, ProtocolTransportVersion.Tls12, ProtocolCapabilityStatus.Unknown)]
    [InlineData(SslProtocols.Tls13, ProtocolTransportVersion.Tls13, ProtocolCapabilityStatus.Supported)]
    public async Task Actual_target_handshake_is_published_and_survives_metadata_updates(
        SslProtocols protocol, ProtocolTransportVersion expected, ProtocolCapabilityStatus status)
    {
        await using var server = new TlsFixture(davResponses: true, protocols: protocol);
        var options = new FileSourceConfiguration(FileSourceKind.WebDav, server.Address.AbsoluteUri,
            PinnedCertificateSha256: server.CertificatePin);
        var result = await new FileSourceFactory().TestConnectionAsync(options, new());
        Assert.True(result.Success, result.Message);
        var snapshot = Assert.IsType<ProtocolCapabilitySnapshot>(result.Capabilities);
        Assert.Equal(expected, snapshot.Transport?.Version);
        Assert.Equal(status, ProtocolCapabilitySummary.Read(snapshot, "dav.tls13").Status);
        Assert.Equal(ProtocolCapabilityStatus.Supported, ProtocolCapabilitySummary.Read(snapshot, "dav.propfind").Status);
        Assert.All(server.Negotiated, negotiated => Assert.Equal(protocol, negotiated));
        Assert.Equal(new[] { "PROPFIND", "OPTIONS" }, server.Methods);
        Assert.Equal(2, server.Connections); // 夹具每响应关闭，不为展示增加握手或请求。
        Assert.InRange(snapshot.Transport!.ObservedUtc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
    }

    [Fact(Timeout = 15000)]
    public async Task Rejected_or_captured_certificate_is_not_successful_tls_evidence()
    {
        await using var server = new TlsFixture(davResponses: true);
        var result = await new FileSourceFactory().TestConnectionAsync(
            new(FileSourceKind.WebDav, server.Address.AbsoluteUri), new());
        Assert.False(result.Success);
        Assert.Null(result.Capabilities?.Transport);
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(result.Capabilities!, "dav.tls13").Status);
        _ = await WebDavCertificateFetcher.FetchCertificateAsync(server.Address.AbsoluteUri, default);
        Assert.Empty(server.Methods);
        Assert.Null(result.Capabilities?.Transport);
    }

    [Fact(Timeout = 15000)]
    public async Task Canceled_handshake_cannot_publish_transport()
    {
        await using var server = new TlsFixture(stall: true);
        await using var source = new WebDavFileSource(new(FileSourceKind.WebDav, server.Address.AbsoluteUri),
            new(), new WholeFileCache(Path.Combine(Path.GetTempPath(), "HybridMount.TlsOnly", Guid.NewGuid().ToString("N"))));
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ProbeAsync(cancel.Token));
        Assert.Null(source.ProtocolCapabilities.Transport);
        Assert.Empty(server.Methods);
    }

    [Fact(Timeout = 15000)]
    public async Task Authenticated_outer_proxy_tls_does_not_become_target_evidence()
    {
        await using var server = new TlsFixture();
        using var socket = new TcpClient();
        await socket.ConnectAsync(server.Address.Host, server.Address.Port);
        await using var tls = new SslStream(socket.GetStream(), false, (_, _, _, _) => true);
        Assert.Null(WebDavTlsObservation.Observe(server.Address, tls));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "proxy.example.test", EnabledSslProtocols = SslProtocols.None
        });
        Assert.Null(WebDavTlsObservation.Observe(server.Address, tls));
        Assert.Null(WebDavTlsObservation.Observe(new Uri("http://proxy.example.test/"), tls));
        Assert.NotNull(WebDavTlsObservation.Observe(new Uri("https://proxy.example.test/"), tls));
    }

    [Fact]
    public async Task Http_and_new_sessions_do_not_inherit_tls_and_snapshots_remain_immutable()
    {
        await using var source = new WebDavFileSource(new(FileSourceKind.WebDav, "http://127.0.0.1:1/", AllowHttp: true),
            new(), new WholeFileCache(Path.Combine(Path.GetTempPath(), "HybridMount.TlsOnly", Guid.NewGuid().ToString("N"))));
        Assert.Equal(ProtocolTransportVersion.Http, source.ProtocolCapabilities.Transport?.Version);
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "dav.tls13").Status);
        var tracker = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        var old = tracker.Snapshot;
        var observed = new ProtocolTransportEvidence(ProtocolTransportVersion.Tls13, DateTimeOffset.UtcNow);
        tracker.Record("dav.tls13", ProtocolCapabilityStatus.Supported, "@server", transport: observed);
        tracker.Record("sqs.attributes", ProtocolCapabilityStatus.Supported);
        tracker.ClearPrefix("sqs.");
        Assert.Same(observed, tracker.Snapshot.Transport);
        Assert.Null(old.Transport);
        Assert.Null(new ProtocolCapabilityTracker(FileSourceKind.WebDav).Snapshot.Transport);
    }

    [Theory]
    [InlineData("http://dav.example.test:8080/root/", true)]
    [InlineData("https://dav.example.test:8443/root/", false)]
    [InlineData("", false)]
    [InlineData("dav.example.test/root", false)]
    public void New_and_imported_draft_derive_flags_without_rewriting_url(string address, bool allowHttp)
    {
        var settings = WebDavEndpointDraft.Create(address, null, null);
        Assert.Equal(address, settings.Endpoint);
        Assert.Equal(allowHttp, settings.AllowHttp);
        if (address.StartsWith("http")) Assert.Equal(new Uri(address).Port, FileSourceFactory.Endpoint(settings).Port);
        else Assert.Throws<ArgumentException>(() => FileSourceFactory.Endpoint(settings));
    }

    [Theory]
    [InlineData("https://dav.example.test/", true)]
    [InlineData("https://dav.example.test/", false)]
    [InlineData("http://dav.example.test/", true)]
    public void Unchanged_existing_endpoint_preserves_legacy_flag(string address, bool allowHttp)
    {
        var original = new FileSourceConfiguration(FileSourceKind.WebDav, address, AllowHttp: allowHttp);
        Assert.Equal(original, WebDavEndpointDraft.Create(address, original, null));
        Assert.False(WebDavEndpointDraft.Create("https://other.example.test/", original, null).AllowHttp);
        Assert.True(WebDavEndpointDraft.Create("http://other.example.test/", original, null).AllowHttp);
    }
    [Fact]
    public void Independent_test_does_not_override_live_transport_and_reconnect_retires_it()
    {
        var source = new FileSourceConfiguration(FileSourceKind.WebDav, "https://dav.example.test/");
        var definition = new MountDefinition(Guid.NewGuid(), "mount", "D:\\unused", Source: source);
        var live = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        live.Record("dav.tls13", ProtocolCapabilityStatus.Unknown, "@server",
            transport: new(ProtocolTransportVersion.Tls12, DateTimeOffset.UtcNow));
        var draft = new ProtocolCapabilityDraftSession(FileSourceKind.WebDav, "same", 0, source);
        var mount = new MountSnapshot(definition, "", "", IsConnected: true, ProtocolCapabilities: live.Snapshot);
        _ = draft.Select("same", mount);
        var tested = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        tested.Record("dav.tls13", ProtocolCapabilityStatus.Supported, "@server",
            transport: new(ProtocolTransportVersion.Tls13, DateTimeOffset.UtcNow));
        draft.Begin("same"); Assert.True(draft.Complete("same", tested.Snapshot));
        var combined = Assert.IsType<ProtocolCapabilitySnapshot>(draft.Select("same", mount));
        Assert.Equal(ProtocolTransportVersion.Tls12, combined.Transport?.Version);
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(combined, "dav.tls13").Status);
        var reconnected = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        Assert.Same(reconnected.Snapshot, draft.Select("same", mount with { ProtocolCapabilities = reconnected.Snapshot }));
        draft.Begin("same");draft.Invalidate();
        Assert.False(draft.Complete("same", tested.Snapshot));
    }

}
