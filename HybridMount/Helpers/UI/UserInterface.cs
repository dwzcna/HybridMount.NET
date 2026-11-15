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
public static class UserInterface
{
    private static readonly DependencyProperty StateProperty=DependencyProperty.RegisterAttached("State",typeof(State),typeof(UserInterface),new PropertyMetadata(null));
    private static State For(DependencyObject target){if(target.GetValue(StateProperty) is State value)return value;var state=new State(target);target.SetValue(StateProperty,state);return state;}
    /// <summary>与x:Uid相同的资源前缀；只登记本地化属性，语言变化时从当前PRI原位更新。</summary>
    public static readonly DependencyProperty ResourceIdProperty=DependencyProperty.RegisterAttached("ResourceId",typeof(string),typeof(UserInterface),new PropertyMetadata(null,(target,args)=>For(target).SetResourceId(args.NewValue as string)));
    public static string GetResourceId(DependencyObject target)=>target.GetValue(ResourceIdProperty) as string??"";
    public static void SetResourceId(DependencyObject target,string value)=>target.SetValue(ResourceIdProperty,value);
    /// <summary>布局在测量尚未Loaded的标签前刷新当前语言；不改写用户输入、选择和消息开关。</summary>
    internal static void RefreshResources(DependencyObject target)=>For(target).RefreshResources();
    /// <summary>绑定DataTemplate的原展示对象；只读指定计算属性，路径／名称／ID不应使用本入口。</summary>
    public static readonly DependencyProperty SourceProperty=DependencyProperty.RegisterAttached("Source",typeof(object),typeof(UserInterface),new PropertyMetadata(null,(target,args)=>For(target).SetSource(args.NewValue)));
    public static object? GetSource(DependencyObject target)=>target.GetValue(SourceProperty);
    public static void SetSource(DependencyObject target,object? value)=>target.SetValue(SourceProperty,value);
    /// <summary>语言或模型变化时重读既有Text展示属性。</summary>
    public static readonly DependencyProperty PathProperty=DependencyProperty.RegisterAttached("Path",typeof(string),typeof(UserInterface),new PropertyMetadata(null,(target,args)=>For(target).SetPath("Text",args.NewValue as string)));
    public static string GetPath(DependencyObject target)=>target.GetValue(PathProperty) as string??"";
    public static void SetPath(DependencyObject target,string value)=>target.SetValue(PathProperty,value);
    /// <summary>语言或模型变化时重读既有ToolTip展示属性。</summary>
    public static readonly DependencyProperty ToolTipPathProperty=DependencyProperty.RegisterAttached("ToolTipPath",typeof(string),typeof(UserInterface),new PropertyMetadata(null,(target,args)=>For(target).SetPath("ToolTip",args.NewValue as string)));
    public static string GetToolTipPath(DependencyObject target)=>target.GetValue(ToolTipPathProperty) as string??"";
    public static void SetToolTipPath(DependencyObject target,string value)=>target.SetValue(ToolTipPathProperty,value);
    /// <summary>语言或模型变化时重读既有AutomationName展示属性。</summary>
    public static readonly DependencyProperty AutomationNamePathProperty=DependencyProperty.RegisterAttached("AutomationNamePath",typeof(string),typeof(UserInterface),new PropertyMetadata(null,(target,args)=>For(target).SetPath("AutomationName",args.NewValue as string)));
    public static string GetAutomationNamePath(DependencyObject target)=>target.GetValue(AutomationNamePathProperty) as string??"";
    public static void SetAutomationNamePath(DependencyObject target,string value)=>target.SetValue(AutomationNamePathProperty,value);
    /// <summary>语言或模型变化时重读既有Content展示属性。</summary>
    public static readonly DependencyProperty ContentPathProperty=DependencyProperty.RegisterAttached("ContentPath",typeof(string),typeof(UserInterface),new PropertyMetadata(null,(target,args)=>For(target).SetPath("Content",args.NewValue as string)));
    public static string GetContentPath(DependencyObject target)=>target.GetValue(ContentPathProperty) as string??"";
    public static void SetContentPath(DependencyObject target,string value)=>target.SetValue(ContentPathProperty,value);
    /// <summary>程序化对话框在显示前保留完整资源消息；用户数据和服务原文作为原始参数。</summary>
    public static T Configure<T>(T target,params (string Property,ResourceMessage Message)[] values)where T:DependencyObject
    {foreach(var value in values)For(target).SetMessage(value.Property,value.Message);return target;}
    /// <summary>重读纯展示计算，不重建控件或触发业务动作；工厂与目标生命周期一起释放。</summary>
    public static T Bind<T>(T target,string property,Func<ResourceMessage> read)where T:DependencyObject{For(target).SetFactory(property,read);return target;}
    internal static void ClearMessage(DependencyObject target,string property)=>For(target).ClearMessage(property);
    public static void Set(DependencyObject target,string property,string text)=>For(target).SetMessage(property,global::HybridMount.Services.Localization.ResourceMessage.Raw(text));
    public static void Set(DependencyObject target,string property,ResourceMessage message)=>For(target).SetMessage(property,message);
    /// <summary>所有应用弹窗共用尺寸约束；显示失败、关闭或取消后都解除窗口尺寸订阅。</summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        try
        {
            DialogLayout.Prepare(dialog);
            return await dialog.ShowAsync();
        }
        finally { DialogLayout.Release(dialog); }
    }
    /// <summary>仅重读已有x:Bind，切换前后保持现有滚动位置；返回句柄可与页面生命周期一起释放。</summary>
    public static IDisposable ObserveLanguage(FrameworkElement target,Action update){var state=For(target);state.Callback=update;return state;}
    private static readonly ConcurrentDictionary<(Type,string),DependencyProperty> Properties=new();
    private static void Write(DependencyObject target,string property,string text)
    {
        // WinUI Run.Text是普通WinRT属性，没有TextProperty；任务DataTemplate中的Run必须走实际setter。
        if(property=="Text"&&target is Microsoft.UI.Xaml.Documents.Run run){run.Text=text;return;}
        if(property=="ToolTip"){ToolTipService.SetToolTip(target,text);return;}if(property=="AutomationName"){AutomationProperties.SetName(target,text);return;}if(property=="HelpText"){AutomationProperties.SetHelpText(target,text);return;}
        var dp=Properties.GetOrAdd((target.GetType(),property),item=>(item.Item1.GetProperty(item.Item2+"Property",BindingFlags.Public|BindingFlags.Static|BindingFlags.FlattenHierarchy)?.GetValue(null)??item.Item1.GetField(item.Item2+"Property",BindingFlags.Public|BindingFlags.Static|BindingFlags.FlattenHierarchy)?.GetValue(null)) as DependencyProperty??throw new InvalidOperationException("UI property missing: "+item.Item1.Name+"."+item.Item2));
        target.SetValue(dp,text);
    }
    /// <summary>控件首次登记时固定系统区域输入；后续 UI 语言变化只刷新标签，不重置数字、日期或正在输入的文本。</summary>
    private static void PreserveRegionalInput(DependencyObject target)
    {
        var culture=System.Globalization.CultureInfo.CurrentCulture;
        if(target is NumberBox number)
        {
            string region;
            try{region=new System.Globalization.RegionInfo(culture.Name).TwoLetterISORegionName;}
            catch(ArgumentException){region=new global::Windows.Globalization.GeographicRegion().CodeTwoLetter;}
            var original=number.NumberFormatter as global::Windows.Globalization.NumberFormatting.DecimalFormatter;
            if(original is not null)
            {
                var formatter=new global::Windows.Globalization.NumberFormatting.DecimalFormatter(new[]{culture.Name},region)
                {
                    IntegerDigits=original.IntegerDigits,FractionDigits=original.FractionDigits,IsGrouped=original.IsGrouped,
                    IsDecimalPointAlwaysDisplayed=original.IsDecimalPointAlwaysDisplayed,IsZeroSigned=original.IsZeroSigned,
                    NumberRounder=original.NumberRounder
                };
                number.NumberFormatter=formatter;
            }
            number.Language=culture.Name;
        }
        else if(target is DatePicker date)date.Language=culture.Name;
        else if(target is CalendarDatePicker calendar)calendar.Language=culture.Name;
    }
    private sealed class State:IDisposable
    {
        private readonly WeakReference<DependencyObject> _target;private readonly DispatcherQueue _queue;private readonly IDisposable _language;
        private readonly Dictionary<string,ResourceMessage> _messages=new(StringComparer.Ordinal);private readonly Dictionary<string,string> _paths=new(StringComparer.Ordinal);
        private readonly Dictionary<string,Func<ResourceMessage>> _factories=new(StringComparer.Ordinal);
        private readonly Dictionary<string,string> _propertyResources=new(StringComparer.Ordinal);
        /// <summary>仅替换此UID拥有的属性；属性清单来自PRI，不保存第二份翻译文本。</summary>
        internal void SetResourceId(string? id)
        {
            _propertyResources.Clear();
            if (!string.IsNullOrEmpty(id))
                foreach (var pair in UILanguageService.Active.PropertyResources(id)) _propertyResources.Add(pair.Key, pair.Value);
            Refresh(false);
        }
        internal void RefreshResources()=>Refresh(false);
        internal void ClearMessage(string property){_messages.Remove(property);_factories.Remove(property);}
        internal void SetFactory(string property,Func<ResourceMessage> read){if(_disposed)return;_messages.Remove(property);_factories[property]=read;Refresh(false);}
        private object? _source;private INotifyPropertyChanged? _notifier;private bool _disposed,_loaded;private long _revision;
        public Action? Callback;
        internal State(DependencyObject target)
        {
            PreserveRegionalInput(target);
            _target=new(target);_queue=target.DispatcherQueue;_loaded=target is not FrameworkElement element||element.IsLoaded;
            if(target is FrameworkElement view){view.Loaded+=Loaded;view.Unloaded+=Unloaded;}_language=UILanguageService.Observe(this,static state=>state.QueueRefresh());
        }
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
        /// <summary>通知可以先于模板的 Loaded 回调到达；只在目标 Dispatcher 上核对真实加载状态，再刷新现有控件。</summary>
        private void QueueRefresh()
        {
            if (_disposed) return;
            if (_queue.HasThreadAccess) { RefreshLoadedTarget(); return; }
            var revision = _revision;
            _queue.TryEnqueue(() =>
            {
                if (!_disposed && revision == _revision) RefreshLoadedTarget();
            });
        }
        private void RefreshLoadedTarget()
        {
            if (_disposed || !_target.TryGetTarget(out var target)) return;
            var loaded = target is not FrameworkElement element || element.IsLoaded;
            if (_loaded != loaded)
            {
                _loaded = loaded;
                Detach();
                if (loaded) Attach();
            }
            if (loaded) Refresh(true);
        }
        private void Refresh(bool language)
        {
            if(_disposed||!_target.TryGetTarget(out var target))return;
            List<(ScrollViewer Viewer,double X,double Y)>? scrolls=null;if(language&&Callback is not null)scrolls=Scrolls(target);
            // 界面语言只决定资源；Language 保持系统区域，避免日期和 NumberBox 输入随界面语言改变。
            var culture = UILanguageService.ResourceCulture;
            foreach (var (property,key) in _propertyResources)
                if (!_messages.ContainsKey(property) && !_factories.ContainsKey(property)) Write(target, property, UILanguageService.Active.PropertyText(key, culture));
            foreach(var (property,message) in _messages)Write(target,property,message.Render(culture));
            foreach(var (property,read) in _factories)Write(target,property,read().Render(culture));
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
