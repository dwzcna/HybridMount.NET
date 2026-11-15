using HybridMount.Services.Mounting;
namespace HybridMount.Services;

/// <summary>独立于页面模型的任务定位状态；只请求任务页面，筛选不进入导航参数或IPC任务队列。</summary>
public sealed class TaskFocusService(INavigationService navigation)
{
    private readonly object _gate=new();
    private TaskIssueFocus? _focus;
    private long _revision;
    private bool _stopping;
    public event Action? Changed;
    public TaskIssueFocus? Current {get{lock(_gate)return _focus;}}
    /// <summary>由UI线程调用；先保存筛选，再通知已有页面，首次创建的页面从Current恢复。</summary>
    public bool ShowIssues(Guid mount,IReadOnlyList<string>? paths=null)
    {
        lock(_gate)
        {
            if(_stopping)return false;
            _focus=new(mount,Array.AsReadOnly((paths??[]).Select(RelativePaths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),++_revision);
        }
        Changed?.Invoke();return navigation.Navigate(ApplicationPage.Tasks);
    }
    /// <summary>由UI线程明确查看全部任务；清除上一次问题通知留下的挂载和路径筛选。</summary>
    public bool ShowAll()
    {
        lock(_gate)
        {
            if(_stopping)return false;
            _focus=new(null,Array.Empty<string>(),++_revision,IssuesOnly:false);
        }
        Changed?.Invoke();return navigation.Navigate(ApplicationPage.Tasks);
    }
    /// <summary>弹窗实际关闭后由UI请求；清空旧筛选，目标暂未出现在快照时保留定位意图。</summary>
    public bool ShowTask(string taskId)
    {
        if(string.IsNullOrWhiteSpace(taskId))return false;
        lock(_gate){if(_stopping)return false;_focus=new(null,Array.Empty<string>(),++_revision,IssuesOnly:false,TaskId:taskId);}
        Changed?.Invoke();return navigation.Navigate(ApplicationPage.Tasks);
    }
    private Guid? _configurationMount;
    /// <summary>详情关闭后返回已有配置；导航页读取一次，不借用关闭的详情控件。</summary>
    public bool ShowConfiguration(Guid mount){lock(_gate){if(_stopping)return false;_configurationMount=mount;}return navigation.Navigate(ApplicationPage.Mounts);}
    public Guid? TakeConfiguration(){lock(_gate){var mount=_configurationMount;_configurationMount=null;return mount;}}
    public void RequestStop(){lock(_gate)_stopping=true;}
}
/// <summary>只包含挂载和相对路径的只读定位快照；空路径列表表示挂载全部问题，空字符串表示根及其后代。</summary>
public sealed record TaskIssueFocus(Guid? MountId,IReadOnlyList<string> Paths,long Revision,bool IssuesOnly=true,string? TaskId=null);
