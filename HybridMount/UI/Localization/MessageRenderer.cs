using System.Globalization;
using HybridMount.Models.Localization;
namespace HybridMount.UI.Localization;

/// <summary>唯一语义消息渲染；嵌套参数共用语言快照，数字日期仍使用系统区域。</summary>
internal static class MessageRenderer
{
    internal static string Render(ResourceMessage message,CultureInfo? language=null)
    {
        if(message.Key is not {} key)return message.RawText??"";
        var culture=language??UILanguageService.ResourceCulture;
        var template=UILanguageService.Messages.GetString(key,culture)??
            throw new System.Resources.MissingManifestResourceException("Message resource missing: "+key);
        return string.Format(CultureInfo.CurrentCulture,template,message.Arguments.Select(value=>
            value is ResourceMessage nested?Render(nested,culture):value).ToArray());
    }
}
