using HybridMount.Models.UI;
using HybridMount;
using HybridMount.ViewModels;
using HybridMount.Views.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace HybridMount.UI;

/// <summary>页面消息保留独立状态，进入可视树时交给主窗口右下角InfoBar呈现，离开页面解除订阅。</summary>
public static class PageMessagePresentation
{
    public static InfoBarSeverity Severity(PageMessageSeverity value)=>value switch{PageMessageSeverity.Success=>InfoBarSeverity.Success,PageMessageSeverity.Warning=>InfoBarSeverity.Warning,PageMessageSeverity.Error=>InfoBarSeverity.Error,_=>InfoBarSeverity.Informational};
    public static readonly DependencyProperty MessageProperty=DependencyProperty.RegisterAttached("Message",typeof(PageStatusViewModel),typeof(PageMessagePresentation),new PropertyMetadata(null,MessageChanged));
    private static readonly DependencyProperty RegistrationProperty=DependencyProperty.RegisterAttached("Registration",typeof(object),typeof(PageMessagePresentation),new PropertyMetadata(null));
    public static PageStatusViewModel? GetMessage(DependencyObject owner)=>(PageStatusViewModel?)owner.GetValue(MessageProperty);
    public static void SetMessage(DependencyObject owner,PageStatusViewModel? message)=>owner.SetValue(MessageProperty,message);
    private static void MessageChanged(DependencyObject owner,DependencyPropertyChangedEventArgs args)
    {
        (owner.GetValue(RegistrationProperty) as IDisposable)?.Dispose();owner.ClearValue(RegistrationProperty);
        if(owner is FrameworkElement element&&args.NewValue is PageStatusViewModel message)owner.SetValue(RegistrationProperty,new Registration(element,message));
    }
    /// <summary>只跟随可视生命周期；导航不发布新消息、不清空正文，也不重开已经关闭的提示。</summary>
    private sealed class Registration:IDisposable
    {
        private readonly FrameworkElement _element;private readonly PageStatusViewModel _message;private IDisposable? _visible;
        internal Registration(FrameworkElement element,PageStatusViewModel message){_element=element;_message=message;element.Loaded+=Loaded;element.Unloaded+=Unloaded;if(element.IsLoaded)Attach();}
        private void Loaded(object sender,RoutedEventArgs args)=>Attach();
        private void Attach(){_visible?.Dispose();_visible=App.Current.MainWindow?.RegisterPageMessage(_message);}
        private void Unloaded(object sender,RoutedEventArgs args){_visible?.Dispose();_visible=null;}
        public void Dispose(){_element.Loaded-=Loaded;_element.Unloaded-=Unloaded;_visible?.Dispose();_visible=null;}
    }
}
