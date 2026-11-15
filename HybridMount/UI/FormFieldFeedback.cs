using HybridMount.Controls;
using HybridMount.Models.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
namespace HybridMount.UI;
/// <summary>表单字段就近错误，校验只用现有草稿，不读取文件或访问网络；聚焦前展开包含目标的高级区。</summary>
public sealed class FormFieldFeedback:IDisposable
{
    private readonly List<Action> _undo=[];private Control? _first;private long _generation;private bool _disposed;
    /// <summary>新校验先逆序恢复字段说明，并拒绝上轮排队的焦点更新。</summary>
    public void Begin(){++_generation;for(var i=_undo.Count-1;i>=0;i--)_undo[i]();_undo.Clear();_first=null;}
    /// <summary>未知错误保留原文；明确资源消息走独立重载，语言切换不修改字段输入。</summary>
    public void Check(bool valid,Control field,string message)=>Check(valid,field,ResourceMessage.Raw(message));
    public void Check(bool valid,Control field,ResourceMessage message)
    {
        if(valid||_disposed)return;_first??=field;var previous=AutomationProperties.GetHelpText(field);UserInterface.Set(field,"HelpText",message);_undo.Add(()=>{UserInterface.ClearMessage(field,"HelpText");AutomationProperties.SetHelpText(field,previous);});
        var text=UserInterface.Configure(new TextBlock{TextWrapping=TextWrapping.Wrap,Style=(Style)Application.Current.Resources["FieldErrorTextStyle"]},("Text",message));AutomationProperties.SetLiveSetting(text,AutomationLiveSetting.Polite);
        switch(field)
        {
            case TextBox box:var oldText=box.Description;box.Description=text;_undo.Add(()=>box.Description=oldText);break;
            case PasswordBox password:var oldPassword=password.Description;password.Description=text;_undo.Add(()=>password.Description=oldPassword);break;
            case ComboBox combo:var oldCombo=combo.Description;combo.Description=text;_undo.Add(()=>combo.Description=oldCombo);break;
            case NumberBox number:var oldNumber=number.Description;number.Description=text;_undo.Add(()=>number.Description=oldNumber);break;
            default:if(field.Parent is StackPanel panel){var index=panel.Children.IndexOf(field);panel.Children.Insert(index+1,text);_undo.Add(()=>panel.Children.Remove(text));}break;
        }
    }
    public bool Complete(DependencyObject root)
    {
        if(_disposed||_first is not {} field)return true;Expand(root,field,0);var generation=_generation;
        void Focus(){if(_disposed||generation!=_generation)return;if(root is FrameworkElement element)element.UpdateLayout();field.Focus(FocusState.Programmatic);field.StartBringIntoView(new BringIntoViewOptions{AnimationDesired=false});}
        Focus();field.DispatcherQueue.TryEnqueue(Focus);return false;
    }
    private static bool Expand(DependencyObject current,Control target,int depth)
    {
        if(ReferenceEquals(current,target))return true;if(depth>=64)return false;
        IEnumerable<DependencyObject> children=current switch
        {
            HybridMount.Controls.SettingRow row when row.Setting is DependencyObject setting=>[setting],
            UserControl user when user.Content is DependencyObject content=>[content],
            Expander expander when expander.Content is DependencyObject content=>[content],
            ContentControl control when control.Content is DependencyObject content=>[content],
            ContentPresenter presenter when presenter.Content is DependencyObject content=>[content],
            Border border when border.Child is DependencyObject child=>[child],
            Panel panel=>panel.Children.Cast<DependencyObject>(),_=>[]
        };
        foreach(var child in children)if(Expand(child,target,depth+1)){if(current is Expander expander)expander.IsExpanded=true;return true;}return false;
    }
    /// <summary>关闭时清理临时说明并拒绝排队更新。</summary>
    public void Dispose(){if(_disposed)return;_disposed=true;Begin();}
}
