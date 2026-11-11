using HybridMount.ViewModels;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Helpers.UI;
/// <summary>UI只映射业务反馈等级，不解析正文；映射不改变.60消息打开或关闭状态。</summary>
public static class PageMessagePresentation
{
    public static InfoBarSeverity Severity(PageMessageSeverity value)=>value switch{PageMessageSeverity.Success=>InfoBarSeverity.Success,PageMessageSeverity.Warning=>InfoBarSeverity.Warning,PageMessageSeverity.Error=>InfoBarSeverity.Error,_=>InfoBarSeverity.Informational};
}
