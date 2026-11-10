using System.Text.RegularExpressions;
using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>验证页面枚举、Frame 历史、成功通知与退出释放；可控适配器不替代实际界面验收。</summary>
public sealed class NavigationTests
{
    private sealed class HomeMarker { } private sealed class MountMarker { } private sealed class TasksMarker { } private sealed class LogsMarker { } private sealed class SettingsMarker { }
    private static readonly Type[] Types=[typeof(HomeMarker),typeof(MountMarker),typeof(TasksMarker),typeof(LogsMarker),typeof(SettingsMarker)];
    private static IReadOnlyDictionary<ApplicationPage,Type> Pages()=>Enum.GetValues<ApplicationPage>().Select((page,index)=>(page,index)).ToDictionary(pair=>pair.page,pair=>Types[pair.index]);
    private class Page : IDisposable
    {
        public int Stops;public bool Broken;
        public void Dispose(){Stops++;if(Broken)throw new IOException("page stop");}
    }
    private sealed class AsyncPage : Page,IAsyncDisposable
    {
        public int Releases;public TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync(){Releases++;await Release.Task;}
    }
    /// <summary>模拟系统 Frame 的缓存与历史；业务服务只能请求导航或返回，无法改写测试历史。</summary>
    private sealed class Frame : INavigationFrame
    {
        public event Action<Type,object?>? Navigated;public event Action<Exception>? Failed;
        public readonly Dictionary<Type,Page> Cache=[];public readonly List<object?> Parameters=[];
        public readonly List<Type> Back=[],Forward=[];
        public Type? Current;public int Calls,BackCalls,Disposals;public bool Delay;public string? Failure;
        private (Type Type,Page Page,bool Back)? _pending;
        public bool CanGoBack=>Back.Count>0;
        public int Subscribers=>(Navigated?.GetInvocationList().Length??0)+(Failed?.GetInvocationList().Length??0);
        private bool ShouldFail()
        {
            var failure=Failure;Failure=null;
            if(failure=="throw")throw new IOException("navigation");
            if(failure=="event")Failed?.Invoke(new IOException("event"));
            return failure is "false" or "event";
        }
        public bool Navigate(Type type,object? parameter)
        {
            Calls++;Parameters.Add(parameter);if(ShouldFail())return false;
            if(!Cache.TryGetValue(type,out var page))Cache[type]=page=new();
            return Schedule(type,page,false);
        }
        public bool GoBack()
        {
            BackCalls++;if(!CanGoBack||ShouldFail())return false;
            var type=Back[^1];return Schedule(type,Cache[type],true);
        }
        private bool Schedule(Type type,Page page,bool back)
        {if(Delay)_pending=(type,page,back);else Apply(type,page,back);return true;}
        private void Apply(Type type,Page page,bool back)
        {
            if(back){Back.RemoveAt(Back.Count-1);if(Current is {} current)Forward.Add(current);}
            else{if(Current is {} current)Back.Add(current);Forward.Clear();}
            Current=type;Navigated?.Invoke(type,page);
        }
        public void Complete(){var pending=_pending!.Value;_pending=null;Apply(pending.Type,pending.Page,pending.Back);}
        public void Late(Type type,Page page){Cache[type]=page;Navigated?.Invoke(type,page);}
        public void Dispose(){Disposals++;}
    }
    [Fact]
    public async Task All_pages_use_frame_repeat_current_without_history_and_reuse_cached_instances()
    {
        var frame=new Frame();await using var service=new NavigationService(frame,Pages());var selections=new List<ApplicationPage>();service.Navigated+=selections.Add;
        foreach(var page in Enum.GetValues<ApplicationPage>())
        {
            Assert.True(service.Navigate(page));var before=frame.Calls;var history=frame.Back.Count;
            Assert.True(service.Navigate(page));Assert.Equal(before,frame.Calls);Assert.Equal(history,frame.Back.Count);Assert.Equal(page,service.Current);
        }
        for(var index=0;index<50;index++)Assert.True(service.Navigate((ApplicationPage)(index%5)));
        Assert.Equal(5,frame.Cache.Count);Assert.Equal(frame.Calls,selections.Count);Assert.Equal(frame.Calls-1,frame.Back.Count);Assert.All(frame.Parameters,Assert.Null);
        await service.DisposeAsync();await service.DisposeAsync();Assert.All(frame.Cache.Values,page=>Assert.Equal(1,page.Stops));Assert.Equal(1,frame.Disposals);Assert.Equal(0,frame.Subscribers);
        Assert.False(service.Navigate(ApplicationPage.Home));Assert.False(service.TryGoBack());Assert.False(service.CanGoBack);
    }
    [Theory]
    [InlineData(-1)][InlineData(5)][InlineData(int.MaxValue)]
    public async Task Invalid_page_never_reaches_frame(int value)
    {
        var frame=new Frame();await using var service=new NavigationService(frame,Pages());
        Assert.False(service.Navigate((ApplicationPage)value));Assert.Equal(0,frame.Calls);Assert.Null(service.Current);Assert.Empty(frame.Cache);
    }
    [Fact]
    public async Task Back_uses_frame_history_and_new_navigation_after_return_discards_forward_history()
    {
        var frame=new Frame();await using var service=new NavigationService(frame,Pages());var selections=new List<ApplicationPage>();service.Navigated+=selections.Add;
        Assert.False(service.CanGoBack);Assert.False(service.TryGoBack());Assert.Equal(0,frame.BackCalls);
        Assert.True(service.Navigate(ApplicationPage.Home));Assert.False(service.CanGoBack);
        Assert.True(service.Navigate(ApplicationPage.Mounts));Assert.True(service.Navigate(ApplicationPage.Tasks));Assert.True(service.CanGoBack);
        Assert.True(service.TryGoBack());Assert.Equal(ApplicationPage.Mounts,service.Current);Assert.True(service.CanGoBack);
        Assert.True(service.TryGoBack());Assert.Equal(ApplicationPage.Home,service.Current);Assert.False(service.CanGoBack);Assert.False(service.TryGoBack());
        Assert.Equal(2,frame.Forward.Count);Assert.True(service.Navigate(ApplicationPage.Logs));Assert.Empty(frame.Forward);
        Assert.True(service.TryGoBack());Assert.Equal(ApplicationPage.Home,service.Current);
        Assert.Equal(4,frame.Calls);Assert.Equal(3,frame.BackCalls);
        Assert.Equal(new[]{ApplicationPage.Home,ApplicationPage.Mounts,ApplicationPage.Tasks,ApplicationPage.Mounts,ApplicationPage.Home,ApplicationPage.Logs,ApplicationPage.Home},selections);
        Assert.All(frame.Cache.Values,page=>Assert.Equal(0,page.Stops));
    }
    [Theory]
    [InlineData("false")][InlineData("throw")][InlineData("event")]
    public async Task Failed_navigation_does_not_publish_selection_and_next_request_can_retry(string failure)
    {
        var frame=new Frame();var errors=new List<Exception>();await using var service=new NavigationService(frame,Pages(),errors.Add);var selections=new List<ApplicationPage>();service.Navigated+=selections.Add;
        Assert.True(service.Navigate(ApplicationPage.Home));frame.Failure=failure;Assert.False(service.Navigate(ApplicationPage.Mounts));Assert.Equal(ApplicationPage.Home,service.Current);Assert.Single(selections);Assert.Empty(frame.Back);
        Assert.True(service.Navigate(ApplicationPage.Mounts));Assert.Equal(ApplicationPage.Mounts,service.Current);Assert.Equal(2,selections.Count);Assert.Equal(failure=="false"?0:1,errors.Count);
    }
    [Theory]
    [InlineData("false")][InlineData("throw")][InlineData("event")]
    public async Task Failed_return_preserves_current_and_history_and_allows_retry(string failure)
    {
        var frame=new Frame();var errors=new List<Exception>();await using var service=new NavigationService(frame,Pages(),errors.Add);var selections=new List<ApplicationPage>();service.Navigated+=selections.Add;
        service.Navigate(ApplicationPage.Home);service.Navigate(ApplicationPage.Mounts);frame.Failure=failure;
        Assert.False(service.TryGoBack());Assert.Equal(ApplicationPage.Mounts,service.Current);Assert.Equal(2,selections.Count);Assert.True(service.CanGoBack);Assert.Single(frame.Back);
        Assert.True(service.TryGoBack());Assert.Equal(ApplicationPage.Home,service.Current);Assert.False(service.CanGoBack);Assert.Equal(failure=="false"?0:1,errors.Count);
    }
    [Fact]
    public async Task Selection_roundtrip_and_faulty_subscriber_do_not_reenter_or_skip_later_subscribers()
    {
        var frame=new Frame();var errors=new List<Exception>();await using var service=new NavigationService(frame,Pages(),errors.Add);var recorded=new List<ApplicationPage>();
        service.Navigated+=page=>{Assert.False(service.Navigate(page));Assert.False(service.TryGoBack());};
        service.Navigated+=_=>throw new IOException("subscriber");service.Navigated+=recorded.Add;
        Assert.True(service.Navigate(ApplicationPage.Home));Assert.True(service.Navigate(ApplicationPage.Mounts));Assert.True(service.TryGoBack());
        Assert.Equal(3,recorded.Count);Assert.Equal(3,errors.Count);Assert.Equal(2,frame.Calls);Assert.Equal(1,frame.BackCalls);
    }
    [Fact]
    public async Task Inflight_navigation_and_return_reject_new_requests_until_success_event()
    {
        var frame=new Frame{Delay=true};await using var service=new NavigationService(frame,Pages());Assert.True(service.Navigate(ApplicationPage.Home));Assert.Null(service.Current);
        Assert.False(service.Navigate(ApplicationPage.Mounts));Assert.False(service.TryGoBack());Assert.Equal(1,frame.Calls);frame.Complete();Assert.Equal(ApplicationPage.Home,service.Current);
        Assert.True(service.Navigate(ApplicationPage.Mounts));frame.Complete();Assert.Equal(ApplicationPage.Mounts,service.Current);Assert.Single(frame.Back);
        Assert.True(service.TryGoBack());Assert.Equal(ApplicationPage.Mounts,service.Current);Assert.False(service.TryGoBack());Assert.False(service.Navigate(ApplicationPage.Tasks));
        frame.Complete();Assert.Equal(ApplicationPage.Home,service.Current);Assert.Empty(frame.Back);Assert.Single(frame.Forward);
    }
    [Fact]
    public async Task Stop_attempts_every_cached_page_once_and_preserves_cleanup_failure_on_final_release()
    {
        var frame=new Frame();var service=new NavigationService(frame,Pages());service.Navigate(ApplicationPage.Home);service.Navigate(ApplicationPage.Mounts);service.Navigate(ApplicationPage.Tasks);frame.Cache[typeof(MountMarker)].Broken=true;
        Assert.Throws<AggregateException>(service.StopPages);Assert.Throws<AggregateException>(service.StopPages);Assert.All(frame.Cache.Values,page=>Assert.Equal(1,page.Stops));Assert.False(service.TryGoBack());
        await Assert.ThrowsAsync<AggregateException>(()=>service.DisposeAsync().AsTask());await Assert.ThrowsAsync<AggregateException>(()=>service.DisposeAsync().AsTask());Assert.Equal(1,frame.Disposals);Assert.Equal(0,frame.Subscribers);
    }
    [Fact]
    public async Task Async_release_waits_and_late_cached_page_is_stopped_without_new_selection()
    {
        var frame=new Frame();var waiting=new AsyncPage();frame.Cache[typeof(HomeMarker)]=waiting;var service=new NavigationService(frame,Pages());var selections=0;service.Navigated+=_=>selections++;service.Navigate(ApplicationPage.Home);
        var first=service.DisposeAsync().AsTask();var second=service.DisposeAsync().AsTask();Assert.Same(first,second);Assert.False(first.IsCompleted);Assert.Equal(1,waiting.Stops);
        var late=new Page();frame.Late(typeof(MountMarker),late);Assert.Equal(1,late.Stops);Assert.Equal(1,selections);Assert.False(service.Navigate(ApplicationPage.Settings));Assert.False(service.TryGoBack());
        waiting.Release.SetResult();await first;await second;Assert.Equal(1,waiting.Releases);Assert.Equal(1,late.Stops);Assert.Equal(1,frame.Disposals);
    }
    [Fact]
    public async Task Home_commands_use_single_application_service_before_binding_and_after_stop()
    {
        await using var service=new NavigationService();var home=new HomeViewModel("0.0.9.25",service);Assert.False(home.OpenMounts());Assert.False(service.CanGoBack);Assert.False(service.TryGoBack());
        var frame=new Frame();service.RegisterFrame(frame,Pages());Assert.Throws<InvalidOperationException>(()=>service.RegisterFrame(frame,Pages()));
        Assert.True(home.OpenMounts());Assert.True(home.OpenSettings());Assert.True(home.OpenLogs());Assert.Equal(ApplicationPage.Logs,service.Current);Assert.Equal("版本 0.0.9.25",home.VersionText);
        service.RequestStop();Assert.False(home.OpenMounts());Assert.False(service.TryGoBack());Assert.False(service.CanGoBack);Assert.Throws<InvalidOperationException>(()=>service.RegisterFrame(frame,Pages()));
    }
    [Fact]
    public async Task Page_registration_requires_every_enum_and_unique_page_types()
    {
        await using var service=new NavigationService();var frame=new Frame();var missing=Pages().ToDictionary();missing.Remove(ApplicationPage.Settings);
        Assert.Throws<ArgumentException>(()=>service.RegisterFrame(frame,missing));Assert.Equal(0,frame.Subscribers);
        var duplicate=Pages().ToDictionary();duplicate[ApplicationPage.Settings]=duplicate[ApplicationPage.Home];Assert.Throws<ArgumentException>(()=>service.RegisterFrame(frame,duplicate));Assert.Equal(0,frame.Subscribers);
        service.RegisterFrame(frame,Pages());Assert.True(service.Navigate(ApplicationPage.Settings));
    }
    [Fact]
    public async Task Unexpected_frame_page_does_not_publish_selection_and_remains_owned_for_cleanup()
    {
        var frame=new Frame();var errors=new List<Exception>();await using var service=new NavigationService(frame,Pages(),errors.Add);var selections=0;service.Navigated+=_=>selections++;
        service.Navigate(ApplicationPage.Home);var page=new Page();frame.Late(typeof(string),page);Assert.Single(errors);Assert.Equal(1,selections);Assert.Equal(ApplicationPage.Home,service.Current);
        await service.DisposeAsync();Assert.Equal(1,page.Stops);
    }
    [Fact]
    public void Production_adapter_uses_frame_animation_history_and_required_page_cache()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;Assert.NotNull(root);
        var folder=Path.Combine(root.FullName,"HybridMount");var window=File.ReadAllText(Path.Combine(folder,"Views","MainWindow.xaml.cs"));Assert.DoesNotContain("_pages",window);Assert.DoesNotContain("ContentFrame.Content",window);
        foreach(var name in new[]{"Home","Mounts","Tasks","Logs","Settings"})
        {
            Assert.DoesNotContain("new "+name+"Page(",window);var page=File.ReadAllText(Path.Combine(folder,"Pages",name+"Page.xaml.cs"));Assert.Contains("NavigationCacheMode.Required",page);Assert.DoesNotContain("MainWindow?.Navigate",page);
        }
        var adapter=File.ReadAllText(Path.Combine(folder,"Services","NavigationService.WinUI.cs"));Assert.Contains("_frame.Navigate(pageType,parameter,new EntranceNavigationTransitionInfo())",adapter);
        Assert.Contains("_frame.GoBack(new EntranceNavigationTransitionInfo())",adapter);Assert.DoesNotMatch(new Regex(@"\.Content\s*="),adapter);Assert.Contains("frame.CacheSize=5",adapter);
        Assert.DoesNotContain("BackStack.Clear",adapter);Assert.DoesNotContain("ForwardStack.Clear",adapter);Assert.Contains("Navigation.ItemInvoked",window);Assert.DoesNotContain("Navigation.SelectionChanged",window);
        Assert.Contains("AppTitleBar.BackRequested",window);Assert.Contains("GetOpenPopupsForXamlRoot",window);Assert.Contains("args.Handled=true;TryGoBack()",window);
        var markup=File.ReadAllText(Path.Combine(folder,"Views","MainWindow.xaml"));Assert.Contains("IsBackButtonVisible=\"True\"",markup);Assert.Contains("Key=\"Left\" Modifiers=\"Menu\"",markup);
        Assert.DoesNotContain("hybridmount://",markup);Assert.False(File.Exists(Path.Combine(folder,"Models","ApplicationRoutes.cs")));Assert.False(File.Exists(Path.Combine(folder,"Services","NavigationGateway.cs")));
    }
}
