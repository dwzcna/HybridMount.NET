using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>结构化 TLS 分类、只读重试、写入未知结果及真实本机 TLS 失败；不更改系统证书信任。</summary>
public sealed class WebDavNetworkTests
{
    private static HttpRequestException Reset(bool tls=true)=>new(tls?HttpRequestError.SecureConnectionError:HttpRequestError.ConnectionError,
        "SECRET-URL-AND-RESPONSE",new IOException("PRIVATE-CREDENTIALS",new SocketException((int)SocketError.ConnectionReset)));
    [Fact]
    public void Classification_uses_safe_fields_without_inventing_certificate_failure()
    {
        var reset=WebDavNetworkDiagnostics.Failure(Reset(),"PROPFIND 连接");
        Assert.Equal(ConnectionFailureKind.Tls,reset.Diagnostic.Kind);Assert.True(reset.Diagnostic.Transient);
        Assert.Equal("ConnectionReset",reset.Diagnostic.SocketError);Assert.Contains("10054",reset.Message);
        Assert.DoesNotContain("SECRET",reset.Message);Assert.DoesNotContain("PRIVATE",reset.Message);Assert.Null(reset.InnerException);
        var certificate=WebDavNetworkDiagnostics.Failure(Reset(),"GET 连接",SslPolicyErrors.RemoteCertificateNameMismatch);
        Assert.Equal(ConnectionFailureKind.Certificate,certificate.Diagnostic.Kind);Assert.False(certificate.Diagnostic.Transient);
        var unknown=WebDavNetworkDiagnostics.Failure(new HttpRequestException(HttpRequestError.SecureConnectionError,"SECRET",new AuthenticationException("SECRET")),"GET 连接");
        Assert.Equal(ConnectionFailureKind.Tls,unknown.Diagnostic.Kind);Assert.False(unknown.Diagnostic.Transient);Assert.Contains("不足以确定",unknown.Message);
        Assert.Equal(ConnectionFailureKind.Timeout,WebDavNetworkDiagnostics.Failure(new TaskCanceledException(),"GET 正文",timeout:true).Diagnostic.Kind);
        Assert.Equal(ConnectionFailureKind.Network,WebDavNetworkDiagnostics.Failure(Reset(false),"GET 连接").Diagnostic.Kind);
    }
    private sealed class FaultHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage?>> fault):DelegatingHandler(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false})
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            =>await fault(request,token)??await base.SendAsync(request,token);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Read_retry_is_bounded_and_preserves_xml_range_and_version(bool range)
    {
        await using var fixture=new DavFixture();var attempts=0;var requests=new ConcurrentQueue<(string? Range,string? Version,string Body)>();
        var method=range?"GET":"PROPFIND";
        var handler=new FaultHandler(async (request,token)=>
        {
            if(request.Method.Method!=method)return null;
            requests.Enqueue((request.Headers.Range?.ToString(),request.Headers.IfMatch.FirstOrDefault()?.ToString(),request.Content is null?"":await request.Content.ReadAsStringAsync(token)));
            if(Interlocked.Increment(ref attempts)<3)throw Reset();return null;
        });
        await using var source=new WebDavFileSource(fixture.Options,new("user","app-password"),new WholeFileCache(Path.Combine(fixture.Area,"retry-cache")),handler){ReadRetryDelay=TimeSpan.FromMilliseconds(5)};
        var entry=await source.GetMetadataAsync("文件.bin");
        if(range)Assert.Equal(8,await source.ReadRangeAsync(entry,0,new byte[8]));
        Assert.Equal(3,attempts);Assert.Single(requests.Distinct());
        var sent=requests.First();if(range){Assert.NotNull(sent.Range);Assert.Equal("\"v1\"",sent.Version);}else Assert.Contains("propfind",sent.Body.ToLowerInvariant());
    }
    [Fact]
    public async Task Strict_preview_does_not_multiply_requests_or_change_normal_read_retry()
    {
        await using var fixture=new DavFixture();var attempts=0;
        var handler=new FaultHandler((request,token)=>{if(request.Method.Method=="GET"){Interlocked.Increment(ref attempts);throw Reset();}return Task.FromResult<HttpResponseMessage?>(null);});
        await using var source=new WebDavFileSource(fixture.Options,new(),new WholeFileCache(Path.Combine(fixture.Area,"preview-retry")),handler){ReadRetryDelay=TimeSpan.Zero};
        var entry=await source.GetMetadataAsync("文件.bin");
        await Assert.ThrowsAsync<SourceConnectionException>(async()=>await source.TryReadPreviewRangeAsync(entry,0,new byte[8]));Assert.Equal(1,attempts);
        await Assert.ThrowsAsync<SourceConnectionException>(async()=>await source.ReadRangeAsync(entry,0,new byte[8]));Assert.Equal(4,attempts);
    }
    [Fact]
    public async Task Report_retry_keeps_the_same_token_and_request_body()
    {
        var bodies=new List<string>();var attempts=0;var area=Path.Combine(Path.GetTempPath(),"HybridMount.TlsTests",Guid.NewGuid().ToString("N"));
        var handler=new FaultHandler(async (request,token)=>
        {
            Assert.Equal("REPORT",request.Method.Method);bodies.Add(await request.Content!.ReadAsStringAsync(token));
            if(++attempts<3)throw Reset();
            var response=SyncDavFixture.Multi("urn:next",[]);return new HttpResponseMessage((System.Net.HttpStatusCode)207){Content=new ByteArrayContent(response.Body)};
        });
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),new WholeFileCache(area),handler){ReadRetryDelay=TimeSpan.Zero};
        var result=await source.GetCollectionChangesAsync("","urn:old",default);Assert.Equal("urn:next",result.NextToken);
        Assert.Equal(3,attempts);Assert.Single(bodies.Distinct());Assert.Contains("urn:old",bodies[0]);
    }
    [Fact]
    public async Task Persistent_read_failure_has_three_attempts_and_safe_diagnostic()
    {
        var attempts=0;var handler=new FaultHandler((request,token)=>{Interlocked.Increment(ref attempts);throw Reset();});
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new("secret-user","secret-password"),new WholeFileCache("unused"),handler){ReadRetryDelay=TimeSpan.Zero};
        var failure=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync());
        Assert.Equal(3,attempts);Assert.Equal(3,failure.Diagnostic.Attempts);Assert.DoesNotContain("secret",failure.Message);Assert.Contains("OPTIONS",failure.Diagnostic.Stage);
    }
    [Fact]
    public async Task Cancellation_during_backoff_remains_cancellation_and_stops_promptly()
    {
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var attempts=0;
        var handler=new FaultHandler((request,token)=>{Interlocked.Increment(ref attempts);entered.TrySetResult();throw Reset();});
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),new WholeFileCache("unused"),handler){ReadRetryDelay=TimeSpan.FromSeconds(2)};
        using var stop=new CancellationTokenSource();var read=source.ProbeAsync(stop.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var watch=Stopwatch.StartNew();stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>read);
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(2));Assert.Equal(1,attempts);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task Sent_mutation_is_never_retried_and_keeps_unknown_result(bool cancel)
    {
        var attempts=0;var applied=false;using var stop=new CancellationTokenSource();
        var handler=new FaultHandler(async (request,token)=>
        {
            Assert.Equal("MKCOL",request.Method.Method);Interlocked.Increment(ref attempts);applied=true;
            if(cancel){stop.CancelAfter(40);await Task.Delay(Timeout.InfiniteTimeSpan,token);}throw Reset();
        });
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),new WholeFileCache("unused"),handler){ReadRetryDelay=TimeSpan.Zero};
        var failure=await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.CreateDirectoryAsync("created",stop.Token));
        Assert.True(applied);Assert.Equal(1,attempts);Assert.NotNull(failure.InnerException);Assert.Contains("不确定",failure.Message);
    }
    [Theory(Timeout=15000)]
    [InlineData(false)][InlineData(true)]
    public async Task Real_untrusted_or_wrong_host_certificate_is_rejected_without_retry(bool wrongHost)
    {
        await using var server=new TlsFixture(wrongHost:wrongHost);
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,server.Address.AbsoluteUri),new(),new WholeFileCache("unused"));
        var failure=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync());
        Assert.True(failure.Diagnostic.Kind==ConnectionFailureKind.Certificate,failure.Message+"; server="+string.Join(" | ",server.Failures));Assert.False(failure.Diagnostic.Transient);
        Assert.Contains("RemoteCertificateChainErrors",failure.Diagnostic.CertificateError);
        if(wrongHost)Assert.Contains("RemoteCertificateNameMismatch",failure.Diagnostic.CertificateError);
        Assert.Equal(1,server.Connections);
    }
    [Fact(Timeout=15000)]
    public async Task Real_tls_remote_reset_is_a_handled_safe_failure()
    {
        await using var server=new TlsFixture(reset:true);
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,server.Address.AbsoluteUri),new(),new WholeFileCache("unused")){ReadRetryDelay=TimeSpan.Zero};
        var failure=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync());
        Assert.Equal(ConnectionFailureKind.Tls,failure.Diagnostic.Kind);Assert.True(server.ClientHelloSeen);
        Assert.InRange(server.Connections,1,3);Assert.DoesNotContain(server.Address.AbsoluteUri,failure.Message);
    }
    [Theory(Timeout=15000)]
    [InlineData(false)][InlineData(true)]
    public async Task Real_slow_handshake_has_deadline_and_caller_cancellation(bool caller)
    {
        await using var server=new TlsFixture(stall:true);
        await using var source=new WebDavFileSource(new(FileSourceKind.WebDav,server.Address.AbsoluteUri),new(),new WholeFileCache("unused")){MetadataDeadline=TimeSpan.FromMilliseconds(caller?5000:150)};
        using var stop=new CancellationTokenSource();if(caller)stop.CancelAfter(80);
        var watch=Stopwatch.StartNew();
        if(caller)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>source.ProbeAsync(stop.Token));
        else Assert.Equal(ConnectionFailureKind.Timeout,(await Assert.ThrowsAsync<SourceConnectionException>(()=>source.ProbeAsync())).Diagnostic.Kind);
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(5));
    }
    [Theory(Timeout=15000)]
    [InlineData(false)][InlineData(true)]
    public async Task Form_probe_deadline_and_caller_cancellation_are_distinct(bool caller)
    {
        await using var server=new TlsFixture(stall:true);using var cancel=new CancellationTokenSource();if(caller)cancel.CancelAfter(60);
        var options=new FileSourceConfiguration(FileSourceKind.WebDav,server.Address.AbsoluteUri);
        var work=new FileSourceFactory().TestConnectionAsync(options,new(),cancel.Token,TimeSpan.FromMilliseconds(150));
        if(caller)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);
        else{var result=await work;Assert.False(result.Success);Assert.Equal(ConnectionFailureKind.Timeout,result.Diagnostic?.Kind);}
    }
    [Fact]
    public void Repeated_failures_are_merged_and_recovery_is_recorded_once()
    {
        var now=DateTimeOffset.UtcNow;var gate=new NetworkFailureLog(()=>now);var diagnostic=WebDavNetworkDiagnostics.Failure(Reset(),"GET").Diagnostic;var emitted=0;
        Parallel.For(0,100,iteration=>{if(gate.ShouldWrite(diagnostic,out _))Interlocked.Increment(ref emitted);});Assert.Equal(1,emitted);
        now+=TimeSpan.FromMinutes(6);Assert.True(gate.ShouldWrite(diagnostic,out var suffix));Assert.Contains("99",suffix);
        Assert.True(gate.Recovered(out var recovery));Assert.Contains("恢复",recovery);Assert.False(gate.Recovered(out _));
        Assert.True(gate.ShouldWrite(diagnostic,out _));
    }
}

/// <summary>只监听本机随机端口，不安装证书信任；用户临时密钥容器随证书释放，不提供外部服务。</summary>
internal sealed class TlsFixture : IAsyncDisposable
{
    private readonly TcpListener _listener=new(System.Net.IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();
    private readonly ConcurrentBag<Task> _clients=[];
    private readonly Task _accept;
    private readonly bool _reset,_stall;
    private readonly X509Certificate2 _certificate;
    private int _connections,_hello;
    public ConcurrentQueue<string> Failures {get;}=new();
    public int Connections=>Volatile.Read(ref _connections);
    public bool ClientHelloSeen=>Volatile.Read(ref _hello)!=0;
    public Uri Address {get;}
    public TlsFixture(bool wrongHost=false,bool reset=false,bool stall=false)
    {
        _reset=reset;_stall=stall;
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=hybridmount-test",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},true));
        var names=new SubjectAlternativeNameBuilder();if(wrongHost)names.AddDnsName("unrelated.example.test");else names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannel 服务端需可用的用户密钥容器；仅导入私钥材料，不安装证书信任，Dispose 清理临时容器。
        var material=generated.Export(X509ContentType.Pfx);
        try{_certificate=X509CertificateLoader.LoadPkcs12(material,null,X509KeyStorageFlags.UserKeySet|X509KeyStorageFlags.Exportable);}
        finally{CryptographicOperations.ZeroMemory(material);}
        _listener.Start();Address=new($"https://127.0.0.1:{((System.Net.IPEndPoint)_listener.LocalEndpoint).Port}/dav/");_accept=AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);Interlocked.Increment(ref _connections);_clients.Add(ServeAsync(client));}}
        catch(OperationCanceledException){ }catch(SocketException)when(_stop.IsCancellationRequested){ }catch(ObjectDisposedException)when(_stop.IsCancellationRequested){ }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using(client)
        try
        {
            await using var network=client.GetStream();
            if(_reset)
            {
                var first=new byte[1];if(await network.ReadAsync(first,_stop.Token)>0&&first[0]==0x16)Interlocked.Exchange(ref _hello,1);
                client.Client.LingerState=new(true,0);return;
            }
            if(_stall){await Task.Delay(Timeout.InfiniteTimeSpan,_stop.Token);return;}
            await using var tls=new SslStream(network,false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=_certificate,EnabledSslProtocols=SslProtocols.None},_stop.Token);
            await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),_stop.Token);
        }
        catch(Exception error)when(error is IOException or AuthenticationException or OperationCanceledException or SocketException)
        {
            var safe=new List<string>();for(var inner=error;inner is not null&&safe.Count<8;inner=inner.InnerException)safe.Add(inner.GetType().Name+"/"+inner.HResult.ToString("X8")+(inner is System.ComponentModel.Win32Exception native?"/"+native.NativeErrorCode.ToString("X8"):""));
            Failures.Enqueue(string.Join(">",safe));
        }
    }
    public async ValueTask DisposeAsync(){await _stop.CancelAsync();_listener.Stop();await _accept;await Task.WhenAll(_clients);_certificate.Dispose();_stop.Dispose();}
}
