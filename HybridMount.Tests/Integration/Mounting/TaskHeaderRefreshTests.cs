using HybridMount.Commons.Settings;
using HybridMount.Commons.UI;
using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.Services.Navigation;
using HybridMount.ViewModels;
namespace HybridMount.Tests.Integration.Mounting;

/// <summary>真实仓库和任务快照验证普通进度不发布页头布局属性；筛选及终态仍及时更新。</summary>
public sealed class TaskHeaderRefreshTests
{
    private sealed class Definitions : IMountDefinitionStore
    {
        public long CacheLimitBytes { get; set; }
        public IReadOnlyList<MountDefinition> LoadDefinitions() => [];
        public void SaveDefinition(MountDefinition definition) => throw new NotSupportedException();
        public void DeleteDefinition(Guid id) => throw new NotSupportedException();
    }
    private sealed class Dispatcher : IUIDispatcher
    { public bool TryEnqueue(Action callback) { callback(); return true; } }
    [Fact]
    public async Task ProgressKeepsHeaderAndCommandsQuietButFilterAndCompletionStillNotify()
    {
        var area = Directory.CreateTempSubdirectory("HybridMount.HeaderRefresh.").FullName;
        try
        {
            var repository = new MountRepository(Path.Combine(area, "data"), definitions: new Definitions());
            await using var logger = new LoggerService(Path.Combine(area, "logs.db"));
            await using var manager = new MountManager(repository, logger);
            await using var navigation = new NavigationService(); var focus = new TaskFocusService(navigation);
            var item = new LocalUpload("work", Guid.NewGuid(), "file", "file", false, "stamp", 1, "Sending", DateTimeOffset.UtcNow, Total: 100);
            repository.SaveUpload(item); repository.SaveOperation(item.ToSnapshot());
            using var model = new TasksViewModel(manager.TasksQueue, new Dispatcher(), focus);
            await model.SnapshotIdle; model.SelectedJobId = item.Id;
            var row = Assert.Single(model.Operations); var notices = new List<string?>(); model.PropertyChanged += (_, args) => notices.Add(args.PropertyName);
            for (var i = 1; i <= 6; i++)
            {
                item = item with { Bytes = i }; repository.SaveUpload(item); repository.SaveOperation(item.ToSnapshot());
                manager.TasksQueue.SetConcurrency(4); await model.SnapshotIdle;
            }
            Assert.Same(row, Assert.Single(model.Operations)); Assert.Equal(6, row.BytesTransferred);
            Assert.DoesNotContain(nameof(TasksViewModel.HasIssueFilter), notices); Assert.DoesNotContain(nameof(TasksViewModel.IssueViewTitle), notices);
            Assert.DoesNotContain(nameof(TasksViewModel.CanCancelSelected), notices); Assert.DoesNotContain(nameof(TasksViewModel.CanClearAll), notices);
            Assert.True(model.CanCancelSelected);
            item = item with { State = "Completed" }; repository.SaveUpload(item); repository.SaveOperation(item.ToSnapshot());
            manager.TasksQueue.SetConcurrency(4); await model.SnapshotIdle;
            Assert.False(model.CanCancelSelected); Assert.Contains(nameof(TasksViewModel.CanCancelSelected), notices); Assert.True(model.CanClearCompleted);
            notices.Clear(); focus.ShowIssues(item.MountId); await model.SnapshotIdle;
            Assert.True(model.HasIssueFilter); Assert.Contains(nameof(TasksViewModel.HasIssueFilter), notices);
            notices.Clear(); focus.ShowAll(); await model.SnapshotIdle;
            Assert.False(model.HasIssueFilter); Assert.Contains(nameof(TasksViewModel.HasIssueFilter), notices);
        }
        finally { Directory.Delete(area, true); }
    }
}
