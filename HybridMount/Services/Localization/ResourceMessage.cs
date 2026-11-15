using System.Globalization;
namespace HybridMount.Services.Localization;

/// <summary>XAML PRI 属性资源契约；C# 文案直接使用标准 ResourceManager。</summary>
public interface IXamlPropertyProvider
{
    string? Read(string language, string key);
    IReadOnlyDictionary<string, string> PropertyResources(string id);
}
/// <summary>转换 RESW 属性分隔点；using 命名空间括号内的点保持原样。</summary>
internal static class ResourceNames
{
    internal static string ToPriPath(string key)
    {
        var depth = 0; var result = new System.Text.StringBuilder(key.Length);
        foreach (var character in key)
        {
            if (character == '[') depth++; else if (character == ']') depth--;
            result.Append(character == '.' && depth == 0 ? '/' : character);
        }
        return result.ToString();
    }
}
/// <summary>只保存语义键和参数或 Raw 原文；用户、路径和第三方错误不反向匹配资源。</summary>
public sealed record ResourceMessage
{
    public string? Key { get; }
    public IReadOnlyList<object?> Arguments { get; }
    private readonly string? _raw;
    private ResourceMessage(string? key, object?[] arguments, string? raw)
    { Key = key; Arguments = Array.AsReadOnly(arguments); _raw = raw; }
    public static ResourceMessage Create(string key, params object?[] arguments) => new(key, arguments.ToArray(), null);
    public static ResourceMessage Raw(string? text) => new(null, [], text ?? "");
    /// <summary>嵌套消息共用一次 UI 语言快照；参数格式保持系统区域。</summary>
    public string Render(CultureInfo? language = null)
    {
        if (Key is null) return _raw ?? "";
        var culture = language ?? UILanguageService.ResourceCulture;
        var template = UILanguageService.Messages.GetString(Key, culture) ?? throw new System.Resources.MissingManifestResourceException("Message resource missing: " + Key);
        return string.Format(CultureInfo.CurrentCulture, template, Arguments.Select(value => value is ResourceMessage message ? message.Render(culture) : value).ToArray());
    }
    public override string ToString() => Render();
}
