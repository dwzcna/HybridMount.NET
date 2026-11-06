using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace HybridMount.Services;

/// <summary>原生扩展与应用共用的有界协议：四字节小端长度后跟 UTF-8 JSON。</summary>
public static class IpcProtocol
{
    /// <summary>首版传输版本，与产品源码版本分离。</summary>
    public const int Version = 2;
    /// <summary>单帧上限，避免异常输入造成无界分配。</summary>
    public const int MaximumFrameBytes = 1024 * 1024;
    /// <summary>单次文件系统选择上限，超限明确拒绝而不截断。</summary>
    public const int MaximumPaths = 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16 };
    /// <summary>按用户与登录会话隔离的管道名，和原生端保持一致。</summary>
    public static string PipeName => $"HybridMount.NET.v2.{WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("缺少用户 SID")}.{Process.GetCurrentProcess().SessionId}";

    public static string LegacyPipeName => PipeName.Replace(".v2.",".v1.");
    /// <summary>验证请求结构；文件命令不探测或修改所选路径。</summary>
    public static bool IsValid(IpcRequest request)
    {
        if (request.Version != Version || !Guid.TryParse(request.RequestId, out _) || request.Paths is null) return false;
        if (request.Operation is "getOperation" or "cancelOperation") return request.Paths.Length == 0 && Guid.TryParse(request.JobId,out _);
        if (request.Operation is "ping" or "showWindow") return request.Paths.Length == 0;
        if(request.Operation is "getPreview" or "requestPreview")return request.Paths.Length==1 && request.PreviewSize is >=16 and <=256 &&
            !string.IsNullOrWhiteSpace(request.Paths[0])&&request.Paths[0].Length<=32767&&!request.Paths[0].Contains('\0')&&Path.IsPathFullyQualified(request.Paths[0]);
        if (request.Operation is not ("freeSpace" or "keepOnDevice" or "showSyncIssues") || request.Paths.Length is < 1 or > MaximumPaths) return false;
        return request.Paths.All(path => !string.IsNullOrWhiteSpace(path) && path.Length <= 32767 && !path.Contains('\0') && Path.IsPathFullyQualified(path));
    }
    /// <summary>读取一条完整帧；截断、零长度、过长或非法 JSON 均失败。</summary>
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token = default)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaximumFrameBytes) throw new InvalidDataException("IPC 帧长度无效。");
        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            await stream.ReadExactlyAsync(buffer.AsMemory(0, (int)length), token);
            return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, (int)length), JsonOptions)
                ?? throw new InvalidDataException("IPC 消息为空。");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }
    /// <summary>写出长度和完整响应，校验序列化后的实际帧大小。</summary>
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length is 0 or > MaximumFrameBytes) throw new InvalidDataException("IPC 消息过长。");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
}
