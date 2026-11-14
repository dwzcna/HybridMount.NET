using System.Diagnostics;
using System.Text.Json;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Models.Persistence;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace HybridMount.Tests;

/// <summary>纯SQLite夹具验证点查、映射优先顺序和过滤等价性；不注册同步根，不访问正文或云端。</summary>
public sealed class RepositoryHotPathTests(ITestOutputHelper output)
{
    [Fact]
    public void MaterializedExistenceAndFileCandidatesKeepDeletedAndRemoteSemantics()
    {
        using var test = new Fixture();
        var mount = Guid.NewGuid();
        Assert.False(test.Repository.HasMaterializedEntries(mount));
        test.Repository.SaveEntry(mount, new("cold", "cold", "cold", "v", false, Materialized: false));
        Assert.False(test.Repository.HasMaterializedEntries(mount));
        test.Repository.SaveEntry(mount, new("tombstone", "gone", "gone", "v", false, Deleted: true, Materialized: true));
        Assert.True(test.Repository.HasMaterializedEntries(mount));
        test.Repository.SaveEntry(mount, new("directory", "dir", "dir", "v", true, Materialized: true));
        test.Repository.SaveEntry(mount, new("present", "present", "present", "v", false, Materialized: true));
        test.Repository.SaveEntry(mount, new("remote-gone", "remote-gone", "remote-gone", "v", false, Materialized: true));
        Assert.True(test.Repository.SetRemotePresence(mount, test.Repository.EntryById(mount, "remote-gone")!, RemotePresence.Deleted));
        var expected = test.Repository.Entries(mount).Where(row => row.Materialized && !row.Deleted && !row.IsDirectory).OrderBy(row => row.Id).ToArray();
        Assert.Equal(expected, test.Repository.MaterializedFiles(mount).OrderBy(row => row.Id).ToArray());
        Assert.False(test.Repository.HasMaterializedEntries(Guid.NewGuid()));
    }

    [Fact]
    public void ExactSourceAndUploadMappingPreserveCaseTombstonesAndPreferredIdentity()
    {
        using var test = new Fixture();
        var mount = Guid.NewGuid();
        var rows = new[]
        {
            new MountEntry("a", "Folder/File", "Folder/File", "active", false),
            new MountEntry("z", "Folder/File", "Folder/File", "deleted", false, Deleted: true),
            new MountEntry("b", "folder/file", "folder/file", "case", false),
            new MountEntry("c", "Folder/File", "elsewhere", "renamed", false),
            new MountEntry("d", "only-gone", "only-gone", "v", false, Deleted: true)
        };
        foreach (var row in rows) test.Repository.SaveEntry(mount, row);
        foreach (var source in new[] { "Folder/File", "folder/file", "FOLDER/FILE", "missing", "only-gone" })
        {
            var snapshot = test.Repository.Entries(mount);
            Assert.Equal(snapshot.FirstOrDefault(row => row.SourcePath == source), test.Repository.EntryByExactSource(mount, source));
            foreach (var preferred in new string?[] { null, "z", "a", "b", "c", "d", "missing" })
            {
                var expected = snapshot.FirstOrDefault(row => row.Id == preferred && row.SourcePath == source && row.LocalPath == source)
                    ?? snapshot.FirstOrDefault(row => !row.Deleted && row.SourcePath == source && row.LocalPath == source);
                Assert.Equal(expected, test.Repository.EntryForUpload(mount, preferred, source, source));
            }
        }
        Assert.Null(test.Repository.EntryById(mount, null));
        Assert.Null(test.Repository.EntryById(Guid.NewGuid(), "a"));
    }

    [Fact]
    public void DeletedAncestorQueriesDoNotBroadenOrdinalSourceBarriers()
    {
        using var test = new Fixture();
        var mount = Guid.NewGuid();
        test.Repository.SaveEntry(mount, new("parent", "Folder", "Folder", "v", true));
        Assert.True(test.Repository.SetRemotePresence(mount, test.Repository.EntryById(mount, "parent")!, RemotePresence.Deleted));
        test.Repository.SaveEntry(mount, new("deleted-local", "other", "other", "v", true, Deleted: true));
        var longPath = "Folder/" + string.Join('/', Enumerable.Repeat("x", 600));
        foreach (var path in new[] { "Folder", "Folder/child", "folder/child", "Folder2/child", "other/child", "", longPath })
            Assert.Equal(UploadRules.DeletedSourceAncestor(test.Repository.Entries(mount), path) is not null,
                test.Repository.HasDeletedSourceAncestor(mount, path));
        Assert.False(test.Repository.HasDeletedSourceAncestor(Guid.NewGuid(), "Folder/child"));
    }

    [Fact]
    public void UploadFiltersAndCountsMatchModelRulesAcrossMountsAndRetryStates()
    {
        using var test = new Fixture();
        var mount = Guid.NewGuid();
        var other = Guid.NewGuid();
        var states = new[] { "Queued", "Waiting", "Prepared", "Sending", "RemoteCommitted", "OutcomeUnknown", "Failed", "Conflict", "Canceled", "Ignored", "Completed" };
        foreach (var id in new[] { mount, other })
            for (var index = 0; index < states.Length; index++)
            {
                var state = states[index];
                var item = new LocalUpload(id.ToString("N") + index, id, index % 2 == 0 ? "File" : "file", "source", false,
                    "stamp", 1, state, DateTimeOffset.UtcNow, Error: UploadAttentionRules.IsFailureState(
                        new("probe", id, "File", "source", false, "stamp", 1, state, DateTimeOffset.UtcNow)) ? "reason" : null);
                test.Repository.SaveUpload(item);
                if (state == "Failed") test.Repository.UpdateUpload(item.Id, row => row with { State = "Queued", Error = null });
            }
        foreach (var id in new Guid?[] { null, mount, other, Guid.NewGuid() })
        {
            var all = test.Repository.Uploads(id);
            Assert.Equal(all.Where(UploadRules.Unfinished).ToArray(), test.Repository.UnfinishedUploads(id).ToArray());
            Assert.Equal(all.Where(UploadAttentionRules.NeedsAttention).ToArray(), test.Repository.UploadsNeedingAttention(id).ToArray());
            if (id is {} value)
            {
                Assert.Equal(all.Count(UploadAttentionRules.NeedsAttention), test.Repository.UploadAttentionCount(value));
                Assert.Equal(all.Where(row => row.LocalPath == "File").ToArray(), test.Repository.UploadsAtPath(value, "File").ToArray());
                Assert.Equal(all.Where(row => row.LocalPath == "file").ToArray(), test.Repository.UploadsAtPath(value, "file").ToArray());
            }
        }
        var counts = test.Repository.Uploads().Where(UploadAttentionRules.NeedsAttention).GroupBy(row => row.MountId).ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(counts.OrderBy(pair => pair.Key), test.Repository.UploadAttentionCounts().OrderBy(pair => pair.Key));
    }

    [Fact]
    public void AttentionFilteringAvoidsParsingUnrelatedCompletedHistory()
    {
        using var test = new Fixture();
        var mount = Guid.NewGuid();
        test.Repository.SaveUpload(new("failed", mount, "file", "file", false, "stamp", 1, "Failed", DateTimeOffset.UtcNow, Error: "reason"));
        using (var db = test.Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "INSERT INTO uploads VALUES('unrelated',$m,'other',$j)";
            command.Parameters.AddWithValue("$m", mount.ToString("N"));
            command.Parameters.AddWithValue("$j", "{\"State\":\"Completed\",\"Generation\":\"unrelated-invalid-model\"}");
            command.ExecuteNonQuery();
        }
        Assert.Single(test.Repository.UnfinishedUploads(mount));
        Assert.Single(test.Repository.UploadsNeedingAttention(mount));
        Assert.Equal(1, test.Repository.UploadAttentionCount(mount));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    public void QueryPlansAndWarmMeasurementsUseIndependentDatabase(int count)
    {
        using var test = new Fixture();
        var mount = Guid.NewGuid();
        foreach (var batch in Enumerable.Range(0, count).Select(index => new MountEntry(index.ToString("D8"), "folder/" + index, "folder/" + index, "v", false, Materialized: index % 2 == 0)).Chunk(256))
            test.Repository.SaveEntries(batch, mount);
        test.Repository.SaveUpload(new("failed", mount, "file", "file", false, "stamp", 1, "Failed", DateTimeOffset.UtcNow, Error: "reason"));
        using var db = test.Open();
        string Plan(string sql)
        {
            using var command = db.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            command.Parameters.AddWithValue("$m", mount.ToString("N"));
            command.Parameters.AddWithValue("$id", "00000000");
            command.Parameters.AddWithValue("$p", "file");
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read()) rows.Add(reader.GetString(3));
            return string.Join("; ", rows);
        }
        var materializedPlan = Plan("SELECT EXISTS(SELECT 1 FROM entries WHERE mount=$m AND materialized=1)");
        var attentionPlan = Plan("SELECT COUNT(*) FROM uploads WHERE mount=$m AND " + MountRepository.UploadAttentionSql);
        var pathPlan = Plan("SELECT json FROM uploads WHERE mount=$m AND path=$p ORDER BY rowid DESC");
        Assert.Contains("entries_materialized", materializedPlan);
        Assert.Contains("uploads_attention", attentionPlan);
        Assert.Contains("uploads_local_path", pathPlan);
        double[] Measure(Action action)
        {
            for (var index = 0; index < 5; index++) action();
            var values = new double[100];
            for (var index = 0; index < values.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                action();
                values[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            Array.Sort(values);
            return values;
        }
        object Stats(double[] values) => new { p50 = values[49], p95 = values[94], p99 = values[98] };
        void SelectOne(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            Assert.Equal(1L, command.ExecuteScalar());
        }
        var full = Measure(() => Assert.NotNull(test.Repository.Entries(mount).FirstOrDefault(row => row.Id == "00000000")));
        var point = Measure(() => Assert.NotNull(test.Repository.EntryById(mount, "00000000")));
        var open = Measure(() => { using var connection = test.Open(); SelectOne(connection); });
        var reuse = Measure(() => SelectOne(db));
        var metrics = new { count, samples = 100, fullIndex = Stats(full), pointLookup = Stats(point), openConfigureSelect = Stats(open), reusedConnectionSelect = Stats(reuse), defaultTimeoutSeconds = db.DefaultTimeout,
            materializedPlan, attentionPlan, pathPlan };
        var json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        if (Environment.GetEnvironmentVariable("HYBRIDMOUNT_QUERY_METRICS") is {} directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "queries-" + count + ".json"), json);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _area = Directory.CreateTempSubdirectory("HybridMount.HotQueries.").FullName;
        internal MountRepository Repository { get; }
        internal Fixture() => Repository = new(Path.Combine(_area, "data"), definitions: new Definitions());
        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Repository.DataDirectory, "mounts.db"), Pooling = false }.ToString());
            try { connection.Open(); MountDatabase.ConfigurePathComparison(connection); return connection; }
            catch { connection.Dispose(); throw; }
        }
        public void Dispose() => Directory.Delete(_area, true);
    }
    private sealed class Definitions : IMountDefinitionStore
    {
        public IReadOnlyList<MountDefinition> LoadDefinitions() => [];
        public void SaveDefinition(MountDefinition definition) { }
        public void DeleteDefinition(Guid id) { }
        public long CacheLimitBytes { get; set; }
    }
}
