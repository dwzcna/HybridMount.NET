using Microsoft.Win32;
using Windows.ApplicationModel;

namespace HybridMount.Services;

/// <summary>从参考工程迁移的 StartupTask／当前用户 Run 入口，不安装系统服务。</summary>
public sealed class StartupService(LoggerService logger) : IStartupService
{
    /// <summary>与本产品 MSIX 清单保持一致的独立启动标识。</summary>
    public const string TaskId = "HybridMountStartup";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string RunValue => ApplicationPaths.ProductScope;
    private static string StartupCommand => $"\"{Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用路径。")}\" --tray";

    /// <summary>查询已打包的系统状态，或开发运行的当前用户注册。</summary>
    public async Task<StartupState> GetAsync()
    {
        StartupState state;
        if (WinRegisterService.IsPackaged()) state = Describe((await StartupTask.GetAsync(TaskId)).State);
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            var enabled = key?.GetValue(RunValue) is string command && string.Equals(command, StartupCommand, StringComparison.OrdinalIgnoreCase);
            state = new(enabled, true, enabled ? "已注册当前用户登录启动（开发运行）。" : "登录启动已关闭。");
        }
        SaveActual(state.Enabled);
        return state;
    }
    /// <summary>只管理本产品启动项；用户或系统策略禁用时不绕过 Windows。</summary>
    public async Task<StartupState> SetAsync(bool enabled)
    {
        StartupState actual;
        if (WinRegisterService.IsPackaged())
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled) actual = Describe(await task.RequestEnableAsync());
            else { task.Disable(); actual = Describe(task.State); }
        }
        else
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath, writable: true) ?? throw new IOException("无法访问当前用户启动项。");
            if (enabled) key.SetValue(RunValue, StartupCommand, RegistryValueKind.String);
            else key.DeleteValue(RunValue, false);
            actual = new(enabled, true, enabled ? "已注册当前用户登录启动（开发运行）。" : "登录启动已关闭。");
        }
        SaveActual(actual.Enabled);
        logger.Queue("启动", "Information", actual.Detail);
        return actual;
    }
    private void SaveActual(bool enabled)
    {
        try { WinRegisterService.WriteFlag("StartupEnabled", enabled); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { logger.Queue("启动", "Warning", $"启动项状态已由系统确定，但偏好缓存保存失败：{error.GetType().Name}"); }
    }
    private static StartupState Describe(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => new(true, true, "登录 Windows 后静默进入托盘。"),
        StartupTaskState.EnabledByPolicy => new(true, false, "登录启动已由系统策略启用。"),
        StartupTaskState.DisabledByPolicy => new(false, false, "登录启动已被系统策略禁用。"),
        StartupTaskState.DisabledByUser => new(false, false, "请在 Windows 的启动应用设置中重新允许此应用。"),
        _ => new(false, true, "登录启动已关闭。")
    };
}
