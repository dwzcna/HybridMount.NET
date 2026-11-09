using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>主窗口状态在无WinUI消息泵下可验证，导航由既有服务拥有，释放不结束后台导航服务。</summary>
public sealed class MainWindowViewModelTests
{
    private sealed class Immediate:IUiDispatcher {public bool TryEnqueue(Action action){action();return true;}}
    private sealed class Frame:INavigationFrame
    {
        private Type? _current;private readonly Stack<Type> _history=[];
        public event Action<Type,object?>? Navigated;
        public event Action<Exception>? Failed {add{} remove{}}
        public bool CanGoBack=>_history.Count>0;
        public bool Navigate(Type pageType,object? parameter){if(_current is {} current)_history.Push(current);_current=pageType;Navigated?.Invoke(pageType,null);return true;}
        public bool GoBack(){if(!_history.TryPop(out var page))return false;_current=page;Navigated?.Invoke(page,null);return true;}
        public void Dispose() { }
    }
    private sealed class Home { }private sealed class Mounts { }private sealed class Tasks { }private sealed class Logs { }private sealed class Settings { }
    [Fact]
    public async Task NavigationNotificationStateAndSubscriptionLifetimeRemainIndependent()
    {
        var types=new Dictionary<ApplicationPage,Type>{{ApplicationPage.Home,typeof(Home)},{ApplicationPage.Mounts,typeof(Mounts)},{ApplicationPage.Tasks,typeof(Tasks)},{ApplicationPage.Logs,typeof(Logs)},{ApplicationPage.Settings,typeof(Settings)}};
        await using var navigation=new NavigationService(new Frame(),types);using var vm=new MainWindowViewModel(navigation,new Immediate());var updates=0;vm.Navigated+=_=>updates++;
        Assert.True(vm.Navigate(ApplicationPage.Home));Assert.Equal(ApplicationPage.Home,vm.CurrentPage);Assert.False(vm.CanGoBack);
        Assert.True(vm.Navigate(ApplicationPage.Tasks));Assert.True(vm.CanGoBack);Assert.True(vm.TryGoBack());Assert.Equal(ApplicationPage.Home,vm.CurrentPage);
        vm.ShowNotification("日志","已保存的说明");Assert.True(vm.IsNotificationOpen);Assert.Equal("已保存的说明",vm.NotificationMessage);vm.IsNotificationOpen=false;
        var before=updates;vm.Dispose();Assert.False(vm.Navigate(ApplicationPage.Mounts));Assert.True(navigation.Navigate(ApplicationPage.Mounts));Assert.Equal(before,updates);
        vm.ShowNotification("退出后","不得覆盖");Assert.Equal("日志",vm.NotificationSource);
    }
}
