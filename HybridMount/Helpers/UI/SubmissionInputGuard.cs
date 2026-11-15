using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
namespace HybridMount.Helpers.UI;

/// <summary>提交期间观察现有控件的输入变化；任何变化立即取消尚未移交的认证，包含改后改回。UI线程创建和释放。</summary>
internal sealed class SubmissionInputGuard:IDisposable
{
    private readonly List<Action> _detach=[];
    internal SubmissionInputGuard(DependencyObject root,Action invalidate)
    {
        void Walk(DependencyObject node)
        {
            switch(node)
            {
                case TextBox text:TextChangedEventHandler changed=(_,_)=>invalidate();text.TextChanged+=changed;_detach.Add(()=>text.TextChanged-=changed);break;
                case PasswordBox password:RoutedEventHandler secret=(_,_)=>invalidate();password.PasswordChanged+=secret;_detach.Add(()=>password.PasswordChanged-=secret);break;
                case NumberBox number:global::Windows.Foundation.TypedEventHandler<NumberBox,NumberBoxValueChangedEventArgs> value=(_,_)=>invalidate();number.ValueChanged+=value;_detach.Add(()=>number.ValueChanged-=value);break;
                case ComboBox combo:SelectionChangedEventHandler selection=(_,_)=>invalidate();combo.SelectionChanged+=selection;_detach.Add(()=>combo.SelectionChanged-=selection);break;
                case ToggleSwitch toggle:RoutedEventHandler toggled=(_,_)=>invalidate();toggle.Toggled+=toggled;_detach.Add(()=>toggle.Toggled-=toggled);break;
                case ToggleButton button:RoutedEventHandler check=(_,_)=>invalidate();button.Checked+=check;button.Unchecked+=check;button.Indeterminate+=check;_detach.Add(()=>{button.Checked-=check;button.Unchecked-=check;button.Indeterminate-=check;});break;
            }
            for(var index=0;index<VisualTreeHelper.GetChildrenCount(node);index++)Walk(VisualTreeHelper.GetChild(node,index));
        }
        Walk(root);
    }
    public void Dispose(){foreach(var detach in _detach)detach();_detach.Clear();}
}
