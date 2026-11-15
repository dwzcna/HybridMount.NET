using HybridMount.Commons.Tasks;
using HybridMount.Commons.UI;
using HybridMount.Models;
using HybridMount.Models.Mounting;
using HybridMount.Models.Tasks;
using HybridMount.Services.Navigation;
using HybridMount.ViewModels;
namespace HybridMount.Tests.Unit.UI;

/// <summary>首页可见性、行复用、失败保留和全部任务导航；不读取仓库或运行任务。</summary>
public sealed class HomeOverviewViewModelTests
{
    private sealed class Overview : ITaskOverview
    {
        internal TaskOverviewSnapshot Value = TaskOverviewSnapshot.Empty with { Initialized = true };
        internal int Reads; internal bool Broken;
        public TaskOverviewSnapshot Current { get { Reads++; return Broken ? throw new IOException("fixture") : Value; } }
        public event Action? Changed;
        internal void Wake() => Changed?.Invoke();
    }
    private sealed class Dispatcher : IUIDispatcher
    { public bool TryEnqueue(Action action) { action(); return true; } }
    private sealed class Frame : INavigationFrame
    {
        public event Action<Type, object?>? Navigated;
        public event Action<Exception>? Failed { add { } remove { } }
        public bool CanGoBack => false;
        public bool Navigate(Type type, object? parameter) { Navigated?.Invoke(type, parameter); return true; }
        public bool GoBack() => false;
        public void Dispose() { }
    }
    [Fact]
    public async Task HiddenPageDoesNotReadOrPublishAndReturningUsesLatestSnapshot()
    {
        await using var navigation = new NavigationService(); var overview = new Overview(); var focus = new TaskFocusService(navigation);
        using var model = new HomeViewModel("0.0.23.1", navigation, overview, focus, new Dispatcher());
        overview.Wake(); Assert.Equal(0, overview.Reads); model.SetVisible(true); await model.WhenIdle;
        overview.Value = new(true, 1, 2, 1, 1, 0, 0, 0, 1, [new("run", "download", "Running", 0, 1, [], null)]); overview.Wake(); await model.WhenIdle;
        var row = Assert.Single(model.RunningTasks); Assert.Equal(50, model.ProgressPercent);
        overview.Value = overview.Value with { Ended = 0, Succeeded = 0 }; overview.Wake(); await model.WhenIdle; Assert.Same(row, Assert.Single(model.RunningTasks));
        model.SetVisible(false); var reads = overview.Reads; overview.Value = overview.Value with { Ended = 2, Succeeded = 2, RunningCount = 0, RunningTasks = [] }; overview.Wake(); await model.WhenIdle;
        Assert.Equal(reads, overview.Reads); Assert.Equal(0, model.ProgressPercent);
        model.SetVisible(true); await model.WhenIdle; Assert.Equal(100, model.ProgressPercent); Assert.Empty(model.RunningTasks);
    }
    [Fact]
    public async Task FailedReadRetainsPreviousRowsAndCountsInsteadOfPretendingIdle()
    {
        await using var navigation = new NavigationService(); var overview = new Overview { Value = new(true, 1, 4, 2, 0, 2, 0, 0, 1, [new("a", "upload", "Sending", 0, 1, [], null)]) };
        using var model = new HomeViewModel("0.0.23.1", navigation, overview, new TaskFocusService(navigation), new Dispatcher());
        model.SetVisible(true); await model.WhenIdle; overview.Broken = true; overview.Wake(); await model.WhenIdle;
        Assert.Equal(50, model.ProgressPercent); Assert.Single(model.RunningTasks); Assert.Contains("IOException", model.UpdateError);
        Assert.DoesNotContain("暂无任务", model.StateText);
    }
    [Fact]
    public async Task ViewAllTasksClearsEveryOldFocusAndNavigatesToAll()
    {
        await using var navigation = new NavigationService(new Frame(), Enum.GetValues<ApplicationPage>().ToDictionary(page => page, page => page switch { ApplicationPage.Home => typeof(string), ApplicationPage.Mounts => typeof(int), ApplicationPage.Tasks => typeof(double), ApplicationPage.Logs => typeof(decimal), _ => typeof(float) }));
        var focus = new TaskFocusService(navigation); focus.ShowIssues(Guid.NewGuid(), ["folder/file"]); focus.ShowTask("old-task");
        using var model = new HomeViewModel("0.0.23.1", navigation, new Overview(), focus, new Dispatcher());
        Assert.True(model.OpenTasks()); Assert.Equal(ApplicationPage.Tasks, navigation.Current); Assert.Null(focus.Current!.MountId);
        Assert.Empty(focus.Current.Paths); Assert.False(focus.Current.IssuesOnly); Assert.Null(focus.Current.TaskId);
    }
}
