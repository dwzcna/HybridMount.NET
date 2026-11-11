using System.Globalization;
using System.Text.Json;
namespace HybridMount.Services.Localization;

/// <summary>纯资源提供者；语言明确指定，不修改日期、数字、协议或线程区域格式。</summary>
public interface IUiTextProvider { string? Read(string language,string key); }
/// <summary>消息保留资源键和参数，原始用户名称、路径、服务错误作为参数或Raw保留。语言刷新不重新发布业务消息。</summary>
public sealed record ResourceMessage
{
    public string? Key {get;}
    public IReadOnlyList<object?> Arguments {get;}
    private readonly string? _raw;
    private ResourceMessage(string? key,object?[] args,string? raw){Key=key;Arguments=Array.AsReadOnly(args);_raw=raw;}
    /// <summary>创建明确的资源消息；参数复制为只读快照，嵌套消息随当前语言渲染。</summary>
    public static ResourceMessage Create(string key,params object?[] args)=>new(key,args.ToArray(),null);
    /// <summary>原始文本不参与资源匹配，适用于服务器原文、用户输入和已有日志。</summary>
    public static ResourceMessage Raw(string? text)=>new(null,[],text??"");
    public string Render()=>Key is null?_raw??"":UiText.Text(Key,Arguments.Select(x=>x is ResourceMessage message?message.Render():x).ToArray());
    public override string ToString()=>Render();
}
/// <summary>随产品保存的资源契约；Source仅作诊断回退与旧业务消息的模板识别，实际显示优先使用PRI。</summary>
internal sealed record UiTemplate(string Source,int[] LocalizableArguments);
internal static class UiCatalog
{
    internal static readonly IReadOnlyDictionary<string,UiTemplate> Templates=Load();
    private static IReadOnlyDictionary<string,UiTemplate> Load()
    {
        using var stream=typeof(UiCatalog).Assembly.GetManifestResourceStream("HybridMount.Resources.UiCatalog.json")??throw new InvalidOperationException("UI catalog missing");
        return JsonSerializer.Deserialize<Dictionary<string,UiTemplate>>(stream)??throw new InvalidOperationException("UI catalog invalid");
    }
}
/// <summary>创建界面前的安全回退；生产初始化后由MRT／PRI提供三种语言，测试可注入独立资源读取器。</summary>
internal sealed class CatalogTextProvider:IUiTextProvider
{
    public string? Read(string language,string key)=>UiCatalog.Templates.TryGetValue(key,out var template)?template.Source:null;
}
