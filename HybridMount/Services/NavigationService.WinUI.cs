using HybridMount.Pages;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
namespace HybridMount.Services;

public sealed partial class NavigationService
{
    /// <summary>唯一生产映射；Frame 创建五个页面并按 Required 缓存，窗口只在首次创建时注册。</summary>
    public void RegisterFrame(Frame frame,Action<Exception>? report=null)
    {
        EnsureCanRegister();ArgumentNullException.ThrowIfNull(frame);
        var adapter=new WinUiFrame(frame);
        try
        {
            RegisterFrame(adapter,new Dictionary<ApplicationPage,Type>
            {
                [ApplicationPage.Home]=typeof(HomePage),[ApplicationPage.Mounts]=typeof(MountsPage),
                [ApplicationPage.Tasks]=typeof(TasksPage),[ApplicationPage.Logs]=typeof(LogsPage),[ApplicationPage.Settings]=typeof(SettingsPage)
            },report);
        }
        catch{adapter.Dispose();throw;}
    }
    /// <summary>仅转接系统 Frame、内置切换动画和事件，历史及页面生命周期由 WinUI 驱动。</summary>
    private sealed class WinUiFrame : INavigationFrame
    {
        private readonly Frame _frame;
        public event Action<Type,object?>? Navigated;
        public event Action<Exception>? Failed;
        public WinUiFrame(Frame frame){_frame=frame;frame.CacheSize=5;frame.Navigated+=OnNavigated;frame.NavigationFailed+=OnFailed;}
        public bool CanGoBack=>_frame.CanGoBack;
        public bool Navigate(Type pageType,object? parameter)=>_frame.Navigate(pageType,parameter,new EntranceNavigationTransitionInfo());
        public bool GoBack(){if(!_frame.CanGoBack)return false;_frame.GoBack(new EntranceNavigationTransitionInfo());return true;}
        private void OnNavigated(object sender,NavigationEventArgs args)=>Navigated?.Invoke(args.SourcePageType,args.Content);
        private void OnFailed(object sender,NavigationFailedEventArgs args){args.Handled=true;Failed?.Invoke(args.Exception);}
        public void Dispose(){_frame.Navigated-=OnNavigated;_frame.NavigationFailed-=OnFailed;}
    }
}
