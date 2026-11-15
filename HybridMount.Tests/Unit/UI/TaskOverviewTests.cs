using HybridMount.Commons.Tasks;
using HybridMount.Models.Mounting;
using HybridMount.Models.Tasks;
using HybridMount.Services.Tasks;
namespace HybridMount.Tests.Unit.UI;

/// <summary>轮次统计消费真实生命周期契约，验证快速完成、迟到、重试和初始化并发，不使用任务页历史列表。</summary>
public sealed class TaskOverviewTests
{
    private sealed class Source : ITaskLifecycleSource
    {
        internal Func<TaskLifecycleCommit> Read = () => new(0, []);
        private long _revision;
        public event Action<TaskLifecycleCommit>? TaskCommitted;
        public TaskLifecycleCommit ReadUnfinishedTasks() => Read();
        internal void Emit(params TaskLifecycleEntry[] entries) => TaskCommitted?.Invoke(new(++_revision, entries));
        internal void Commit(TaskLifecycleCommit commit) { _revision = Math.Max(_revision, commit.Revision); TaskCommitted?.Invoke(commit); }
    }
    private static TaskLifecycleEntry Entry(string id, string state = "Queued", long attempt = 1, long order = 0) =>
        new(new(id, "download", state, 0, 1, [], null, ItemPath: id) { SubmissionOrder = order }, attempt, TaskHistoryRules.IsHistory(state));
    [Fact]
    public async Task EmptyWaitingClosedAndNextRoundReflectActualWork()
    {
        var source = new Source(); await using var overview = new TaskOverviewService(source); await overview.WhenInitialized;
        Assert.False(overview.Current.HasRound);
        source.Emit(Entry("a"), Entry("b", "WaitingCapacity"));
        Assert.Equal((2, 0, 0), (overview.Current.Total, overview.Current.Ended, overview.Current.RunningCount));
        source.Emit(Entry("a", "Completed")); Assert.False(overview.Current.IsClosed); Assert.Equal(50, overview.Current.Percent);
        source.Emit(Entry("b", "Canceled")); Assert.True(overview.Current.IsClosed); Assert.Equal(1, overview.Current.Canceled);
        var round = overview.Current.Round; source.Emit(Entry("c", "Running"));
        Assert.Equal(round + 1, overview.Current.Round); Assert.Equal(1, overview.Current.Total); Assert.Equal(0, overview.Current.Ended);
    }
    [Fact]
    public async Task MoreThanOneHundredFastTasksRemainInSameRoundAndDuplicateNotificationsDoNotAddRows()
    {
        var source = new Source(); await using var overview = new TaskOverviewService(source); await overview.WhenInitialized;
        source.Emit(Enumerable.Range(0, 240).Select(i => Entry("file-" + i)).ToArray());
        for (var i = 0; i < 240; i++) { source.Emit(Entry("file-" + i, "Completed")); source.Emit(Entry("file-" + i, "Completed")); }
        Assert.Equal(240, overview.Current.Total); Assert.Equal(240, overview.Current.Succeeded); Assert.Equal(1, overview.Current.Round);
        source.Emit(Entry("fresh")); source.Emit(Entry("file-0", "Completed"));
        Assert.Equal(1, overview.Current.Total); Assert.Equal(0, overview.Current.Ended);
    }
    [Fact]
    public async Task RetryWithdrawsTerminalResultWithinRoundAndStartsNewRoundAfterClosure()
    {
        var source = new Source(); await using var overview = new TaskOverviewService(source); await overview.WhenInitialized;
        source.Emit(Entry("hold"), Entry("retry")); source.Emit(Entry("retry", "Failed")); Assert.Equal(50, overview.Current.Percent);
        source.Emit(Entry("retry", "Running", 2)); Assert.Equal(0, overview.Current.Ended); Assert.Equal(0, overview.Current.Failed);
        source.Emit(Entry("retry", "Completed", 1)); Assert.Equal(0, overview.Current.Ended); // 迟到旧尝试即使较晚提交也不覆盖。
        source.Emit(Entry("retry", "Completed", 2), Entry("hold", "Completed")); Assert.True(overview.Current.IsClosed);
        var round = overview.Current.Round; source.Emit(Entry("retry", "Queued", 3));
        Assert.Equal(round + 1, overview.Current.Round); Assert.Equal(1, overview.Current.Total);
    }
    [Fact]
    public async Task NewTasksCanLowerProgressAndAllTerminalKindsHaveExplicitOutcomes()
    {
        var source = new Source(); await using var overview = new TaskOverviewService(source); await overview.WhenInitialized;
        var states = new[] { "Completed", "Failed", "PartialFailure", "Interrupted", "Canceled", "Ignored", "Superseded" };
        source.Emit(states.Select((_, i) => Entry(i.ToString())).Append(Entry("hold")).ToArray());
        source.Emit(states.Select((state, i) => Entry(i.ToString(), state)).ToArray());
        Assert.Equal((1, 3, 1, 2), (overview.Current.Succeeded, overview.Current.Failed, overview.Current.Canceled, overview.Current.Skipped));
        var before = overview.Current.Percent; source.Emit(Entry("added")); Assert.True(overview.Current.Percent < before); Assert.False(overview.Current.IsClosed);
    }
    [Theory]
    [InlineData("Conflict")][InlineData("OutcomeUnknown")][InlineData("PermissionBlocked")][InlineData("PermissionPending")]
    [InlineData("WaitingCapacity")][InlineData("Canceling")][InlineData("Deferred")][InlineData("FutureState")]
    public async Task UnresolvedAndUnknownStatesNeverEndTheRound(string state)
    {
        var source = new Source(); await using var overview = new TaskOverviewService(source); await overview.WhenInitialized;
        source.Emit(Entry("pending", state)); Assert.Equal(1, overview.Current.Unfinished); Assert.False(overview.Current.IsClosed);
    }
    [Fact]
    public async Task RunningPreviewUsesStableTaskOrderAndOnlyThreeRows()
    {
        var source = new Source(); await using var overview = new TaskOverviewService(source); await overview.WhenInitialized;
        source.Emit(Entry("wait"), Entry("late", "Running", order: 10), Entry("first", "Running", order: 1),
            Entry("third", "Running", order: 3), Entry("second", "Running", order: 2));
        Assert.Equal(new[] { "first", "second", "third" }, overview.Current.RunningTasks.Select(task => task.Id));
        Assert.Equal(1, overview.Current.AdditionalRunning);
        source.Emit(Entry("first", "Running", order: 1)); Assert.Equal("first", overview.Current.RunningTasks[0].Id);
    }
    [Fact]
    public async Task MergedMembersAreRemovedAtomicallyAndACompletedBatchCountsOnlyOnce()
    {
        var source = new Source(); await using var overview = new TaskOverviewService(source); await overview.WhenInitialized;
        source.Emit(Entry("member"));
        source.Emit(Entry("member") with { Removed = true }, Entry("batch"));
        Assert.Equal(1, overview.Current.Total); Assert.Equal(1, overview.Current.Round);
        source.Emit(Entry("batch", "Completed")); Assert.Equal(1, overview.Current.Succeeded);
    }
    [Fact]
    public async Task SeedExcludesOldHistoryButInitializationDoesNotDropObservedQuickCompletions()
    {
        var entered = new ManualResetEventSlim(); var release = new ManualResetEventSlim();
        var source = new Source { Read = () => { entered.Set(); release.Wait(); return new(2, [Entry("old", "Completed"), Entry("persisted", "Conflict")]); } };
        await using var overview = new TaskOverviewService(source); Assert.True(entered.Wait(5000));
        source.Emit(Entry("fast", "Running")); source.Emit(Entry("fast", "Completed")); release.Set(); await overview.WhenInitialized;
        Assert.Equal(2, overview.Current.Total); Assert.Equal(1, overview.Current.Succeeded); Assert.False(overview.Current.IsClosed);
    }
    [Fact]
    public async Task OutOfOrderAndReadFailuresPreserveLastSnapshot()
    {
        var source = new Source(); await using var overview = new TaskOverviewService(source); await overview.WhenInitialized;
        source.Commit(new(10, [Entry("a", "Running")])); source.Commit(new(9, [Entry("a", "Completed")]));
        Assert.Equal(0, overview.Current.Ended);
        source.Commit(new(11, [], "IOException (0x80070005)"));
        Assert.Equal(1, overview.Current.Total); Assert.NotNull(overview.Current.Error);
        source.Commit(new(12, [Entry("a", "Completed")])); Assert.Null(overview.Current.Error); Assert.Equal(1, overview.Current.Ended);
    }
    [Fact]
    public async Task FailedStartupDoesNotBecomeAFalseIdleSnapshotAndDisposalUnsubscribes()
    {
        var source = new Source { Read = () => throw new IOException("secret body must not escape") };
        var overview = new TaskOverviewService(source); await overview.WhenInitialized;
        Assert.Contains("IOException", overview.Current.Error); Assert.DoesNotContain("secret", overview.Current.Error);
        source.Emit(Entry("working")); await overview.WhenRecovered; Assert.NotNull(overview.Current.Error); Assert.Equal(1, overview.Current.Total);
        source.Read = () => new(2, [Entry("working"), Entry("recovered", "Conflict")]);
        source.Emit(Entry("working", "Running")); await overview.WhenRecovered;
        Assert.Null(overview.Current.Error); Assert.Equal(2, overview.Current.Total);
        await overview.DisposeAsync(); source.Emit(Entry("late")); Assert.Equal(2, overview.Current.Total);
    }
}
