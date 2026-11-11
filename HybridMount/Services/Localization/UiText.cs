namespace HybridMount.Services.Localization;

/// <summary>所有展示共用当前资源服务；弱订阅不把关闭的页面、对话框或消息留在全局单例中。</summary>
public static class UiText
{
    private static UiLanguageService _current=new(new MemoryUiLanguageStore(),new CatalogTextProvider());
    private static readonly object Gate=new();private static readonly List<IObserver> Observers=[];
    static UiText()=>_current.Changed+=Changed;
    public static UiLanguageService Current=>Volatile.Read(ref _current);
    public static string Text(string key,params object?[] args)=>Current.Format(key,args);
    public static ResourceMessage Message(string key,params object?[] args)
    {
        var values=args.ToArray();if(UiCatalog.Templates.TryGetValue(key,out var template))foreach(var index in template.LocalizableArguments)if(index<values.Length&&values[index] is string text)values[index]=Describe(text);return ResourceMessage.Create(key,values);
    }
    public static ResourceMessage Describe(string? text)=>Current.Describe(text);
    /// <summary>仅展示自有已知业务文字时使用；用户名、路径、服务原文及日志保持直接显示或Raw。</summary>
    public static string Legacy(string? text)=>Describe(text).Render();
    /// <summary>应用创建XAML前安装正式服务；测试可在隔离作用域还原原服务。</summary>
    public static IDisposable Use(UiLanguageService service)
    {
        var previous=Current;previous.Changed-=Changed;Volatile.Write(ref _current,service);service.Changed+=Changed;Notify();return new Restore(previous,service);
    }
    private sealed class Restore(UiLanguageService previous,UiLanguageService installed):IDisposable
    {
        private int _disposed;public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;installed.Changed-=Changed;Volatile.Write(ref _current,previous);previous.Changed+=Changed;Notify();}
    }
    private interface IObserver{bool Invoke();}
    private sealed class Observer<T>(T target,Action<T> update):IObserver,IDisposable where T:class
    {
        private readonly WeakReference<T> _target=new(target);private bool _disposed;
        public bool Invoke(){if(_disposed||!_target.TryGetTarget(out var target))return false;update(target);return true;}
        public void Dispose(){lock(Gate){_disposed=true;Observers.Remove(this);}}
    }
    public static IDisposable Observe<T>(T target,Action<T> update)where T:class
    {var observer=new Observer<T>(target,update);lock(Gate)Observers.Add(observer);return observer;}
    private static void Changed(object? sender,EventArgs args)=>Notify();
    private static void Notify()
    {
        IObserver[] snapshot;lock(Gate)snapshot=Observers.ToArray();foreach(var observer in snapshot)try{if(!observer.Invoke())lock(Gate)Observers.Remove(observer);}catch(Exception error){System.Diagnostics.Trace.TraceWarning("Localized display: 0x{0:X8}",error.HResult);}
    }
}
