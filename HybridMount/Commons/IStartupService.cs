namespace HybridMount.Commons;

/// <summary>登录启动平台入口，返回系统状态而非候选开关值。</summary>
public interface IStartupService
{
    /// <summary>查询系统实际注册和策略状态。</summary>
    Task<StartupState> GetAsync();
    /// <summary>修改当前用户登录启动，返回实际生效结果。</summary>
    Task<StartupState> SetAsync(bool enabled);
}
