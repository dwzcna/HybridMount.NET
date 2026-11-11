using HybridMount.Models.Mounting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using HybridMount.ViewModels;
namespace HybridMount.Helpers.UI;

/// <summary>按结构化任务状态选择系统列表项样式；只改变左侧选择指示条与浅色底，不改变文字色。</summary>
public sealed class TaskAppearance : StyleSelector
{
    public Style? Normal {get;set;}
    public Style? Error {get;set;}
    public Style? Conflict {get;set;}
    protected override Style SelectStyleCore(object item,DependencyObject container)=>Choose(item);
    private Style Choose(object item)=>(item is TaskListItem row?row.IssueKind:item is MountOperationSnapshot value?value.IssueKind:TaskIssueKind.Normal) switch
    {TaskIssueKind.Conflict=>Conflict!,TaskIssueKind.Error=>Error!,_=>Normal!};
    /// <summary>状态变化时复用原容器并更新标头／浅底；StyleSelector只在创建时执行，因此另跟踪实际类别。</summary>
    public static readonly DependencyProperty KindProperty=DependencyProperty.RegisterAttached("Kind",typeof(TaskIssueKind),typeof(TaskAppearance),new PropertyMetadata(TaskIssueKind.Normal,Changed));
    public static TaskIssueKind GetKind(DependencyObject target)=>(TaskIssueKind)target.GetValue(KindProperty);
    public static void SetKind(DependencyObject target,TaskIssueKind value)=>target.SetValue(KindProperty,value);
    private static void Changed(DependencyObject target,DependencyPropertyChangedEventArgs args){if(target is FrameworkElement element){element.Loaded-=Loaded;element.Loaded+=Loaded;}Apply(target);}
    private static void Loaded(object sender,RoutedEventArgs args)=>Apply((DependencyObject)sender);
    private static void Apply(DependencyObject target)
    {
        var kind=GetKind(target);for(var cursor=VisualTreeHelper.GetParent(target);cursor is not null;cursor=VisualTreeHelper.GetParent(cursor))
            if(cursor is ListViewItem container){if(ItemsControl.ItemsControlFromItemContainer(container) is ListView {ItemContainerStyleSelector:TaskAppearance styles})container.Style=kind==TaskIssueKind.Conflict?styles.Conflict:kind==TaskIssueKind.Error?styles.Error:styles.Normal;return;}
    }
}
