using HybridMount.Commons.Presentation;
using System.Globalization;
namespace HybridMount.Commons.Localization;

/// <summary>XAML PRI 属性资源契约；C# 文案直接使用标准 ResourceManager。</summary>
public interface IXamlPropertyProvider
{
    string? Read(string language, string key);
    IReadOnlyDictionary<string, string> PropertyResources(string id);
}
