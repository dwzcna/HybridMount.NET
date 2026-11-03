using HybridMount.Pages;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
namespace HybridMount.Services;

public sealed partial class NavigationService
{
    /// <summary>唯一生产装配；只登记五个页面类型，页面实例由 Frame 创建并按 Required 缓存。</summary>
    public NavigationService(Frame frame,Action<Exception>? report=null):this(new WinUiFrame(frame),new Dictionary<string,Type>
    {
        [ApplicationRoutes.Home.OriginalString]=typeof(HomePage),[ApplicationRoutes.Mounts.OriginalString]=typeof(MountsPage),
        [ApplicationRoutes.Tasks.OriginalString]=typeof(TasksPage),[ApplicationRoutes.Logs.OriginalString]=typeof(LogsPage),[ApplicationRoutes.Settings.OriginalString]=typeof(SettingsPage)
    },report) { }
    /// <summary>只转接系统 Frame API 与事件，不实现备用的页面构造路径。</summary>
    private sealed class WinUiFrame : INavigationFrame
    {
        private readonly Frame _frame;
        public event Action<Type,object?>? Navigated;
        public event Action<Exception>? Failed;
        public WinUiFrame(Frame frame){_frame=frame;frame.CacheSize=5;frame.Navigated+=OnNavigated;frame.NavigationFailed+=OnFailed;}
        public bool Navigate(Type pageType,object? parameter)=>_frame.Navigate(pageType,parameter);
        public void ClearHistory(){_frame.BackStack.Clear();_frame.ForwardStack.Clear();}
        private void OnNavigated(object sender,NavigationEventArgs args)=>Navigated?.Invoke(args.SourcePageType,args.Content);
        private void OnFailed(object sender,NavigationFailedEventArgs args){args.Handled=true;Failed?.Invoke(args.Exception);}
        public void Dispose(){_frame.Navigated-=OnNavigated;_frame.NavigationFailed-=OnFailed;}
    }
}
