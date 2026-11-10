namespace HybridMount.Services;

/// <summary>可验证的 Frame 边界；生产适配器使用系统导航和历史，不自行创建页面。</summary>
internal interface INavigationFrame : IDisposable
{
    event Action<Type,object?>? Navigated;
    event Action<Exception>? Failed;
    bool Navigate(Type pageType,object? parameter);
    bool GoBack();
    bool CanGoBack {get;}
}

/// <summary>应用范围的 UI 线程导航入口；Frame 拥有历史和缓存，服务统一管理成功通知与退出释放。</summary>
public sealed partial class NavigationService : INavigationService,IAsyncDisposable
{
    private INavigationFrame? _frame;
    private IReadOnlyDictionary<ApplicationPage,Type> _pageTypes=new Dictionary<ApplicationPage,Type>();
    private Action<Exception>? _report;
    private readonly List<object> _visited=[];
    private readonly HashSet<object> _known=new(ReferenceEqualityComparer.Instance),_stopped=new(ReferenceEqualityComparer.Instance),_released=new(ReferenceEqualityComparer.Instance);
    private readonly List<Exception> _stopFailures=[];
    private bool _navigating,_stopping,_requestFailed;
    private Task? _releaseWork;
    public ApplicationPage? Current {get;private set;}
    public bool CanGoBack=>!_stopping&&_frame?.CanGoBack==true;
    public event Action<ApplicationPage>? Navigated;

    /// <summary>可在窗口创建前注入页面模型；注册 Frame 之前不接受导航。</summary>
    public NavigationService(Action<Exception>? report=null)=>_report=report;
    internal NavigationService(INavigationFrame frame,IReadOnlyDictionary<ApplicationPage,Type> pageTypes,Action<Exception>? report=null):this(report)=>RegisterFrame(frame,pageTypes);

    private void EnsureCanRegister()
    {
        if(_stopping)throw new InvalidOperationException("程序正在退出。");
        if(_frame is not null)throw new InvalidOperationException("导航已绑定。");
    }
    /// <summary>只登记一次完整的页面表，保留测试替身与 WinUI 适配器共同的入口。</summary>
    internal void RegisterFrame(INavigationFrame frame,IReadOnlyDictionary<ApplicationPage,Type> pageTypes,Action<Exception>? report=null)
    {
        EnsureCanRegister();ArgumentNullException.ThrowIfNull(frame);ArgumentNullException.ThrowIfNull(pageTypes);
        var pages=Enum.GetValues<ApplicationPage>();
        if(pageTypes.Count!=pages.Length||pageTypes.Values.Any(type=>type is null)||pageTypes.Values.Distinct().Count()!=pages.Length||pages.Any(page=>!pageTypes.ContainsKey(page)))
            throw new ArgumentException("导航表必须包含全部五个唯一页面。",nameof(pageTypes));
        _frame=frame;_pageTypes=new Dictionary<ApplicationPage,Type>(pageTypes);_report=report??_report;
        frame.Navigated+=FrameNavigated;frame.Failed+=FrameFailed;
    }
    /// <summary>重复当前页不增加历史；非法枚举、重入和停止请求均不进入 Frame。</summary>
    public bool Navigate(ApplicationPage page)
    {
        if(_stopping||_navigating||_frame is not {} frame||!_pageTypes.TryGetValue(page,out var pageType))return false;
        if(Current==page)return true;
        return RunNavigation(()=>frame.Navigate(pageType,null));
    }
    /// <summary>通过 Frame 自身历史返回；没有历史或导航仍在进行时返回 false。</summary>
    public bool TryGoBack()
    {
        if(_stopping||_navigating||_frame is not {} frame||!frame.CanGoBack)return false;
        return RunNavigation(frame.GoBack);
    }
    private bool RunNavigation(Func<bool> request)
    {
        _navigating=true;_requestFailed=false;
        try{if(!request()){_navigating=false;return false;}return !_requestFailed;}
        catch(Exception error){FrameFailed(error);return false;}
    }
    private void FrameNavigated(Type pageType,object? page)
    {
        if(page is not null&&_known.Add(page))_visited.Add(page);
        if(_stopping){if(page is not null)StopOne(page);return;}
        var target=_pageTypes.FirstOrDefault(pair=>pair.Value==pageType);
        if(target.Value is null){FrameFailed(new InvalidOperationException("Frame 返回未登记页面。"));return;}
        try
        {
            Current=target.Key;
            if(Navigated is {} handlers)foreach(Action<ApplicationPage> handler in handlers.GetInvocationList())try{handler(target.Key);}catch(Exception error){Report(error);}
        }
        finally{_navigating=false;}
    }
    private void FrameFailed(Exception error){_requestFailed=true;_navigating=false;Report(error);}
    private void Report(Exception error){try{_report?.Invoke(error);}catch(Exception reportError){System.Diagnostics.Trace.TraceWarning("导航诊断失败："+reportError.GetType().Name);}}
    private void StopOne(object page)
    {if(!_stopped.Add(page))return;try{if(page is IDisposable disposable)disposable.Dispose();}catch(Exception error){_stopFailures.Add(error);}}
    /// <summary>真正退出先拒绝所有新导航，托盘隐藏不调用此入口。</summary>
    public void RequestStop()=>_stopping=true;
    /// <summary>停止全部已访问缓存页面；单页失败继续处理其他页，离开页面不会调用此方法。</summary>
    public void StopPages()
    {
        RequestStop();foreach(var page in _visited.ToArray())StopOne(page);
        if(_stopFailures.Count>0)throw new AggregateException("部分页面停止失败。",_stopFailures.ToArray());
    }
    /// <summary>重复释放等待同一任务；异步清理期间收到的迟到页面也会停止并释放。</summary>
    public ValueTask DisposeAsync()=>new(_releaseWork??=ReleaseCoreAsync());
    private async Task ReleaseCoreAsync()
    {
        var errors=new List<Exception>();try{StopPages();}catch(Exception error){errors.Add(error);}
        var reportedStops=_stopFailures.Count;
        while(_visited.FirstOrDefault(page=>!_released.Contains(page)) is {} page)
        {
            _released.Add(page);try{if(page is IAsyncDisposable asynchronous)await asynchronous.DisposeAsync();}catch(Exception error){errors.Add(error);}
        }
        errors.AddRange(_stopFailures.Skip(reportedStops));
        if(_frame is {} frame)
        {
            frame.Navigated-=FrameNavigated;frame.Failed-=FrameFailed;
            try{frame.Dispose();}catch(Exception error){errors.Add(error);}
            _frame=null;
        }
        _visited.Clear();_known.Clear();_stopped.Clear();_released.Clear();_pageTypes=new Dictionary<ApplicationPage,Type>();Current=null;Navigated=null;
        if(errors.Count>0)throw new AggregateException("部分导航页面释放失败。",errors);
    }
}
