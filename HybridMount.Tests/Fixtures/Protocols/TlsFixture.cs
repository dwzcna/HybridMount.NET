using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Files.Content;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Infrastructure.Protocols.WebDAV;
using HybridMount.Models.Mounting;
using HybridMount.Tests.Integration.Mounting;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace HybridMount.Tests.Fixtures.Protocols;

/// <summary>只监听本机随机端口，不安装证书信任；用户临时密钥容器随证书释放，不提供外部服务。</summary>
internal sealed class TlsFixture : IAsyncDisposable
{
    private readonly TcpListener _listener=new(System.Net.IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();
    private readonly ConcurrentBag<Task> _clients=[];
    private readonly Task _accept;
    private readonly bool _reset,_stall,_davResponses;
    private readonly SslProtocols _protocols;
    public ConcurrentQueue<SslProtocols> Negotiated {get;}=new();
    private readonly X509Certificate2 _certificate;
    private int _connections,_hello;
    public ConcurrentQueue<string> Failures {get;}=new();
    public int Connections=>Volatile.Read(ref _connections);
    public bool ClientHelloSeen=>Volatile.Read(ref _hello)!=0;
    public Uri Address {get;}
    /// <summary>仅供隔离测试显式核验服务器证书，不安装系统信任。</summary>
    public string CertificatePin=>Convert.ToHexString(SHA256.HashData(_certificate.RawData));
    public ConcurrentQueue<string> Methods {get;}=new();
    public TlsFixture(bool wrongHost=false,bool reset=false,bool stall=false,bool davResponses=false,bool expired=false,SslProtocols protocols=SslProtocols.None)
    {
        _reset=reset;_stall=stall;_davResponses=davResponses;_protocols=protocols;
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=hybridmount-test",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new("1.3.6.1.5.5.7.3.1")},true));
        var names=new SubjectAlternativeNameBuilder();if(wrongHost)names.AddDnsName("unrelated.example.test");else names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(expired?-10:-1),DateTimeOffset.UtcNow.AddDays(expired?-1:1));
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
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=_certificate,EnabledSslProtocols=_protocols},_stop.Token);
            Negotiated.Enqueue(tls.SslProtocol);
            if(_davResponses)
            {
                // 实际消费请求头及正文后返回只读 DAV 集合，避免仅凭握手成功宣称探测通过。
                var header=new List<byte>();var one=new byte[1];
                while(header.Count<16384)
                {
                    if(await tls.ReadAsync(one,_stop.Token)==0)throw new IOException("请求提前结束。");
                    header.Add(one[0]);if(header.Count>=4&&header.TakeLast(4).SequenceEqual(new byte[]{13,10,13,10}))break;
                }
                var lines=System.Text.Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                var method=lines[0].Split(' ')[0];Methods.Enqueue(method);
                var length=lines.Where(x=>x.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)).Select(x=>int.Parse(x.Split(':')[1])).SingleOrDefault();
                if(length>0)await tls.ReadExactlyAsync(new byte[length],_stop.Token);
                var body=method=="PROPFIND"?"<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/dav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype><d:getcontentlength>0</d:getcontentlength><d:getetag>&amp;quot;root&amp;quot;</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>":"";
                var bytes=System.Text.Encoding.UTF8.GetBytes(body);var status=method=="PROPFIND"?"207 Multi-Status":"200 OK";
                await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {bytes.Length}\r\nContent-Type: application/xml\r\nConnection: close\r\n\r\n"),_stop.Token);
                if(bytes.Length>0)await tls.WriteAsync(bytes,_stop.Token);return;
            }
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
