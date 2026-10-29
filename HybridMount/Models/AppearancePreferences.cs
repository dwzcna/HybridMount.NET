using global::System.Text.Json;

namespace HybridMount.Models;

/// <summary>DWORD 持久化使用稳定数字；None是首次安装默认，系统材质回退不改变此选择。</summary>
public enum AppearanceBackdrop { None = 0, MicaBase = 1, MicaAltBaseAlt = 2, AcrylicBase = 3, AcrylicThin = 4 }
/// <summary>System保留系统动态跟随，不持久化当前实际明暗状态。</summary>
public enum AppearanceTheme { System = 0, Light = 1, Dark = 2 }

/// <summary>两项外观偏好的不可变正式快照；JSON 方法仅保留纯快照校验入口，不用于注册表读取。</summary>
public sealed record AppearancePreferences(AppearanceBackdrop Backdrop, AppearanceTheme Theme)
{
    public static AppearancePreferences Default { get; } = new(AppearanceBackdrop.None, AppearanceTheme.System);
    /// <summary>只接受明确稳定字符串，不接受数字或未知枚举。</summary>
    public static bool TryBackdrop(string? value, out AppearanceBackdrop backdrop)
    {
        backdrop = value switch
        {
            "MicaBase" => AppearanceBackdrop.MicaBase, "MicaAltBaseAlt" => AppearanceBackdrop.MicaAltBaseAlt,
            "AcrylicBase" => AppearanceBackdrop.AcrylicBase, "AcrylicThin" => AppearanceBackdrop.AcrylicThin,
            _ => AppearanceBackdrop.None
        };
        return value is "None" or "MicaBase" or "MicaAltBaseAlt" or "AcrylicBase" or "AcrylicThin";
    }
    /// <summary>只接受正式主题名称，Default等实现细节不能写入用户设置。</summary>
    public static bool TryTheme(string? value, out AppearanceTheme theme)
    {
        theme = value switch { "Light" => AppearanceTheme.Light, "Dark" => AppearanceTheme.Dark, _ => AppearanceTheme.System };
        return value is "System" or "Light" or "Dark";
    }
    /// <summary>缺失或非法字段各自取默认；另一项合法偏好保持，读取不回写。</summary>
    /// <param name="raw">原始REG_SZ；缺值或错误类型视为非法。</param>
    /// <returns>合法正式快照。</returns>
    public static AppearancePreferences Parse(object? raw)
    {
        if (raw is not string json) return Default;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Default;
            string? Read(string name)
            {
                var values = document.RootElement.EnumerateObject().Where(property => property.Name == name).ToArray();
                return values.Length == 1 && values[0].Value.ValueKind == JsonValueKind.String ? values[0].Value.GetString() : null;
            }
            TryBackdrop(Read("backdrop"), out var backdrop); TryTheme(Read("theme"), out var theme);
            return new(backdrop, theme);
        }
        catch (JsonException) { return Default; }
    }
    /// <summary>写前拒绝非法枚举；使用已知稳定名称，避免反射序列化及AOT元数据依赖。</summary>
    /// <returns>完整两项设置的JSON。</returns>
    public string ToJson()
    {
        if (!TryBackdrop(Backdrop.ToString(), out _) || !TryTheme(Theme.ToString(), out _))
            throw new ArgumentOutOfRangeException(nameof(Backdrop), "外观设置包含未知枚举。");
        return $"{{\"backdrop\":\"{Backdrop}\",\"theme\":\"{Theme}\"}}";
    }
}
