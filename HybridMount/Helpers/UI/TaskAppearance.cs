using HybridMount.Models.Mounting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.Helpers.UI;

/// <summary>按结构化任务状态选择系统列表项样式；只改变左侧选择指示条与浅色底，不改变文字色。</summary>
public sealed class TaskAppearance : StyleSelector
{
    public Style? Normal {get;set;}
    public Style? Error {get;set;}
    public Style? Conflict {get;set;}
    protected override Style SelectStyleCore(object item,DependencyObject container)=>Choose(item);
    private Style Choose(object item)=>(item is MountOperationSnapshot value?value.IssueKind:TaskIssueKind.Normal) switch
    {TaskIssueKind.Conflict=>Conflict!,TaskIssueKind.Error=>Error!,_=>Normal!};
}
