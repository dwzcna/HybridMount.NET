using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HybridMount.Tests;

/// <summary>隔离 TCP HTTP 服务，走真实 HttpClient/SDK；无需 URL ACL、管理员服务或外部凭据。</summary>
internal sealed class HttpTestServer : IAsyncDisposable
{
    internal sealed record Request(string Method, string Target, Dictionary<string,string> Headers, byte[] Body);
    internal sealed record Response(int Status, byte[] Body, Dictionary<string,string>? Headers=null)
    {
        public static Response Text(int status,string body,Dictionary<string,string>? headers=null)=>new(status,Encoding.UTF8.GetBytes(body),headers);
    }
    private readonly TcpListener _listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();
    private readonly ConcurrentBag<Task> _clients=[];
    private readonly Task _accept;
    public Uri Address { get; }
    public ConcurrentQueue<Request> Requests { get; }=new();
    public Func<Request,Task<Response>> Handler { get; set; }=_=>Task.FromResult(new Response(404,[]));
    public HttpTestServer()
    {
        _listener.Start();Address=new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");_accept=AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try { while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_clients.Add(ServeAsync(client));} }
        catch(OperationCanceledException){}catch(SocketException)when(_stop.IsCancellationRequested){}
    }
    private async Task ServeAsync(TcpClient client)
    {
        using(client)
        try
        {
            await using var stream=client.GetStream();
            var first=(await Line(stream)).Split(' ');if(first.Length<2)return;
            var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            while(await Line(stream) is {Length:>0} line){var colon=line.IndexOf(':');if(colon<1)throw new IOException("Header");headers[line[..colon]]=line[(colon+1)..].Trim();}
            if(headers.TryGetValue("Expect",out var expect)&&expect.Contains("100-continue",StringComparison.OrdinalIgnoreCase))await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(),_stop.Token);
            byte[] body=[];
            if(headers.TryGetValue("Content-Length",out var value))
            {var length=int.Parse(value);if(length>32*1024*1024)throw new IOException("Body limit");body=new byte[length];await stream.ReadExactlyAsync(body,_stop.Token);}
            else if(headers.TryGetValue("Transfer-Encoding",out var encoding)&&encoding.Contains("chunked"))
            {
                using var content=new MemoryStream();
                while(true)
                {
                    var length=Convert.ToInt32((await Line(stream)).Split(';')[0],16);if(length==0){while((await Line(stream)).Length>0){}break;}
                    var chunk=new byte[length];await stream.ReadExactlyAsync(chunk,_stop.Token);await content.WriteAsync(chunk,_stop.Token);await Line(stream);
                }
                body=content.ToArray();
            }
            var request=new Request(first[0],first[1],headers,body);Requests.Enqueue(request);
            var response=await Handler(request);
            var fields=response.Headers is null?new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase):new(response.Headers,StringComparer.OrdinalIgnoreCase);
            fields.TryAdd("Content-Length",response.Body.Length.ToString());fields["Connection"]="close";
            var head=$"HTTP/1.1 {response.Status} Result\r\n"+string.Join("",fields.Select(pair=>$"{pair.Key}: {pair.Value}\r\n"))+"\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head),_stop.Token);
            if(request.Method!="HEAD")await stream.WriteAsync(response.Body,_stop.Token);
        }
        catch(Exception error)when(error is IOException or OperationCanceledException or SocketException){}
    }
    private async Task<string> Line(Stream stream)
    {
        using var bytes=new MemoryStream();var one=new byte[1];
        while(await stream.ReadAsync(one,_stop.Token)!=0)
        {if(one[0]==10)return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');bytes.WriteByte(one[0]);if(bytes.Length>65536)throw new IOException("Line limit");}
        return Encoding.ASCII.GetString(bytes.ToArray());
    }
    public async ValueTask DisposeAsync(){await _stop.CancelAsync();_listener.Stop();await _accept;await Task.WhenAll(_clients);_stop.Dispose();}
}
