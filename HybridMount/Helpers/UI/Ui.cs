using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using HybridMount.Services.Localization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace HybridMount.Helpers.UI;

/// <summary>在现有控件上更新文字，不重建页面或改写用户输入。每个状态由目标持有，全局只弱订阅，卸载与释放后拒绝迟到刷新。</summary>
public static class Ui
{
    private static readonly DependencyProperty StateProperty=DependencyProperty.RegisterAttached("State",typeof(State),typeof(Ui),new PropertyMetadata(null));
    private static State For(DependencyObject target){if(target.GetValue(StateProperty) is State value)return value;var state=new State(target);target.SetValue(StateProperty,state);return state;}
    /// <summary>以资源键更新Text，不触发业务提交。</summary>
    public static readonly DependencyProperty TextKeyProperty=DependencyProperty.RegisterAttached("TextKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("Text",args.NewValue as string)));
    public static string GetTextKey(DependencyObject target)=>target.GetValue(TextKeyProperty) as string??"";
    public static void SetTextKey(DependencyObject target,string value)=>target.SetValue(TextKeyProperty,value);
    /// <summary>以资源键更新Label，不触发业务提交。</summary>
    public static readonly DependencyProperty LabelKeyProperty=DependencyProperty.RegisterAttached("LabelKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("Label",args.NewValue as string)));
    public static string GetLabelKey(DependencyObject target)=>target.GetValue(LabelKeyProperty) as string??"";
    public static void SetLabelKey(DependencyObject target,string value)=>target.SetValue(LabelKeyProperty,value);
    /// <summary>以资源键更新Content，不触发业务提交。</summary>
    public static readonly DependencyProperty ContentKeyProperty=DependencyProperty.RegisterAttached("ContentKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("Content",args.NewValue as string)));
    public static string GetContentKey(DependencyObject target)=>target.GetValue(ContentKeyProperty) as string??"";
    public static void SetContentKey(DependencyObject target,string value)=>target.SetValue(ContentKeyProperty,value);
    /// <summary>以资源键更新Header，不触发业务提交。</summary>
    public static readonly DependencyProperty HeaderKeyProperty=DependencyProperty.RegisterAttached("HeaderKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("Header",args.NewValue as string)));
    public static string GetHeaderKey(DependencyObject target)=>target.GetValue(HeaderKeyProperty) as string??"";
    public static void SetHeaderKey(DependencyObject target,string value)=>target.SetValue(HeaderKeyProperty,value);
    /// <summary>以资源键更新Description，不触发业务提交。</summary>
    public static readonly DependencyProperty DescriptionKeyProperty=DependencyProperty.RegisterAttached("DescriptionKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("Description",args.NewValue as string)));
    public static string GetDescriptionKey(DependencyObject target)=>target.GetValue(DescriptionKeyProperty) as string??"";
    public static void SetDescriptionKey(DependencyObject target,string value)=>target.SetValue(DescriptionKeyProperty,value);
    /// <summary>以资源键更新PlaceholderText，不触发业务提交。</summary>
    public static readonly DependencyProperty PlaceholderTextKeyProperty=DependencyProperty.RegisterAttached("PlaceholderTextKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("PlaceholderText",args.NewValue as string)));
    public static string GetPlaceholderTextKey(DependencyObject target)=>target.GetValue(PlaceholderTextKeyProperty) as string??"";
    public static void SetPlaceholderTextKey(DependencyObject target,string value)=>target.SetValue(PlaceholderTextKeyProperty,value);
    /// <summary>以资源键更新Title，不触发业务提交。</summary>
    public static readonly DependencyProperty TitleKeyProperty=DependencyProperty.RegisterAttached("TitleKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("Title",args.NewValue as string)));
    public static string GetTitleKey(DependencyObject target)=>target.GetValue(TitleKeyProperty) as string??"";
    public static void SetTitleKey(DependencyObject target,string value)=>target.SetValue(TitleKeyProperty,value);
    /// <summary>以资源键更新Message，不触发业务提交。</summary>
    public static readonly DependencyProperty MessageKeyProperty=DependencyProperty.RegisterAttached("MessageKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("Message",args.NewValue as string)));
    public static string GetMessageKey(DependencyObject target)=>target.GetValue(MessageKeyProperty) as string??"";
    public static void SetMessageKey(DependencyObject target,string value)=>target.SetValue(MessageKeyProperty,value);
    /// <summary>以资源键更新PrimaryButtonText，不触发业务提交。</summary>
    public static readonly DependencyProperty PrimaryButtonTextKeyProperty=DependencyProperty.RegisterAttached("PrimaryButtonTextKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("PrimaryButtonText",args.NewValue as string)));
    public static string GetPrimaryButtonTextKey(DependencyObject target)=>target.GetValue(PrimaryButtonTextKeyProperty) as string??"";
    public static void SetPrimaryButtonTextKey(DependencyObject target,string value)=>target.SetValue(PrimaryButtonTextKeyProperty,value);
    /// <summary>以资源键更新SecondaryButtonText，不触发业务提交。</summary>
    public static readonly DependencyProperty SecondaryButtonTextKeyProperty=DependencyProperty.RegisterAttached("SecondaryButtonTextKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("SecondaryButtonText",args.NewValue as string)));
    public static string GetSecondaryButtonTextKey(DependencyObject target)=>target.GetValue(SecondaryButtonTextKeyProperty) as string??"";
    public static void SetSecondaryButtonTextKey(DependencyObject target,string value)=>target.SetValue(SecondaryButtonTextKeyProperty,value);
    /// <summary>以资源键更新CloseButtonText，不触发业务提交。</summary>
    public static readonly DependencyProperty CloseButtonTextKeyProperty=DependencyProperty.RegisterAttached("CloseButtonTextKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("CloseButtonText",args.NewValue as string)));
    public static string GetCloseButtonTextKey(DependencyObject target)=>target.GetValue(CloseButtonTextKeyProperty) as string??"";
    public static void SetCloseButtonTextKey(DependencyObject target,string value)=>target.SetValue(CloseButtonTextKeyProperty,value);
    /// <summary>以资源键更新OnContent，不触发业务提交。</summary>
    public static readonly DependencyProperty OnContentKeyProperty=DependencyProperty.RegisterAttached("OnContentKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("OnContent",args.NewValue as string)));
    public static string GetOnContentKey(DependencyObject target)=>target.GetValue(OnContentKeyProperty) as string??"";
    public static void SetOnContentKey(DependencyObject target,string value)=>target.SetValue(OnContentKeyProperty,value);
    /// <summary>以资源键更新OffContent，不触发业务提交。</summary>
    public static readonly DependencyProperty OffContentKeyProperty=DependencyProperty.RegisterAttached("OffContentKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("OffContent",args.NewValue as string)));
    public static string GetOffContentKey(DependencyObject target)=>target.GetValue(OffContentKeyProperty) as string??"";
    public static void SetOffContentKey(DependencyObject target,string value)=>target.SetValue(OffContentKeyProperty,value);
    /// <summary>以资源键更新ToolTip，不触发业务提交。</summary>
    public static readonly DependencyProperty ToolTipKeyProperty=DependencyProperty.RegisterAttached("ToolTipKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("ToolTip",args.NewValue as string)));
    public static string GetToolTipKey(DependencyObject target)=>target.GetValue(ToolTipKeyProperty) as string??"";
    public static void SetToolTipKey(DependencyObject target,string value)=>target.SetValue(ToolTipKeyProperty,value);
    /// <summary>以资源键更新AutomationName，不触发业务提交。</summary>
    public static readonly DependencyProperty AutomationNameKeyProperty=DependencyProperty.RegisterAttached("AutomationNameKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("AutomationName",args.NewValue as string)));
    public static string GetAutomationNameKey(DependencyObject target)=>target.GetValue(AutomationNameKeyProperty) as string??"";
    public static void SetAutomationNameKey(DependencyObject target,string value)=>target.SetValue(AutomationNameKeyProperty,value);
    /// <summary>以资源键更新HelpText，不触发业务提交。</summary>
    public static readonly DependencyProperty HelpTextKeyProperty=DependencyProperty.RegisterAttached("HelpTextKey",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetKey("HelpText",args.NewValue as string)));
    public static string GetHelpTextKey(DependencyObject target)=>target.GetValue(HelpTextKeyProperty) as string??"";
    public static void SetHelpTextKey(DependencyObject target,string value)=>target.SetValue(HelpTextKeyProperty,value);
    /// <summary>绑定DataTemplate的原展示对象；只读指定计算属性，路径／名称／ID不应使用本入口。</summary>
    public static readonly DependencyProperty SourceProperty=DependencyProperty.RegisterAttached("Source",typeof(object),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetSource(args.NewValue)));
    public static object? GetSource(DependencyObject target)=>target.GetValue(SourceProperty);
    public static void SetSource(DependencyObject target,object? value)=>target.SetValue(SourceProperty,value);
    /// <summary>语言或模型变化时重读既有Text展示属性。</summary>
    public static readonly DependencyProperty PathProperty=DependencyProperty.RegisterAttached("Path",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetPath("Text",args.NewValue as string)));
    public static string GetPath(DependencyObject target)=>target.GetValue(PathProperty) as string??"";
    public static void SetPath(DependencyObject target,string value)=>target.SetValue(PathProperty,value);
    /// <summary>语言或模型变化时重读既有ToolTip展示属性。</summary>
    public static readonly DependencyProperty ToolTipPathProperty=DependencyProperty.RegisterAttached("ToolTipPath",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetPath("ToolTip",args.NewValue as string)));
    public static string GetToolTipPath(DependencyObject target)=>target.GetValue(ToolTipPathProperty) as string??"";
    public static void SetToolTipPath(DependencyObject target,string value)=>target.SetValue(ToolTipPathProperty,value);
    /// <summary>语言或模型变化时重读既有AutomationName展示属性。</summary>
    public static readonly DependencyProperty AutomationNamePathProperty=DependencyProperty.RegisterAttached("AutomationNamePath",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetPath("AutomationName",args.NewValue as string)));
    public static string GetAutomationNamePath(DependencyObject target)=>target.GetValue(AutomationNamePathProperty) as string??"";
    public static void SetAutomationNamePath(DependencyObject target,string value)=>target.SetValue(AutomationNamePathProperty,value);
    /// <summary>语言或模型变化时重读既有Content展示属性。</summary>
    public static readonly DependencyProperty ContentPathProperty=DependencyProperty.RegisterAttached("ContentPath",typeof(string),typeof(Ui),new PropertyMetadata(null,(target,args)=>For(target).SetPath("Content",args.NewValue as string)));
    public static string GetContentPath(DependencyObject target)=>target.GetValue(ContentPathProperty) as string??"";
    public static void SetContentPath(DependencyObject target,string value)=>target.SetValue(ContentPathProperty,value);
    /// <summary>兼容程序化创建控件；只捕获已有字符串属性，绝不把UIElement内容转成文字或跟踪输入框正文。</summary>
    public static T Watch<T>(T target,params string[] properties)where T:DependencyObject
    {foreach(var property in properties)if(!For(target).Has(property)&&Read(target,property) is string text)For(target).SetMessage(property,UiText.Describe(text));return target;}
    /// <summary>程序化对话框在显示前保留完整资源消息；用户数据和服务原文作为原始参数。</summary>
    public static T Configure<T>(T target,params (string Property,ResourceMessage Message)[] values)where T:DependencyObject
    {foreach(var value in values)For(target).SetMessage(value.Property,value.Message);return target;}
    /// <summary>重读纯展示计算，不重建控件或触发业务动作；工厂与目标生命周期一起释放。</summary>
    public static T Bind<T>(T target,string property,Func<ResourceMessage> read)where T:DependencyObject{For(target).SetFactory(property,read);return target;}
    public static void Set(DependencyObject target,string property,string text)=>For(target).SetMessage(property,UiText.Describe(text));
    public static void Set(DependencyObject target,string property,ResourceMessage message)=>For(target).SetMessage(property,message);
    public static void TrackDialog(ContentDialog dialog)=>Watch(dialog,"Title","PrimaryButtonText","SecondaryButtonText","CloseButtonText");
    public static global::Windows.Foundation.IAsyncOperation<ContentDialogResult> ShowAsync(ContentDialog dialog){TrackDialog(dialog);return dialog.ShowAsync();}
    /// <summary>仅重读已有x:Bind，切换前后保持现有滚动位置；返回句柄可与页面生命周期一起释放。</summary>
    public static IDisposable ObserveLanguage(FrameworkElement target,Action update){var state=For(target);state.Callback=update;return state;}
    private static object? Read(DependencyObject target,string property)=>property switch
    {"ToolTip"=>ToolTipService.GetToolTip(target),"AutomationName"=>AutomationProperties.GetName(target),"HelpText"=>AutomationProperties.GetHelpText(target),_=>target.GetType().GetProperty(property)?.GetValue(target)};
    private static readonly ConcurrentDictionary<(Type,string),DependencyProperty> Properties=new();
    private static void Write(DependencyObject target,string property,string text)
    {
        // WinUI Run.Text是普通WinRT属性，没有TextProperty；任务DataTemplate中的Run必须走实际setter。
        if(property=="Text"&&target is Microsoft.UI.Xaml.Documents.Run run){run.Text=text;return;}
        if(property=="ToolTip"){ToolTipService.SetToolTip(target,text);return;}if(property=="AutomationName"){AutomationProperties.SetName(target,text);return;}if(property=="HelpText"){AutomationProperties.SetHelpText(target,text);return;}
        var dp=Properties.GetOrAdd((target.GetType(),property),item=>(item.Item1.GetProperty(item.Item2+"Property",BindingFlags.Public|BindingFlags.Static|BindingFlags.FlattenHierarchy)?.GetValue(null)??item.Item1.GetField(item.Item2+"Property",BindingFlags.Public|BindingFlags.Static|BindingFlags.FlattenHierarchy)?.GetValue(null)) as DependencyProperty??throw new InvalidOperationException("UI property missing: "+item.Item1.Name+"."+item.Item2));
        target.SetValue(dp,text);
    }
    private sealed class State:IDisposable
    {
        private readonly WeakReference<DependencyObject> _target;private readonly DispatcherQueue _queue;private readonly IDisposable _language;
        private readonly Dictionary<string,ResourceMessage> _messages=new(StringComparer.Ordinal);private readonly Dictionary<string,string> _paths=new(StringComparer.Ordinal);
        private readonly Dictionary<string,Func<ResourceMessage>> _factories=new(StringComparer.Ordinal);
        internal bool Has(string property)=>_messages.ContainsKey(property)||_factories.ContainsKey(property);
        internal void SetFactory(string property,Func<ResourceMessage> read){if(_disposed)return;_messages.Remove(property);_factories[property]=read;Refresh(false);}
        private object? _source;private INotifyPropertyChanged? _notifier;private bool _disposed,_loaded;private long _revision;
        public Action? Callback;
        internal State(DependencyObject target)
        {
            _target=new(target);_queue=target.DispatcherQueue;_loaded=target is not FrameworkElement element||element.IsLoaded;
            if(target is FrameworkElement view){view.Loaded+=Loaded;view.Unloaded+=Unloaded;}_language=UiText.Observe(this,static state=>state.QueueRefresh());
        }
        internal void SetKey(string property,string? key){if(string.IsNullOrEmpty(key))_messages.Remove(property);else SetMessage(property,ResourceMessage.Create(key));}
        internal void SetMessage(string property,ResourceMessage message)
        {
            if(_disposed||!_target.TryGetTarget(out var target))return;
            // 可编辑内容只保留原赋值行为，语言刷新不得覆盖用户草稿。
            if(property=="Text"&&target is TextBox or NumberBox){Write(target,property,message.Render());return;}
            _messages[property]=message;Write(target,property,message.Render());
        }
        internal void SetPath(string property,string? path){if(string.IsNullOrEmpty(path))_paths.Remove(property);else _paths[property]=path;Refresh(false);}
        internal void SetSource(object? source){if(ReferenceEquals(source,_source))return;Detach();_source=source;Attach();Refresh(false);}
        private void Attach(){if(_loaded&&_source is INotifyPropertyChanged notifier){_notifier=notifier;notifier.PropertyChanged+=Changed;}}
        private void Detach(){if(_notifier is {} previous){_notifier=null;previous.PropertyChanged-=Changed;}}
        private void Changed(object? sender,PropertyChangedEventArgs args){if(string.IsNullOrEmpty(args.PropertyName)||_paths.Values.Any(path=>path.Split('.')[0]==args.PropertyName))QueueRefresh();}
        private void Loaded(object sender,RoutedEventArgs args){if(_disposed)return;_loaded=true;Detach();Attach();Refresh(true);}
        private void Unloaded(object sender,RoutedEventArgs args){_loaded=false;++_revision;Detach();}
        private void QueueRefresh()
        {
            if(_disposed||!_loaded)return;if(_queue.HasThreadAccess){Refresh(true);return;}var revision=_revision;_queue.TryEnqueue(()=>{if(!_disposed&&_loaded&&revision==_revision)Refresh(true);});
        }
        private void Refresh(bool language)
        {
            if(_disposed||!_target.TryGetTarget(out var target))return;
            List<(ScrollViewer Viewer,double X,double Y)>? scrolls=null;if(language&&Callback is not null)scrolls=Scrolls(target);
            if(language&&Callback is not null&&target is FrameworkElement element)element.Language=UiText.Current.Current.Effective;
            foreach(var (property,message) in _messages)Write(target,property,message.Render());
            foreach(var (property,read) in _factories)Write(target,property,read().Render());
            foreach(var (property,path) in _paths){object? value=_source;foreach(var name in path.Split('.'))value=value?.GetType().GetProperty(name)?.GetValue(value);Write(target,property,value is ResourceMessage message?message.Render():value?.ToString()??"");}
            if(language)Callback?.Invoke();if(scrolls is {Count:>0}){var revision=++_revision;_queue.TryEnqueue(DispatcherQueuePriority.Low,()=>{if(_disposed||!_loaded||revision!=_revision)return;foreach(var item in scrolls)item.Viewer.ChangeView(item.X,item.Y,null,true);});}
        }
        private static List<(ScrollViewer,double,double)> Scrolls(DependencyObject target)
        {
            var result=new List<(ScrollViewer,double,double)>();var pending=new Stack<DependencyObject>();pending.Push(target);var count=0;
            while(pending.Count>0&&++count<=4096){var node=pending.Pop();if(node is ScrollViewer viewer)result.Add((viewer,viewer.HorizontalOffset,viewer.VerticalOffset));for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)pending.Push(VisualTreeHelper.GetChild(node,i));}return result;
        }
        public void Dispose(){if(_disposed)return;_disposed=true;++_revision;_language.Dispose();Detach();Callback=null;_factories.Clear();if(_target.TryGetTarget(out var target)&&target is FrameworkElement element){element.Loaded-=Loaded;element.Unloaded-=Unloaded;}}
    }
}
