using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>一次实际连接的有界阶段记录；多地址竞争只写本请求的记录，不保存地址、凭据或正文。</summary>
internal sealed class WebDavConnectionTrace
{
    private readonly object _gate=new();
    private readonly List<ConnectionTransportStage> _stages=[];
    private long _tcpAt;
    internal long TlsStartedAt {get{lock(_gate)return _tcpAt==0?Stopwatch.GetTimestamp():_tcpAt;}}
    internal bool Started {get;private set;}
    internal bool TcpConnected {get;private set;}
    internal void Record(string stage,string status,long started,AddressFamily? family=null,int? httpStatus=null,string? canceledBy=null)
    {
        lock(_gate)
        {
            if(stage=="DNS")Started=true;
            if(stage=="TCP"&&status=="Succeeded"){TcpConnected=true;if(_tcpAt==0)_tcpAt=Stopwatch.GetTimestamp();}
            if(_stages.Count==32)_stages.RemoveAt(0);
            _stages.Add(new(stage,status,Stopwatch.GetElapsedTime(started).TotalMilliseconds,family?.ToString(),httpStatus,canceledBy));
        }
    }
    internal IReadOnlyList<ConnectionTransportStage> Snapshot {get{lock(_gate)return Array.AsReadOnly(_stages.ToArray());}}
}

/// <summary>在发送HTTP前按地址族连接，最多同时一个IPv4和一个IPv6候选；成功流拥有唯一Socket，失败和竞争落败均排空。</summary>
internal sealed class WebDavTcpConnector
{
    private readonly Func<string,CancellationToken,Task<IPAddress[]>> _resolve;
    private readonly Func<Socket,EndPoint,CancellationToken,ValueTask> _dial;
    private readonly TimeSpan _deadline,_stagger;
    /// <summary>生产使用系统DNS与Socket；可注入仅用于本机夹具的解析及连接屏障，Socket始终由本类拥有。</summary>
    internal WebDavTcpConnector(Func<string,CancellationToken,Task<IPAddress[]>>? resolve=null,
        Func<Socket,EndPoint,CancellationToken,ValueTask>? dial=null,TimeSpan? deadline=null,TimeSpan? stagger=null)
    {
        _resolve=resolve??((host,token)=>Dns.GetHostAddressesAsync(host,token));
        _dial=dial??((socket,address,token)=>socket.ConnectAsync(address,token));
        _deadline=deadline??TimeSpan.FromSeconds(15);_stagger=stagger??TimeSpan.FromMilliseconds(250);
    }
    internal static IPAddress Normalize(IPAddress address)=>address.IsIPv4MappedToIPv6?address.MapToIPv4():address;
    /// <summary>只返回TCP流，代理CONNECT、SNI、TLS和证书均由SocketsHttpHandler继续处理；DNS与竞争共享期限。</summary>
    internal async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint,WebDavConnectionTrace? trace,CancellationToken token)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(token);lifetime.CancelAfter(_deadline);
        var cancel=lifetime.Token;var attempts=new List<Task<NetworkStream>>();NetworkStream? winner=null;var delivered=false;
        var started=Stopwatch.GetTimestamp();
        try
        {
            IPAddress[] addresses;
            try
            {
                addresses=IPAddress.TryParse(endpoint.Host.Trim('[',']'),out var literal)?[Normalize(literal)]:
                    (await _resolve(endpoint.Host,cancel).ConfigureAwait(false)).Select(Normalize).Distinct().ToArray();
                if(addresses.Length is 0 or >256)throw new SocketException((int)SocketError.HostNotFound);
                trace?.Record("DNS","Succeeded",started);
            }
            catch{trace?.Record("DNS","Failed",started,canceledBy:cancel.IsCancellationRequested?token.IsCancellationRequested?"caller":"connect-deadline":null);throw;}
            var ipv4=addresses.Where(address=>address.AddressFamily==AddressFamily.InterNetwork).ToArray();
            var ipv6=addresses.Where(address=>address.AddressFamily==AddressFamily.InterNetworkV6).ToArray();
            if(ipv4.Length!=0)attempts.Add(DialFamilyAsync(ipv4));
            if(ipv6.Length!=0)
            {
                if(attempts.Count!=0)
                {
                    var delay=Task.Delay(_stagger,cancel);
                    if(await Task.WhenAny(attempts[0],delay).ConfigureAwait(false)==attempts[0]&&attempts[0].IsCompletedSuccessfully)
                    {winner=await attempts[0].ConfigureAwait(false);cancel.ThrowIfCancellationRequested();delivered=true;return winner;}
                    cancel.ThrowIfCancellationRequested();
                }
                attempts.Add(DialFamilyAsync(ipv6));
            }
            var pending=attempts.ToList();Exception? last=null;
            while(pending.Count!=0)
            {
                var done=await Task.WhenAny(pending).ConfigureAwait(false);pending.Remove(done);
                try{winner=await done.ConfigureAwait(false);cancel.ThrowIfCancellationRequested();delivered=true;return winner;}
                catch(Exception error)when(error is SocketException or IOException){last=error;}
            }
            throw last??new SocketException((int)SocketError.HostUnreachable);
        }
        catch(OperationCanceledException error)when(!token.IsCancellationRequested)
        {throw new TimeoutException("WebDAV DNS/TCP connect deadline exceeded.",error);}
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            // 同时成功的另一个连接仍属于本类，不能遗留到连接池外。
            foreach(var attempt in attempts)
                try{var stream=await attempt.ConfigureAwait(false);if(!ReferenceEquals(stream,winner)||!delivered)stream.Dispose();}
                catch(Exception){/* 已观察竞争候选的失败；不得妨碍其余候选释放。 */}
        }

        async Task<NetworkStream> DialFamilyAsync(IReadOnlyList<IPAddress> candidates)
        {
            Exception? last=null;
            foreach(var address in candidates)
            {
                cancel.ThrowIfCancellationRequested();var began=Stopwatch.GetTimestamp();
                var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp){NoDelay=true};
                if(address.AddressFamily==AddressFamily.InterNetworkV6)socket.DualMode=false;
                try
                {
                    await _dial(socket,new IPEndPoint(address,endpoint.Port),cancel).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();
                    var stream=new NetworkStream(socket,ownsSocket:true);
                    trace?.Record("TCP","Succeeded",began,socket.AddressFamily);return stream;
                }
                catch(Exception error)
                {
                    socket.Dispose();trace?.Record("TCP","Failed",began,address.AddressFamily,canceledBy:cancel.IsCancellationRequested?token.IsCancellationRequested?"caller":winner is not null?"race":"connect-deadline":null);
                    if(error is not(SocketException or IOException)||cancel.IsCancellationRequested)throw;
                    last=error;
                }
            }
            throw last??new SocketException((int)SocketError.HostUnreachable);
        }
    }
}
