using HybridMount.Infrastructure.Protocols.WebDAV;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
namespace HybridMount.Tests.Integration.Protocols;

/// <summary>本机真实Socket及受控DNS／竞争；验证地址族和实际句柄回收，不访问生产网络。</summary>
public sealed class WebDavTcpConnectorTests
{
    [Theory(Timeout=10000)]
    [InlineData("127.0.0.1")][InlineData("::ffff:127.0.0.1")]
    public async Task IPv4AndMappedLiteralsAlwaysUsePureIPv4(string host)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try
        {
            var dns=0;Socket? observed=null;var trace=new WebDavConnectionTrace();
            var connector=new WebDavTcpConnector((_,_)=>{dns++;throw new InvalidOperationException();},async (socket,address,token)=>
            {observed=socket;Assert.Equal(AddressFamily.InterNetwork,socket.AddressFamily);await socket.ConnectAsync(address,token);});
            using(var stream=await connector.ConnectAsync(new(host,((IPEndPoint)listener.LocalEndpoint).Port),trace,default))
            {using var accepted=await listener.AcceptSocketAsync();Assert.Equal(AddressFamily.InterNetwork,accepted.RemoteEndPoint!.AddressFamily);Assert.NotNull(observed);}
            Assert.Equal(0,dns);Assert.Contains(trace.Snapshot,item=>item.Stage=="TCP"&&item.AddressFamily=="InterNetwork"&&item.Status=="Succeeded");
        }
        finally{listener.Stop();}
    }
    [Fact(Timeout=10000)]
    public async Task DNSKeepsIPv4OrderAndDoesNotStartIPv6AfterEarlySuccess()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var seen=new List<string>();
        try
        {
            var connector=new WebDavTcpConnector((_,_)=>Task.FromResult(new[]{IPAddress.IPv6Loopback,IPAddress.Parse("127.0.0.2"),IPAddress.Loopback}),async (socket,address,token)=>
            {
                var target=(IPEndPoint)address;seen.Add(target.Address.ToString());
                if(target.Address.Equals(IPAddress.Parse("127.0.0.2")))throw new SocketException((int)SocketError.ConnectionRefused);
                await socket.ConnectAsync(address,token);
            });
            using var stream=await connector.ConnectAsync(new("isolated.invalid",((IPEndPoint)listener.LocalEndpoint).Port),new(),default);
            Assert.Equal(new[]{"127.0.0.2","127.0.0.1"},seen);
        }
        finally{listener.Stop();}
    }
    [Fact(Timeout=10000)]
    public async Task IPv6WinnerStartsAfterDelayAndLosingIPv4SocketIsDisposedBeforeReturn()
    {
        var listener=new TcpListener(IPAddress.IPv6Loopback,0);listener.Server.DualMode=false;listener.Start();
        var sockets=new ConcurrentBag<SafeSocketHandle>();var started=System.Diagnostics.Stopwatch.StartNew();long ipv6At=0;var active=0;var maximum=0;
        try
        {
            var connector=new WebDavTcpConnector((_,_)=>Task.FromResult(new[]{IPAddress.Loopback,IPAddress.IPv6Loopback}),async (socket,address,token)=>
            {
                sockets.Add(socket.SafeHandle);var current=Interlocked.Increment(ref active);int previous;do{previous=Volatile.Read(ref maximum);if(current<=previous)break;}while(Interlocked.CompareExchange(ref maximum,current,previous)!=previous);
                try
                {
                    if(socket.AddressFamily==AddressFamily.InterNetwork)await Task.Delay(Timeout.InfiniteTimeSpan,token);
                    else{Assert.False(socket.DualMode);ipv6At=started.ElapsedMilliseconds;await socket.ConnectAsync(address,token);}
                }
                finally{Interlocked.Decrement(ref active);}
            });
            var trace=new WebDavConnectionTrace();
            using(var stream=await connector.ConnectAsync(new("isolated.invalid",((IPEndPoint)listener.LocalEndpoint).Port),trace,default))
            {
                Assert.InRange(ipv6At,200,3000);Assert.InRange(maximum,1,2);Assert.Equal(1,sockets.Count(item=>item.IsClosed));
                Assert.Contains(trace.Snapshot,item=>item.CancellationSource=="race");
            }
            Assert.All(sockets,item=>Assert.True(item.IsClosed));
        }
        finally{listener.Stop();}
    }
    [Theory(Timeout=10000)]
    [InlineData(false)][InlineData(true)]
    public async Task DNSCancellationAndDeadlineDoNotCreateAnySocket(bool caller)
    {
        using var cancel=new CancellationTokenSource();if(caller)cancel.CancelAfter(60);var dials=0;var trace=new WebDavConnectionTrace();
        var connector=new WebDavTcpConnector(async (_,token)=>{await Task.Delay(Timeout.InfiniteTimeSpan,token);return [];},
            (_,_,_)=>{dials++;throw new InvalidOperationException();},deadline:TimeSpan.FromMilliseconds(100));
        if(caller)await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await connector.ConnectAsync(new("isolated.invalid",5006),trace,cancel.Token));
        else await Assert.ThrowsAsync<TimeoutException>(async()=>await connector.ConnectAsync(new("isolated.invalid",5006),trace,cancel.Token));
        Assert.Equal(0,dials);Assert.Contains(trace.Snapshot,item=>item.CancellationSource==(caller?"caller":"connect-deadline"));
    }
    [Fact(Timeout=10000)]
    public async Task CancelDuringBothCandidatesClosesEveryAllocatedSocket()
    {
        using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));var sockets=new ConcurrentBag<SafeSocketHandle>();
        var connector=new WebDavTcpConnector((_,_)=>Task.FromResult(new[]{IPAddress.Loopback,IPAddress.IPv6Loopback}),async (socket,_,token)=>
        {sockets.Add(socket.SafeHandle);await Task.Delay(Timeout.InfiniteTimeSpan,token);},stagger:TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await connector.ConnectAsync(new("isolated.invalid",5006),new(),cancel.Token));
        Assert.Equal(2,sockets.Count);Assert.All(sockets,item=>Assert.True(item.IsClosed));
    }
}
