using CommunityToolkit.Mvvm.ComponentModel;
namespace HybridMount.ViewModels;

/// <summary>主窗口的导航、通知和同步问题状态；通过UI派发器更新，不创建窗口或停止后台业务。</summary>
public sealed class MainWindowViewModel : ObservableObject,IDisposable
{
    private readonly NavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly UploadIssueNotificationService? _issues;
    private readonly TaskFocusService? _focus;
    private readonly Action<Exception>? _report;
    private bool _disposed,_notificationOpen;
    private int _pending;
    private string _notificationSource="",_notificationMessage="";
    public ApplicationPage? CurrentPage=>_navigation.Current;
    public bool CanGoBack=>!_disposed&&_navigation.CanGoBack;
    public IReadOnlyList<UploadIssueNotice> IssueNotices {get;private set;}=[];
    public string NotificationSource {get=>_notificationSource;private set=>SetProperty(ref _notificationSource,value);}
    public string NotificationMessage {get=>_notificationMessage;private set=>SetProperty(ref _notificationMessage,value);}
    public bool IsNotificationOpen {get=>_notificationOpen;set=>SetProperty(ref _notificationOpen,value);}
    public event Action<ApplicationPage>? Navigated;
    public event Action? NoticesChanged;
    /// <summary>在UI线程建立订阅；通知只读现有快照，失败保留旧内容并交给应用诊断。</summary>
    public MainWindowViewModel(NavigationService navigation,IUiDispatcher dispatcher,UploadIssueNotificationService? issues=null,TaskFocusService? focus=null,Action<Exception>? report=null)
    {_navigation=navigation;_dispatcher=dispatcher;_issues=issues;_focus=focus;_report=report;navigation.Navigated+=OnNavigated;if(issues is not null)issues.Changed+=OnIssues;RefreshNotices();}
    public bool Navigate(ApplicationPage page)=>!_disposed&&_navigation.Navigate(page);
    public bool TryGoBack()=>!_disposed&&_navigation.TryGoBack();
    private void OnNavigated(ApplicationPage page)
    {
        if(_disposed)return;_dispatcher.TryEnqueue(()=>{if(_disposed||_navigation.Current!=page)return;OnPropertyChanged(nameof(CurrentPage));OnPropertyChanged(nameof(CanGoBack));Navigated?.Invoke(page);});
    }
    private void OnIssues()
    {
        if(_disposed||Interlocked.Exchange(ref _pending,1)!=0)return;
        if(!_dispatcher.TryEnqueue(()=>{Interlocked.Exchange(ref _pending,0);if(!_disposed)RefreshNotices();}))Interlocked.Exchange(ref _pending,0);
    }
    /// <summary>仅刷新应用内问题快照；不查询服务器、启动进程或改写任何同步记录。</summary>
    public void RefreshNotices()
    {
        if(_disposed)return;
        try{IssueNotices=_issues?.Pending.ToArray()??[];OnPropertyChanged(nameof(IssueNotices));NoticesChanged?.Invoke();}
        catch(Exception error){_report?.Invoke(error);}
    }
    public void OpenIssues(Guid mount){if(!_disposed)_focus?.ShowIssues(mount);}
    public void DismissIssues(Guid mount){if(!_disposed)_issues?.Dismiss(mount);}
    /// <summary>UI线程显示已持久化日志的说明；窗口隐藏不清除状态，关闭提示只改变本窗口状态。</summary>
    public void ShowNotification(string source,string message)
    {if(_disposed)return;NotificationSource=source;NotificationMessage=message;IsNotificationOpen=true;}
    /// <summary>只解除UI订阅；导航和任务服务仍由应用按既有退出顺序收尾。</summary>
    public void Dispose(){if(_disposed)return;_disposed=true;_navigation.Navigated-=OnNavigated;if(_issues is not null)_issues.Changed-=OnIssues;}
}
