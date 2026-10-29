using HybridMount.Models;

namespace HybridMount.Common;

/// <summary>外观持久化窄接口；纯测试替换存储，不替代状态提交或失败保留规则。</summary>
public interface IAppearancePreferenceStore
{
    /// <summary>读取完整偏好；缺失或非法字段独立取默认，访问失败抛出而不冒充缺值。</summary>
    /// <returns>保留所有合法字段的偏好快照。</returns>
    AppearancePreferences Read();
    /// <summary>同步保存完整偏好；失败尽力回滚并抛出，调用方不得发布候选。</summary>
    /// <param name="preferences">已校验的背景及主题快照。</param>
    void Write(AppearancePreferences preferences);
}
