namespace HybridMount.Models;

/// <summary>Windows 实际启动项状态，注册表缓存不能替代此结果。</summary>
/// <param name="Enabled">系统是否允许登录启动。</param><param name="CanToggle">应用内是否允许切换。</param><param name="Detail">限制或当前状态的说明。</param>
public sealed record StartupState(bool Enabled, bool CanToggle, string Detail)
{
    /// <summary>可选语义消息只用于本次运行的展示；系统状态和日志正文不依赖当前语言。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public global::HybridMount.Services.Localization.ResourceMessage? DisplayMessage { get; init; }
}
