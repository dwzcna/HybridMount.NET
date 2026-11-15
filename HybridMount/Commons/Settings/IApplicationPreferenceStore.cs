namespace HybridMount.Commons.Settings;

/// <summary>当前运行模式的生命周期、密度和云并发偏好；访问错误不能当作缺失。</summary>
public interface IApplicationPreferenceStore
{
    bool ReadCloseToTray();
    void WriteCloseToTray(bool value);
    bool ReadCompactLayout();
    void WriteCompactLayout(bool value);
    object? ReadCloudConcurrency();
    void WriteCloudConcurrency(int value);
}
