namespace HybridMount.Models.Mounting;

/// <summary>预览范围结果；不支持与有效的文件末尾零字节分开表达。</summary>
public readonly record struct PreviewRangeResult(bool Supported,int Bytes);
/// <summary>独立工作进程的有界消息；只传媒体类型、长度和字节，不包含远端地址或凭据。</summary>
public sealed record PreviewWorkerPacket(string Kind,long Offset=0,int Count=0,byte[]? Data=null,string? Extension=null,long Length=0,Dictionary<int,byte[]>? Frames=null);
