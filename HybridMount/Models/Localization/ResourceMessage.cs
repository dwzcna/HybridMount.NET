using HybridMount.Commons.Presentation;
using System.Globalization;
namespace HybridMount.Models.Localization;
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
    internal string? RawText => _raw;
    /// <summary>通过Commons展示契约渲染，模型不引用资源管理器或具体界面服务。</summary>
    public string Render(CultureInfo? language=null) => ModelPresentation.Render(this,"Message",language);
    public override string ToString() => Render();
}
