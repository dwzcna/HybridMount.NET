using System.IO.Pipes;
using System.Text.Json;

namespace HybridMount.Services;

/// <summary>本用户本会话的四路异步占位服务；退出取消所有连接并等待已接纳处理。</summary>
public sealed class IpcServerService : IAsyncDisposable
{
    private readonly LoggerService _logger;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task[]? _workers;
    private Task? _dispose;
    /// <summary>请求已经验证，订阅方负责切换到 UI 线程；不得据此修改文件。</summary>
    public event Action<IpcRequest>? RequestReceived;
    /// <summary>创建服务；测试可指定独立管道，不接触产品实例。</summary>
    public IpcServerService(LoggerService logger, string? pipeName = null)
    {
        _logger = logger;
        _pipeName = pipeName ?? IpcProtocol.PipeName;
    }
    /// <summary>启动四个有界连接处理器，初始化失败同步返回。</summary>
    public void Start()
    {
        if (_workers is not null) throw new InvalidOperationException("管道服务已启动。");
        var servers = new List<NamedPipeServerStream>();
        try
        {
            for (var index = 0; index < 4; index++) servers.Add(CreateServer());
            _workers = servers.Select(server => Task.Run(() => ServeAsync(server))).ToArray();
        }
        catch { foreach (var server in servers) server.Dispose(); throw; }
    }
    private NamedPipeServerStream CreateServer() => new(_pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
    private async Task ServeAsync(NamedPipeServerStream initial)
    {
        NamedPipeServerStream? next = initial;
        while (!_stop.IsCancellationRequested)
        {
            await using var server = next ?? CreateServer();
            next = null;
            try
            {
                await server.WaitForConnectionAsync(_stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await IpcProtocol.ReadAsync<IpcRequest>(server, deadline.Token);
                var response = Handle(request);
                await IpcProtocol.WriteAsync(server, response, deadline.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException)
            { _logger.Queue("通信", "Warning", $"管道请求未完成：{error.GetType().Name}"); }
        }
        if (next is not null) await next.DisposeAsync();
    }
    private IpcResponse Handle(IpcRequest request)
    {
        if (!IpcProtocol.IsValid(request)) return new(IpcProtocol.Version, request.RequestId ?? "", "invalidRequest", "请求版本、操作或路径无效。");
        if (request.Operation is "freeSpace" or "keepOnDevice")
        {
            var title = request.Operation == "freeSpace" ? "释放空间" : "始终保留在设备上";
            _logger.Queue("资源管理器", "Information", $"{title}（占位）：{JsonSerializer.Serialize(request.Paths)}；0.0.1 不修改文件。");
        }
        if (RequestReceived is { } subscribers)
            foreach (Action<IpcRequest> subscriber in subscribers.GetInvocationList())
                try { subscriber(request); }
                catch (Exception error) { _logger.Queue("通信", "Warning", $"请求通知失败：{error.GetType().Name}"); }
        return request.Operation switch
        {
            "ping" => new(IpcProtocol.Version, request.RequestId, "ready", "HybridMount.NET 已运行。"),
            "showWindow" => new(IpcProtocol.Version, request.RequestId, "accepted", "已请求恢复窗口。"),
            _ => new(IpcProtocol.Version, request.RequestId, "notImplemented", "0.0.1 为占位菜单，操作已记录，未修改文件。")
        };
    }
    /// <summary>重复释放共用同一任务，结束管道后由宿主排空日志。</summary>
    public ValueTask DisposeAsync() => new(_dispose ??= StopAsync());
    private async Task StopAsync()
    {
        await _stop.CancelAsync();
        if (_workers is not null) await Task.WhenAll(_workers);
        _stop.Dispose();
    }
}
