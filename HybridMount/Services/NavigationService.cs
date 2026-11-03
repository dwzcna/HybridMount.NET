namespace HybridMount.Services;

/// <summary>可验证的 Frame 边界；生产适配器实际调用 WinUI Frame.Navigate，不创建页面或设置 Content。</summary>
internal interface INavigationFrame : IDisposable
{
    event Action<Type,object?>? Navigated;
    event Action<Exception>? Failed;
    bool Navigate(Type pageType,object? parameter);
    void ClearHistory();
}
/// <summary>UI 线程上的路由、成功通知及缓存页面释放；网络任务仍由应用服务拥有。</summary>
public sealed partial class NavigationService : INavigationService,IAsyncDisposable
{
    private readonly INavigationFrame _frame;
    private readonly IReadOnlyDictionary<string,Type> _routes;
    private readonly Action<Exception>? _report;
    private readonly List<object> _visited=[];
    private readonly HashSet<object> _known=new(ReferenceEqualityComparer.Instance),_stopped=new(ReferenceEqualityComparer.Instance),_released=new(ReferenceEqualityComparer.Instance);
    private readonly List<Exception> _stopFailures=[];
    private bool _navigating,_stopping,_requestFailed;
    private Task? _releaseWork;
    public Uri? Current {get;private set;}
    public event Action<Uri>? Navigated;
    internal NavigationService(INavigationFrame frame,IReadOnlyDictionary<string,Type> routes,Action<Exception>? report=null)
    {
        if(routes.Count!=ApplicationRoutes.All.Count||routes.Values.Distinct().Count()!=routes.Count||ApplicationRoutes.All.Any(route=>!routes.ContainsKey(route.OriginalString)))throw new ArgumentException("导航表必须包含全部五个唯一页面。");
        _frame=frame;_routes=new Dictionary<string,Type>(routes,StringComparer.Ordinal);_report=report;frame.Navigated+=FrameNavigated;frame.Failed+=FrameFailed;
    }
    /// <summary>重复当前页不导航；失败不推进侧栏，正在处理的导航不重入，未支持 URI 不进入 Frame。</summary>
    public bool Navigate(Uri target)
    {
        if(_stopping||!ApplicationRoutes.IsRegistered(target))return false;
        if(_navigating)return false;
        if(Current?.OriginalString==target.OriginalString)return true;
        _navigating=true;_requestFailed=false;
        try{if(!_frame.Navigate(_routes[target.OriginalString],null)){_navigating=false;return false;}return !_requestFailed;}
        catch(Exception error){FrameFailed(error);return false;}
    }
    private void FrameNavigated(Type pageType,object? page)
    {
        if(page is not null&&_known.Add(page))_visited.Add(page);
        if(_stopping){if(page is not null)StopOne(page);return;}
        var key=_routes.FirstOrDefault(pair=>pair.Value==pageType).Key;
        if(key is null){FrameFailed(new InvalidOperationException("Frame 返回未登记页面。"));return;}
        try
        {
            _frame.ClearHistory();Current=new(key);
            if(Navigated is {} handlers)foreach(Action<Uri> handler in handlers.GetInvocationList())try{handler(Current);}catch(Exception error){Report(error);}
        }
        finally{_navigating=false;}
    }
    private void FrameFailed(Exception error){_requestFailed=true;_navigating=false;Report(error);}
    private void Report(Exception error){try{_report?.Invoke(error);}catch(Exception reportError){System.Diagnostics.Trace.TraceWarning("导航诊断失败："+reportError.GetType().Name);}}
    private void StopOne(object page)
    {if(!_stopped.Add(page))return;try{if(page is IDisposable disposable)disposable.Dispose();}catch(Exception error){_stopFailures.Add(error);}}
    /// <summary>先停止所有缓存页面通知；单页失败继续处理其他页，离开页面时不调用此方法。</summary>
    public void StopPages()
    {
        _stopping=true;foreach(var page in _visited.ToArray())StopOne(page);
        if(_stopFailures.Count>0)throw new AggregateException("部分页面停止失败。",_stopFailures.ToArray());
    }
    public ValueTask DisposeAsync()=>new(_releaseWork??=ReleaseCoreAsync());
    private async Task ReleaseCoreAsync()
    {
        var errors=new List<Exception>();try{StopPages();}catch(Exception error){errors.Add(error);}
        var reportedStops=_stopFailures.Count;
        // 系统设置等待期间仍收集迟到的页面；它们先停止通知，再与其余缓存一同释放。
        while(_visited.FirstOrDefault(page=>!_released.Contains(page)) is {} page)
        {
            _released.Add(page);try{if(page is IAsyncDisposable asynchronous)await asynchronous.DisposeAsync();}catch(Exception error){errors.Add(error);}
        }
        errors.AddRange(_stopFailures.Skip(reportedStops));_frame.Navigated-=FrameNavigated;_frame.Failed-=FrameFailed;
        try{_frame.Dispose();}catch(Exception error){errors.Add(error);}
        _visited.Clear();_known.Clear();_stopped.Clear();_released.Clear();Navigated=null;
        if(errors.Count>0)throw new AggregateException("部分导航页面释放失败。",errors);
    }
}
