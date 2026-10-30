using System.Runtime.InteropServices;

namespace HybridMount.Services.Mounting;

/// <summary>协议层真实传输字节及整文件退化原因；不含远端地址、授权头或凭据。</summary>
public sealed record SourceReadDiagnostic(string Strategy,long DownloadedBytes,string? FallbackReason,bool Completed=true);
/// <summary>可选的只读诊断事件；不改变协议能力或读取结果。</summary>
public interface IReadDiagnosticsSource
{
    event Action<SourceReadDiagnostic>? ReadDiagnostic;
}
/// <summary>回调返回前复制进程信息；仅保留 PID 和可执行文件名，绝不读取命令行。</summary>
public sealed record HydrationProcess(uint Id,string Image)
{
    /// <summary>由 CFAPI 回调同步调用；结构长度不足时返回未知进程。</summary>
    public static HydrationProcess Copy(nint pointer)
    {
        if(pointer==0||Marshal.ReadInt32(pointer)<16)return new(0,"unknown");
        var pid=unchecked((uint)Marshal.ReadInt32(pointer,4));
        var image=Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,8));
        return new(pid,string.IsNullOrEmpty(image)?"unknown":Path.GetFileName(image));
    }
    /// <summary>通过启动环境变量显式启用详细水合诊断，默认关闭。</summary>
    public static bool DiagnosticsEnabled=>Environment.GetEnvironmentVariable("HYBRIDMOUNT_HYDRATION_DIAGNOSTICS")=="1";
}
