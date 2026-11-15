using HybridMount.Commons.Presentation;
using System.Globalization;
namespace HybridMount.UI.Localization;

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
