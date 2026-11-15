using HybridMount.Commons.Settings;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Models.Mounting;
using HybridMount.Services.Tasks;
namespace HybridMount.Tests.Integration.Mounting;

/// <summary>使用独立SQLite验证已提交生命周期、历史上限、批次归属和启动恢复；不注册同步根、不访问远端。</summary>
public sealed class TaskOverviewPersistenceTests
{
    private sealed class Area : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("HybridMount.Overview.").FullName;
        internal MountRepository Repository { get; }
        internal Area() => Repository = new(Path, definitions: new Definitions());
        public void Dispose() => Directory.Delete(Path, true);
    }
    private sealed class Definitions : IMountDefinitionStore
    {
        public long CacheLimitBytes { get; set; }
        public IReadOnlyList<MountDefinition> LoadDefinitions() => [];
        public void SaveDefinition(MountDefinition definition) => throw new InvalidOperationException();
        public void DeleteDefinition(Guid id) => throw new InvalidOperationException();
    }
    private static MountOperationSnapshot TaskRow(string id, string state) => new(id, "download", state, state == "Completed" ? 1 : 0, 1, [], null);
    [Fact]
    public async Task HistoryCapAndCleanupNeverTruncateInProcessRoundAndRestartDoesNotRecountOldResults()
    {
        using var area = new Area(); var repository = area.Repository;
        await using var overview = new TaskOverviewService(repository); await overview.WhenInitialized;
        for (var i = 0; i < 160; i++) repository.SaveOperation(TaskRow("file-" + i, "Queued"));
        for (var i = 0; i < 160; i++) repository.SaveOperation(TaskRow("file-" + i, "Completed"));
        Assert.Equal(100, repository.ReadTaskState().History.Count);
        Assert.Equal(160, overview.Current.Total); Assert.Equal(160, overview.Current.Succeeded); Assert.True(overview.Current.IsClosed);
        Assert.Equal(160, repository.ClearOperations(TaskHistoryClearMode.Completed)); Assert.Equal(160, overview.Current.Succeeded);
        repository.SaveOperation(TaskRow("next", "Conflict"));
        await using var restarted = new TaskOverviewService(repository); await restarted.WhenInitialized;
        Assert.Equal(1, restarted.Current.Total); Assert.Equal(0, restarted.Current.Ended); Assert.Equal(1, restarted.Current.Round);
    }
    [Fact]
    public async Task RemoteParentAndIndependentHotDownloadMatchBusinessGrouping()
    {
        using var area = new Area(); var repository = area.Repository; var mount = Guid.NewGuid();
        await using var overview = new TaskOverviewService(repository); await overview.WhenInitialized;
        repository.SaveEntry(mount, new("hot", "hot", "hot", "v0", false, Materialized: true));
        repository.CommitDiscoveredBatch(mount, "scope", new(""), new[] { "cold-a", "cold-b", "hot" }
            .Select(path => new RemoteShadowEntry(new(path, path, false, 10, DateTimeOffset.UnixEpoch, "v1"))).ToArray(), [],
            detection: new("batch", mount, "scope", "connection"));
        Assert.Equal(1, overview.Current.Total);
        var pending = repository.AllRemotePending(mount); var hot = pending.Single(item => item.Path == "hot");
        repository.ClassifyRemotePending(hot, true); repository.FinishRemoteDetection("batch", "Completed");
        Assert.Equal(2, overview.Current.Total); Assert.Equal(0, overview.Current.Ended);
        foreach (var cold in pending.Where(item => item.Path != "hot")) repository.SaveRemotePending(cold with { State = "Completed" });
        Assert.Equal(1, overview.Current.Succeeded); Assert.False(overview.Current.IsClosed);
        hot = repository.FindRemotePending(hot.Id)!;
        repository.SaveRemotePending(hot with { State = "Downloading" }); Assert.Equal(hot.Id, Assert.Single(overview.Current.RunningTasks).Id);
        repository.SaveRemotePending(hot with { State = "Completed" }); Assert.Equal(2, overview.Current.Succeeded); Assert.True(overview.Current.IsClosed);
    }
    [Fact]
    public async Task PartialBatchWithUnresolvedMemberDoesNotCloseAndRetryWithdrawsOldOutcome()
    {
        using var area = new Area(); var repository = area.Repository; var mount = Guid.NewGuid();
        await using var overview = new TaskOverviewService(repository); await overview.WhenInitialized;
        repository.CommitDiscoveredBatch(mount, "scope", new(""), new[] { "a", "b" }.Select(path => new RemoteShadowEntry(new(path, path, false, 1, DateTimeOffset.UnixEpoch, "v1"))).ToArray(), [], detection: new("batch", mount, "scope", "c"));
        repository.FinishRemoteDetection("batch", "Completed");
        var pending = repository.AllRemotePending(mount);
        repository.SaveRemotePending(pending[0] with { State = "Failed", Error = "fixture" });
        Assert.False(overview.Current.IsClosed); Assert.Equal(0, overview.Current.Ended);
        repository.SaveRemotePending(pending[1] with { State = "Conflict" }); Assert.False(overview.Current.IsClosed);
        repository.SaveRemotePending(pending[1] with { State = "Completed" }); Assert.True(overview.Current.IsClosed); Assert.Equal(1, overview.Current.Failed);
        repository.ChangeRemoteBatch("batch", true); Assert.False(overview.Current.IsClosed); Assert.Equal(0, overview.Current.Failed);
    }
    [Fact]
    public async Task SupersededVersionsAndClassifyingOnlyDownloadDoNotLeavePhantomMembers()
    {
        using var area = new Area(); var repository = area.Repository; var mount = Guid.NewGuid();
        await using var overview = new TaskOverviewService(repository); await overview.WhenInitialized;
        repository.SaveEntry(mount, new("hot", "hot", "hot", "v0", false, Materialized: true));
        repository.CommitDiscoveredBatch(mount, "scope", new(""), [new(new("hot", "hot", false, 1, DateTimeOffset.UnixEpoch, "v1"))], [], detection: new("batch", mount, "scope", "c"));
        var hot = Assert.Single(repository.AllRemotePending(mount)); repository.ClassifyRemotePending(hot, true);
        Assert.Equal(1, overview.Current.Total); Assert.False(overview.Current.IsClosed);
        repository.CommitDiscoveredBatch(mount, "scope", new(""), [new(new("hot", "hot", false, 1, DateTimeOffset.UnixEpoch, "v2"))], []);
        Assert.Equal(2, overview.Current.Total); Assert.Equal(1, overview.Current.Skipped);
    }
    [Fact]
    public async Task AtomicAcceptanceRollbackAndBrokenSubscriberCannotCorruptCommittedStatistics()
    {
        using var area = new Area(); var repository = area.Repository;
        repository.TaskCommitted += _ => throw new IOException("subscriber failure");
        await using var overview = new TaskOverviewService(repository); await overview.WhenInitialized;
        repository.AcceptApplicationTask(TaskRow("accepted", "Queued"), ["OperationStageExecute"]);
        Assert.Equal(1, overview.Current.Total);
        Assert.ThrowsAny<Exception>(() => repository.AcceptApplicationTask(TaskRow("accepted", "Running"), ["OperationStageExecute"]));
        Assert.Equal(1, overview.Current.Total); Assert.Equal(0, overview.Current.Ended);
        repository.SaveOperation(TaskRow("accepted", "Completed")); Assert.Equal(1, overview.Current.Succeeded);
    }
    [Fact]
    public async Task ThousandMemberLifecycleUpdatesLoadActiveBatchOnlyOnce()
    {
        using var area = new Area(); var repository = area.Repository; var mount = Guid.NewGuid();
        await using var overview = new TaskOverviewService(repository); await overview.WhenInitialized;
        for (var offset = 0; offset < 1000; offset += 250)
            repository.CommitDiscoveredBatch(mount, "scope", new(""), Enumerable.Range(offset, 250)
                .Select(i => new RemoteShadowEntry(new("id-" + i, "file-" + i, false, 1, DateTimeOffset.UnixEpoch, "v1"))).ToArray(), [], detection: new("batch", mount, "scope", "connection"));
        foreach (var member in repository.AllRemotePending(mount)) repository.SaveRemotePending(member with { State = "Completed" });
        Assert.Equal(1, repository.TaskLifecycleBatchLoads);
        repository.FinishRemoteDetection("batch", "Completed");
        Assert.Equal(1, overview.Current.Total); Assert.Equal(1, overview.Current.Succeeded); Assert.True(overview.Current.IsClosed);
    }
    [Fact]
    public async Task DownloadOnlyBatchDoesNotReloadAllMembersForEveryIndependentProgressUpdate()
    {
        using var area = new Area(); var repository = area.Repository; var mount = Guid.NewGuid();
        await using var overview = new TaskOverviewService(repository); await overview.WhenInitialized;
        repository.CommitDiscoveredBatch(mount, "scope", new(""), Enumerable.Range(0, 100)
            .Select(i => new RemoteShadowEntry(new("id-" + i, "file-" + i, false, 1, DateTimeOffset.UnixEpoch, "v1"))).ToArray(), [], detection: new("batch", mount, "scope", "connection"));
        foreach (var member in repository.AllRemotePending(mount)) repository.ClassifyRemotePending(member, true);
        repository.FinishRemoteDetection("batch", "Completed");
        foreach (var member in repository.AllRemotePending(mount))
        {
            repository.SaveRemotePending(member with { State = "Downloading" });
            repository.SaveRemotePending(member with { State = "Completed" });
        }
        Assert.Equal(1, repository.TaskLifecycleBatchLoads); Assert.Equal(100, overview.Current.Total);
        Assert.Equal(100, overview.Current.Succeeded); Assert.True(overview.Current.IsClosed);
    }
    [Fact]
    public async Task ProjectionFailureRetainsOldCountsAndNextCommitReplaysEveryMissedId()
    {
        using var area = new Area(); var repository = area.Repository;
        await using var overview = new TaskOverviewService(repository); await overview.WhenInitialized;
        repository.SaveOperation(TaskRow("hold", "Queued"));
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = System.IO.Path.Combine(area.Path, "mounts.db"), Pooling = false }.ToString());
        db.Open(); MountDatabase.ConfigurePathComparison(db);
        using var query = db.CreateCommand(); query.CommandText = "INSERT INTO uploads VALUES('broken','fixture','file',$json)";
        query.Parameters.AddWithValue("$json", """{"Generation":"invalid"}"""); query.ExecuteNonQuery();
        repository.SaveOperation(TaskRow("broken", "Queued"));
        Assert.Equal(1, overview.Current.Total); Assert.NotNull(overview.Current.Error);
        query.CommandText = "DELETE FROM uploads WHERE id='broken'"; query.ExecuteNonQuery();
        repository.SaveOperation(TaskRow("hold", "Running"));
        Assert.Null(overview.Current.Error); Assert.Equal(2, overview.Current.Total); Assert.False(overview.Current.IsClosed);
    }
    [Fact]
    public async Task LateRemoteAttemptCannotOverwriteNewAttemptOrFinishTheRound()
    {
        using var area = new Area(); var repository = area.Repository;
        await using var overview = new TaskOverviewService(repository); await overview.WhenInitialized;
        var pending = new RemotePendingChange("remote", Guid.NewGuid(), "scope", "file", new("file", "file", false, 1, DateTimeOffset.UnixEpoch, "v1"), AttemptId: "new", AttemptGeneration: 2);
        repository.SaveRemotePending(pending);
        repository.SaveRemotePendingCurrent(pending with { AttemptId = "old", AttemptGeneration = 1, State = "Completed" });
        Assert.Equal(0, overview.Current.Ended); Assert.False(overview.Current.IsClosed);
        repository.SaveRemotePendingCurrent(pending with { State = "Completed" }); Assert.Equal(1, overview.Current.Succeeded);
    }
}
