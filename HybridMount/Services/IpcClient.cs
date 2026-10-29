using System.IO.Pipes;

namespace HybridMount.Services;

/// <summary>重复启动时向已有实例发送激活请求，使用统一超时和关联校验。</summary>
public static class IpcClient
{
    /// <summary>向同用户同会话实例发送一条消息。</summary>
    public static async Task<IpcResponse> SendAsync(IpcRequest request, string? pipeName = null, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await using var pipe = new NamedPipeClientStream(".", pipeName ?? IpcProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        await IpcProtocol.WriteAsync(pipe, request, deadline.Token);
        var response = await IpcProtocol.ReadAsync<IpcResponse>(pipe, deadline.Token);
        if (response.Version != IpcProtocol.Version || response.RequestId != request.RequestId) throw new InvalidDataException("IPC 响应关联失败。");
        return response;
    }
}
