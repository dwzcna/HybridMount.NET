using System.Runtime.InteropServices;
using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>CFAPI 回调的同步复制及诊断开关；模型仅持有已经复制的进程数据。</summary>
internal static class HydrationDiagnostics
{
    /// <summary>由 CFAPI 回调同步调用；结构长度不足时返回未知进程。</summary>
    public static HydrationProcess CopyProcess(nint pointer)
    {
        if(pointer==0||Marshal.ReadInt32(pointer)<16)return new(0,"unknown");
        var pid=unchecked((uint)Marshal.ReadInt32(pointer,4));
        var image=Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer,8));
        return new(pid,string.IsNullOrEmpty(image)?"unknown":Path.GetFileName(image));
    }
    /// <summary>通过启动环境变量显式启用详细水合诊断，默认关闭。</summary>
    public static bool Enabled=>Environment.GetEnvironmentVariable("HYBRIDMOUNT_HYDRATION_DIAGNOSTICS")=="1";
}
