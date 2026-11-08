using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
namespace HybridMount.Services.Mounting;

/// <summary>缓存缺失只异步请求已运行的同映像后台尝试本地生成；不激活主程序、不发送旧远端预览操作。</summary>
internal sealed class LocalPreviewRequestClient:IAsyncDisposable
{
    private readonly Channel<(string Path,int Size)> _queue=Channel.CreateBounded<(string,int)>(new BoundedChannelOptions(64){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    private readonly ConcurrentDictionary<string,long> _recent=new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop=new();private readonly Task _worker;
    internal LocalPreviewRequestClient(){_worker=Task.Run(WorkAsync);}
    internal void Queue(string path,int size)
    {
        if(_stop.IsCancellationRequested||size is <16 or >256||!Path.IsPathFullyQualified(path)||path.StartsWith("\\",StringComparison.Ordinal))return;
        var now=Environment.TickCount64;var key=path+"|"+size;
        if(_recent.TryGetValue(key,out var time)&&now-time<2000)return;
        foreach(var row in _recent.Where(x=>now-x.Value>2000))_recent.TryRemove(row.Key,out _);
        if(_recent.Count>=128||!_recent.TryAdd(key,now))return;if(!_queue.Writer.TryWrite((path,size)))_recent.TryRemove(key,out _);
    }
    private async Task WorkAsync()
    {
        try
        {
            await foreach(var request in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);deadline.CancelAfter(500);
                    await using var pipe=new NamedPipeClientStream(".",IpcProtocol.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                    await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    // 在发送任何文件路径之前检查同一会话和同一部署映像；SID由CurrentUserOnly管道握手同时约束。
                    if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out var pid))continue;
                    using var peer=Process.GetProcessById(checked((int)pid));using var self=Process.GetCurrentProcess();
                    if(peer.SessionId!=self.SessionId||!string.Equals(peer.MainModule?.FileName,Environment.ProcessPath,StringComparison.OrdinalIgnoreCase))continue;
                    var message=new IpcRequest(2,Guid.NewGuid().ToString("N"),"requestLocalPreview",[request.Path],PreviewSize:request.Size);
                    await IpcProtocol.WriteAsync(pipe,message,deadline.Token).ConfigureAwait(false);var response=await IpcProtocol.ReadAsync<IpcResponse>(pipe,deadline.Token).ConfigureAwait(false);
                    if(response.Version!=2||response.RequestId!=message.RequestId)continue;
                }
                catch(Exception error)when(error is IOException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or System.Text.Json.JsonException){}
            }
        }
        catch(OperationCanceledException)when(_stop.IsCancellationRequested){}
    }
    public async ValueTask DisposeAsync(){_queue.Writer.TryComplete();await _stop.CancelAsync().ConfigureAwait(false);await _worker.ConfigureAwait(false);_stop.Dispose();}
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint process);
}
