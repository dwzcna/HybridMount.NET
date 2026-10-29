using System.Diagnostics;

namespace HybridMount.Services;

/// <summary>托盘和退出的兜底诊断；日志订阅异常不得跨越原生回调边界。</summary>
internal static class LifecycleTraceService
{
    /// <summary>将生命周期问题交给产品日志，初始化或清理阶段同时写调试输出。</summary>
    internal static void Record(string message, string? details = null)
    {
        var text = details is null ? message : $"{message}：{details}";
        Debug.WriteLine(text);
        try { App.Current.Logger.Queue("生命周期", "Warning", text); }
        catch (Exception error) { Debug.WriteLine(error); }
    }
}
