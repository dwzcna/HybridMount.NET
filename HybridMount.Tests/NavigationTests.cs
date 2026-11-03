using System.Text.RegularExpressions;
using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>统一路由、成功侧栏通知、Frame 缓存及页面释放；可控 Frame 测试不冒充 WinUI GUI 验收。</summary>
public sealed class NavigationTests
{
    private sealed class HomeMarker { } private sealed class MountMarker { } private sealed class TasksMarker { } private sealed class LogsMarker { } private sealed class SettingsMarker { }
    private static readonly Type[] Types=[typeof(HomeMarker),typeof(MountMarker),typeof(TasksMarker),typeof(LogsMarker),typeof(SettingsMarker)];
    private static IReadOnlyDictionary<string,Type> Routes()=>ApplicationRoutes.All.Select((uri,index)=>(uri,index)).ToDictionary(pair=>pair.uri.OriginalString,pair=>Types[pair.index],StringComparer.Ordinal);
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
    private sealed class Frame : INavigationFrame
    {
        public event Action<Type,object?>? Navigated;public event Action<Exception>? Failed;
        public readonly Dictionary<Type,Page> Cache=[];public readonly List<object?> Parameters=[];public readonly List<Type> Journal=[];
        public int Calls,Clears,Disposals;public bool Delay;public string? Failure;private (Type Type,Page Page)? _pending;
        public int Subscribers=>(Navigated?.GetInvocationList().Length??0)+(Failed?.GetInvocationList().Length??0);
        public bool Navigate(Type type,object? parameter)
        {
            Calls++;Parameters.Add(parameter);var failure=Failure;Failure=null;
            if(failure=="false")return false;if(failure=="throw")throw new IOException("navigation");if(failure=="event"){Failed?.Invoke(new IOException("event"));return false;}
            if(!Cache.TryGetValue(type,out var page))Cache[type]=page=new();Journal.Add(type);
            if(Delay)_pending=(type,page);else Navigated?.Invoke(type,page);return true;
        }
        public void Complete(){var pending=_pending!.Value;_pending=null;Navigated?.Invoke(pending.Type,pending.Page);}
        public void Late(Type type,Page page){Cache[type]=page;Navigated?.Invoke(type,page);}
        public void ClearHistory(){Clears++;Journal.Clear();}
        public void Dispose(){Disposals++;}
    }
    [Fact]
    public async Task All_routes_use_one_frame_path_repeat_current_without_navigation_and_reuse_cached_pages()
    {
        var frame=new Frame();await using var service=new NavigationService(frame,Routes());var selections=new List<Uri>();service.Navigated+=selections.Add;
        foreach(var uri in ApplicationRoutes.All){Assert.True(service.Navigate(uri));var before=frame.Calls;Assert.True(service.Navigate(new(uri.OriginalString)));Assert.Equal(before,frame.Calls);Assert.Equal(uri,service.Current);}
        for(var index=0;index<50;index++)Assert.True(service.Navigate(ApplicationRoutes.All[index%5]));
        Assert.Equal(5,frame.Cache.Count);Assert.Equal(frame.Calls,frame.Clears);Assert.Equal(frame.Calls,selections.Count);Assert.Empty(frame.Journal);Assert.All(frame.Parameters,Assert.Null);
        await service.DisposeAsync();await service.DisposeAsync();Assert.All(frame.Cache.Values,page=>Assert.Equal(1,page.Stops));Assert.Equal(1,frame.Disposals);Assert.Equal(0,frame.Subscribers);Assert.False(service.Navigate(ApplicationRoutes.Home));
    }
    [Theory]
    [InlineData(null)][InlineData("home")][InlineData("https://example.test/home")][InlineData("file:///home")]
    [InlineData("hybridmount://host/home")][InlineData("hybridmount:///unknown")][InlineData("hybridmount:///home?x=1")][InlineData("hybridmount:///home#top")]
    [InlineData("hybridmount:///home/")][InlineData("hybridmount:///Home")][InlineData("hybridmount:///%68ome")][InlineData("hybridmount:///mounts/../home")][InlineData("hybridmount:///tasks/sub")]
    public async Task Unregistered_external_encoded_or_parameterized_uris_never_reach_frame(string? value)
    {
        var frame=new Frame();await using var service=new NavigationService(frame,Routes());var target=value is null?null!:new Uri(value,UriKind.RelativeOrAbsolute);
        Assert.False(service.Navigate(target));Assert.Equal(0,frame.Calls);Assert.Null(service.Current);Assert.Empty(frame.Cache);
    }
    [Theory]
    [InlineData("false")][InlineData("throw")][InlineData("event")]
    public async Task Failed_navigation_does_not_publish_selection_and_next_request_can_retry(string failure)
    {
        var frame=new Frame();var errors=new List<Exception>();await using var service=new NavigationService(frame,Routes(),errors.Add);var selections=new List<Uri>();service.Navigated+=selections.Add;
        Assert.True(service.Navigate(ApplicationRoutes.Home));frame.Failure=failure;Assert.False(service.Navigate(ApplicationRoutes.Mounts));Assert.Equal(ApplicationRoutes.Home,service.Current);Assert.Single(selections);
        Assert.True(service.Navigate(ApplicationRoutes.Mounts));Assert.Equal(ApplicationRoutes.Mounts,service.Current);Assert.Equal(2,selections.Count);Assert.Equal(failure=="false"?0:1,errors.Count);
    }
    [Fact]
    public async Task Selection_roundtrip_and_faulty_subscriber_do_not_reenter_or_skip_later_subscribers()
    {
        var frame=new Frame();var errors=new List<Exception>();await using var service=new NavigationService(frame,Routes(),errors.Add);var recorded=new List<Uri>();
        service.Navigated+=uri=>Assert.False(service.Navigate(uri));service.Navigated+=_=>throw new IOException("subscriber");service.Navigated+=recorded.Add;
        Assert.True(service.Navigate(ApplicationRoutes.Home));Assert.Single(recorded);Assert.Single(errors);Assert.Equal(1,frame.Calls);
    }
    [Fact]
    public async Task Inflight_frame_request_rejects_new_request_until_success_event()
    {
        var frame=new Frame{Delay=true};await using var service=new NavigationService(frame,Routes());Assert.True(service.Navigate(ApplicationRoutes.Home));Assert.Null(service.Current);
        Assert.False(service.Navigate(ApplicationRoutes.Mounts));Assert.Equal(1,frame.Calls);frame.Complete();Assert.Equal(ApplicationRoutes.Home,service.Current);
        Assert.True(service.Navigate(ApplicationRoutes.Mounts));frame.Complete();Assert.Equal(ApplicationRoutes.Mounts,service.Current);Assert.Empty(frame.Journal);
    }
    [Fact]
    public async Task Stop_attempts_every_cached_page_once_and_preserves_cleanup_failure_on_final_release()
    {
        var frame=new Frame();var service=new NavigationService(frame,Routes());service.Navigate(ApplicationRoutes.Home);service.Navigate(ApplicationRoutes.Mounts);service.Navigate(ApplicationRoutes.Tasks);frame.Cache[typeof(MountMarker)].Broken=true;
        Assert.Throws<AggregateException>(service.StopPages);Assert.Throws<AggregateException>(service.StopPages);Assert.All(frame.Cache.Values,page=>Assert.Equal(1,page.Stops));
        await Assert.ThrowsAsync<AggregateException>(()=>service.DisposeAsync().AsTask());await Assert.ThrowsAsync<AggregateException>(()=>service.DisposeAsync().AsTask());Assert.Equal(1,frame.Disposals);Assert.Equal(0,frame.Subscribers);
    }
    [Fact]
    public async Task Async_release_waits_and_late_cached_page_is_stopped_without_new_selection()
    {
        var frame=new Frame();var waiting=new AsyncPage();frame.Cache[typeof(HomeMarker)]=waiting;var service=new NavigationService(frame,Routes());var selections=0;service.Navigated+=_=>selections++;service.Navigate(ApplicationRoutes.Home);
        var first=service.DisposeAsync().AsTask();var second=service.DisposeAsync().AsTask();Assert.Same(first,second);Assert.False(first.IsCompleted);Assert.Equal(1,waiting.Stops);
        var late=new Page();frame.Late(typeof(MountMarker),late);Assert.Equal(1,late.Stops);Assert.Equal(1,selections);Assert.False(service.Navigate(ApplicationRoutes.Settings));
        waiting.Release.SetResult();await first;await second;Assert.Equal(1,waiting.Releases);Assert.Equal(1,late.Stops);Assert.Equal(1,frame.Disposals);
    }
    [Fact]
    public async Task Home_commands_use_application_gateway_and_shutdown_rejects_forwarding()
    {
        var gateway=new NavigationGateway();var home=new HomeViewModel("0.0.8.10",gateway);Assert.False(home.OpenMounts());
        var frame=new Frame();await using var service=new NavigationService(frame,Routes());gateway.Bind(service);Assert.Throws<InvalidOperationException>(()=>gateway.Bind(service));
        Assert.True(home.OpenMounts());Assert.True(home.OpenSettings());Assert.True(home.OpenLogs());Assert.Equal(ApplicationRoutes.Logs,service.Current);Assert.Equal("版本 0.0.8.10",home.VersionText);
        gateway.RequestStop();Assert.False(home.OpenMounts());gateway.Unbind(service);Assert.Throws<InvalidOperationException>(()=>gateway.Bind(service));
    }
    [Fact]
    public void Production_navigation_has_no_page_factory_or_content_assignment_and_every_page_requires_frame_cache()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;Assert.NotNull(root);
        var folder=Path.Combine(root.FullName,"HybridMount");var window=File.ReadAllText(Path.Combine(folder,"Views","MainWindow.xaml.cs"));Assert.DoesNotContain("_pages",window);Assert.DoesNotContain("ContentFrame.Content",window);
        foreach(var name in new[]{"Home","Mounts","Tasks","Logs","Settings"})
        {
            Assert.DoesNotContain("new "+name+"Page(",window);var page=File.ReadAllText(Path.Combine(folder,"Pages",name+"Page.xaml.cs"));Assert.Contains("NavigationCacheMode.Required",page);Assert.DoesNotContain("MainWindow?.Navigate",page);
        }
        var adapter=File.ReadAllText(Path.Combine(folder,"Services","NavigationService.WinUI.cs"));Assert.Contains("_frame.Navigate(pageType,parameter)",adapter);Assert.DoesNotMatch(new Regex(@"\.Content\s*="),adapter);Assert.Contains("frame.CacheSize=5",adapter);
    }
}
