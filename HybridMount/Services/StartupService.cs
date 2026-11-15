using Microsoft.Win32;
using global::Windows.ApplicationModel;

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
            state = State(enabled, true, enabled ? global::HybridMount.Services.Localization.ResourceMessage.Create("StartAtSignInRegisteredForTheCurrentUserDevelopmentRun") : global::HybridMount.Services.Localization.ResourceMessage.Create("StartAtSignInIsOff"));
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
            actual = State(enabled, true, enabled ? global::HybridMount.Services.Localization.ResourceMessage.Create("StartAtSignInRegisteredForTheCurrentUserDevelopmentRun") : global::HybridMount.Services.Localization.ResourceMessage.Create("StartAtSignInIsOff"));
        }
        SaveActual(actual.Enabled);
        logger.Queue("启动", "Information", actual.Detail);
        return actual;
    }
    /// <summary>有序退出期间只移除当前模式启动入口；不保存偏好或发日志，系统策略仍启用时停止还原。</summary>
    internal static async Task ClearForSettingsResetAsync(bool packaged,string productScope)
    {
        if(productScope!="HybridMount.NET."+(packaged?"Packaged":"Unpackaged")||packaged!=WinRegisterService.IsPackaged())throw new IOException(global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("TheDeploymentOrDataFoldersHaveChangedCheckAndConfirmAgain", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!);
        if(packaged)
        {
            var task=await StartupTask.GetAsync(TaskId);task.Disable();
            if(task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)throw new IOException(global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("SystemPolicyStillEnablesThisAppAtSignInResetCannotComplete", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!);
        }
        using var key=Registry.CurrentUser.OpenSubKey(RunPath,true);key?.DeleteValue(productScope,false);
    }
    private void SaveActual(bool enabled)
    {
        try { WinRegisterService.WriteFlag("StartupEnabled", enabled); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { logger.Queue("启动", "Warning", $"启动项状态已由系统确定，但偏好缓存保存失败：{error.GetType().Name}"); }
    }
    private static StartupState State(bool enabled,bool canToggle,global::HybridMount.Services.Localization.ResourceMessage message)
        =>new(enabled,canToggle,message.Render()){DisplayMessage=message};
    private static StartupState Describe(StartupTaskState state) => state switch
    {
        StartupTaskState.Enabled => State(true, true, global::HybridMount.Services.Localization.ResourceMessage.Create("StartSilentlyInTheTrayAfterSigningInToWindows")),
        StartupTaskState.EnabledByPolicy => State(true, false, global::HybridMount.Services.Localization.ResourceMessage.Create("StartAtSignInIsEnabledBySystemPolicy")),
        StartupTaskState.DisabledByPolicy => State(false, false, global::HybridMount.Services.Localization.ResourceMessage.Create("StartAtSignInIsDisabledBySystemPolicy")),
        StartupTaskState.DisabledByUser => State(false, false, global::HybridMount.Services.Localization.ResourceMessage.Create("AllowThisApplicationAgainInWindowsStartupAppSettings")),
        _ => State(false, true, global::HybridMount.Services.Localization.ResourceMessage.Create("StartAtSignInIsOff"))
    };
}
