namespace HybridMount.Models.Mounting;

/// <summary>真实传输策略与字节，不包含地址、授权头或凭据。</summary>
public sealed record SourceReadDiagnostic(string Strategy,long DownloadedBytes,string? FallbackReason,bool Completed=true);
/// <summary>回调生命周期内复制后的进程身份；不保留原生指针或命令行。</summary>
public sealed record HydrationProcess(uint Id,string Image);
