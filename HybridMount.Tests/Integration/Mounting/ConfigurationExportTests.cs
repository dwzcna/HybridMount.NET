using HybridMount.Models.Protocols.S3;
using HybridMount.Commons.Persistence;
using HybridMount.Commons.Protocols;
using HybridMount.Commons.Settings;
using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Infrastructure.Protocols.S3;
using HybridMount.Models.Configuration;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.Services.Navigation;
using HybridMount.Tests.Integration.Protocols;
using HybridMount.ViewModels;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HybridMount.Tests.Integration.Mounting;

/// <summary>所有配置、凭据、文件与HTTP源均隔离；导出不得建立协议源或借用表单取消所有权。</summary>
public sealed class ConfigurationExportTests
{
    [Theory(Timeout=30000)]
    [InlineData("OperationStageReadConfiguration")]
    [InlineData("OperationStageEncodeBackup")]
    [InlineData("OperationStageWriteBackup")]
    public async Task AcceptedExportOutlivesFormAndCompletionWaitsForAtomicCommit(string blockedStage)
    {
        await using var test = new Area();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.ConfigurationExportCheckpointForTests = async (_, stage, token) =>
        { if (stage == blockedStage) { reached.TrySetResult(); await release.Task.WaitAsync(token); } };
        using var form = new CancellationTokenSource();
        var accepted = await test.Manager.AcceptConfigurationExportAsync([test.Id], "secret-backup-password", test.Target, form.Token).WaitAsync(TimeSpan.FromSeconds(2));
        form.Cancel(); await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Running", test.Manager.GetOperation(accepted.TaskId)!.State);
        Assert.False(File.Exists(test.Target));
        Assert.Equal("Running", Assert.Single(test.Repository.OperationRuns(accepted.TaskId)).Steps.Single(item => item.Name == blockedStage).State);
        Assert.DoesNotContain("secret-backup-password", JsonSerializer.Serialize(test.Repository.LoadOperations()));
        release.TrySetResult();
        var finished = await WebDavTests.Finished(test.Manager, accepted.TaskId);
        Assert.Equal("Completed", finished.State); Assert.Null(finished.MountId);
        Assert.Equal(test.Target, test.Manager.ConfigurationExportLocation(accepted.TaskId));
        Assert.All(Assert.Single(test.Repository.OperationRuns(accepted.TaskId)).Steps, item => Assert.Equal("Completed", item.State));
        var restored = await test.Manager.ReadConfigurationBackupAsync(test.Target, "secret-backup-password");
        Assert.Equal("account-secret", Assert.Single(restored.Mounts).Credentials.UserName);
        Assert.Equal(0, test.Factory.Calls);
    }
    [Fact(Timeout=30000)]
    public async Task CancelImmediatelyBeforeCommitKeepsOriginalAndCleansTemporaryFile()
    {
        await using var test = new Area(); await File.WriteAllTextAsync(test.Target, "original");
        var active = "";
        test.Manager.ConfigurationExportCheckpointForTests = (id, _, _) => { active = id; return Task.CompletedTask; };
        test.Manager.ConfigurationExportBeforeCommitForTests = () => Assert.True(test.Manager.Cancel(active));
        var accepted = await test.Manager.AcceptConfigurationExportAsync([test.Id], "", test.Target);
        var finished = await WebDavTests.Finished(test.Manager, accepted.TaskId);
        Assert.Equal("Canceled", finished.State); Assert.Equal("original", await File.ReadAllTextAsync(test.Target));
        Assert.Empty(Directory.GetFiles(test.Directory, "*.tmp", SearchOption.AllDirectories)); Assert.False(test.Manager.Cancel(accepted.TaskId));
    }
    [Fact(Timeout=30000)]
    public async Task AtomicReplaceFailureReportsActualStageAndKeepsOriginal()
    {
        await using var test = new Area(); await File.WriteAllTextAsync(test.Target, "original");
        using (var held = new FileStream(test.Target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var accepted = await test.Manager.AcceptConfigurationExportAsync([test.Id], "", test.Target);
            var finished = await WebDavTests.Finished(test.Manager, accepted.TaskId);
            Assert.Equal("Failed", finished.State); Assert.Equal("OperationStageCommitBackup", finished.FailureStage); Assert.NotNull(finished.FailureHResult);
        }
        Assert.Equal("original", await File.ReadAllTextAsync(test.Target)); Assert.Empty(Directory.GetFiles(test.Directory, "*.tmp", SearchOption.AllDirectories));
    }
    [Fact(Timeout=30000)]
    public async Task InvalidInputsAndFailedAcceptanceCreateNoTaskAndReleaseTargetReservation()
    {
        await using var test = new Area();
        await Assert.ThrowsAsync<ArgumentException>(() => test.Manager.AcceptConfigurationExportAsync([], "", test.Target));
        await Assert.ThrowsAsync<ArgumentException>(() => test.Manager.AcceptConfigurationExportAsync([test.Id], "short", test.Target));
        await Assert.ThrowsAsync<InvalidDataException>(() => test.Manager.AcceptConfigurationExportAsync([test.Id], "", test.Target + ".old"));
        using (var db = new SqliteConnection("Pooling=false;Data Source=" + Path.Combine(test.Repository.DataDirectory, "mounts.db")))
        {
            db.Open(); MountDatabase.ConfigurePathComparison(db);
            using var command = db.CreateCommand(); command.CommandText = "CREATE TRIGGER reject_export BEFORE INSERT ON operation_steps BEGIN SELECT RAISE(ABORT,'isolated export failure'); END"; command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<SqliteException>(() => test.Manager.AcceptConfigurationExportAsync([test.Id], "", test.Target));
        Assert.Empty(test.Repository.LoadOperations());
        using (var db = new SqliteConnection("Pooling=false;Data Source=" + Path.Combine(test.Repository.DataDirectory, "mounts.db")))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "DROP TRIGGER reject_export"; command.ExecuteNonQuery(); }
        var accepted = await test.Manager.AcceptConfigurationExportAsync([test.Id], "", test.Target);
        Assert.Equal("Completed", (await WebDavTests.Finished(test.Manager, accepted.TaskId)).State);
    }
    [Theory(Timeout=30000)]
    [InlineData(false)][InlineData(true)]
    public async Task DuplicateTargetIsRejectedAndCancelOrShutdownReleasesWork(bool shutdown)
    {
        await using var test = new Area(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.ConfigurationExportCheckpointForTests = async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        var accepted = await test.Manager.AcceptConfigurationExportAsync([test.Id], "", test.Target); await entered.Task;
        await Assert.ThrowsAsync<IOException>(() => test.Manager.AcceptConfigurationExportAsync([test.Id], "", test.Target)); Assert.Single(test.Repository.LoadOperations());
        if (shutdown) test.Manager.RequestStop(); else Assert.True(test.Manager.Cancel(accepted.TaskId));
        Assert.Equal("Canceled", (await WebDavTests.Finished(test.Manager, accepted.TaskId)).State); Assert.False(File.Exists(test.Target)); Assert.Equal(0, test.Factory.Calls);
    }
    [Theory]
    [InlineData(FileSourceKind.WebDav)][InlineData(FileSourceKind.S3)][InlineData(FileSourceKind.Synology)]
    public void BackupBecomesCompleteIndependentDraftEvenWithInvalidLocalPath(FileSourceKind kind)
    {
        var rules = FileExclusionRules.Defaults().ToList();
        var sqs = kind == FileSourceKind.S3 ? new SqsConfiguration(true,"https://sqs.example.test/queue","test-region",SqsCredentialMode.SeparateKeys) : null;
        var item = new MountBackupItem("imported", "not a valid existing directory", new(kind,"https://server.example.test:8443/", "bucket", "/share/folder", "region", false, false, false, new string('A',64)),
            new("user","password", "access","secret","token"),false,true,false,125,455,sqs,sqs is null?null:new(AccessKey:"queue-access",SecretKey:"queue-secret",SessionToken:"queue-token"),false,12345678901,rules);
        var draft = ConnectionSubmissionDraft.FromBackup(item); rules.Clear();
        Assert.Null(draft.ExistingId); Assert.Equal(item.Name,draft.Name); Assert.Equal(item.LocalPath,draft.Path); Assert.Equal(item.Source,draft.Source);
        Assert.Equal(item.Credentials,draft.Credentials); Assert.Equal(item.ExclusionRules,draft.Exclusions);
        Assert.Equal(new MountCreationOptions(true,false,125,455,false,12345678901,sqs,item.SqsCredentials),draft.Options);
        Assert.DoesNotContain("password",draft.ToString());
    }
    [Fact(Timeout=30000)]
    public async Task RestartShowsInterruptedExportAndRequiresFreshProtectedExport()
    {
        await using var test = new Area(); await test.Manager.DisposeAsync();
        var id = Guid.NewGuid().ToString("N");
        test.Repository.AcceptApplicationTask(new(id,"exportConfiguration","Running",0,1,[],null,ItemPath:test.Target){BackupProtected=true},["OperationStageReadConfiguration","OperationStageEncodeBackup","OperationStageWriteBackup","OperationStageCommitBackup"]);
        await using var restarted = new MountManager(test.Repository,test.Logger,test.Factory);
        Assert.Equal("Interrupted",restarted.GetOperation(id)!.State);
        Assert.Equal("Interrupted",Assert.Single(test.Repository.OperationRuns(id)).State);
        Assert.False(File.Exists(test.Target)); Assert.Equal(0,test.Factory.Calls);
        var focus=new TaskFocusService(new NavigationService());
        Assert.True(TasksViewModel.CanReexport(restarted.GetOperation(id)!));
        focus.ShowExport(restarted.GetOperation(id)!.BackupProtected==true);
        Assert.True(focus.TakeExportProtection()); Assert.Null(focus.TakeExportProtection());
        Assert.Equal(test.Target,restarted.ConfigurationExportLocation(id));
    }
    [Theory(Timeout=30000)]
    [InlineData("")][InlineData("protected-backup-password")]
    public async Task OfflineTaskPreservesS3AndSeparateQueueCredentials(string password)
    {
        await using var test = new Area(); await test.Manager.DisposeAsync();
        var previous=Assert.Single(test.Repository.LoadDefinitions());
        var source=new FileSourceConfiguration(FileSourceKind.S3,"https://s3.example.test","bucket","folder/prefix","us-west-2",false);
        var sqs=new SqsConfiguration(true,"https://sqs.us-west-2.amazonaws.com/123456789012/test","us-west-2",SqsCredentialMode.SeparateKeys);
        var definition=previous with{Source=source,Sqs=sqs,ThumbnailsEnabled=true,RemoteAutoDetectEnabled=false,RemoteIncrementalSeconds=125,RemoteFullSeconds=455,MaximumCapacityBytes=12345678901,ExclusionRules=FileExclusionRules.Defaults()};
        test.Repository.SaveDefinition(definition);
        var main=new SourceCredentials(AccessKey:"main-access",SecretKey:"main-secret",SessionToken:"main-token");
        var queue=new SourceCredentials(AccessKey:"queue-access",SecretKey:"queue-secret",SessionToken:"queue-token");
        new CredentialStore(test.Repository.DataDirectory).Save(test.Id,main);
        FileSourceFactory.SaveAdditionalCredentials(definition,queue,test.Repository.DataDirectory);
        await using var manager=new MountManager(test.Repository,test.Logger,test.Factory);
        var accepted=await manager.AcceptConfigurationExportAsync([test.Id],password,test.Target);
        var completed=await WebDavTests.Finished(manager,accepted.TaskId);
        Assert.Equal("Completed",completed.State); Assert.Equal(password.Length>0,completed.BackupProtected);
        var restored=Assert.Single((await manager.ReadConfigurationBackupAsync(test.Target,password)).Mounts);
        Assert.Equal(source,restored.Source); Assert.Equal(main,restored.Credentials); Assert.Equal(sqs,restored.Sqs); Assert.Equal(queue,restored.SqsCredentials);
        Assert.Equal(definition.ExclusionRules,restored.ExclusionRules); Assert.True(restored.ThumbnailsEnabled); Assert.False(restored.AutoConnect); Assert.False(restored.RemoteAutoDetectEnabled);
        Assert.Equal(125,restored.RemoteIncrementalSeconds); Assert.Equal(455,restored.RemoteFullSeconds); Assert.Equal(12345678901,restored.MaximumCapacityBytes); Assert.Equal(0,test.Factory.Calls);
        var persisted=JsonSerializer.Serialize(test.Repository.LoadOperations());
        foreach(var secret in new[]{"main-access","main-secret","main-token","queue-access","queue-secret","queue-token", "protected-backup-password"})Assert.DoesNotContain(secret,persisted);
        if(password.Length>0)Assert.DoesNotContain("main-secret",await File.ReadAllTextAsync(test.Target));
    }
    private sealed class Factory : IFileSourceFactory
    {
        internal int Calls;
        public Task<IFileSource> CreateAsync(MountDefinition mount, IMountRepository repository, bool recovery, CancellationToken token) { Calls++; throw new IOException("Export cannot access source"); }
    }
    private sealed class Store : IMountDefinitionStore
    {
        internal readonly List<MountDefinition> Definitions = [];
        public long CacheLimitBytes { get; set; }
        public IReadOnlyList<MountDefinition> LoadDefinitions() => Definitions.ToArray();
        public void SaveDefinition(MountDefinition value) { Definitions.RemoveAll(item => item.Id == value.Id); Definitions.Add(value); }
        public void DeleteDefinition(Guid id) => Definitions.RemoveAll(item => item.Id == id);
    }
    private sealed class Area : IAsyncDisposable
    {
        internal readonly string Directory = Path.Combine(Path.GetTempPath(),"HybridMount.ExportTaskTests",Guid.NewGuid().ToString("N"));
        internal string Target => Path.Combine(Directory,"backup.hmxbk"); internal readonly Guid Id = Guid.NewGuid();
        internal readonly MountRepository Repository; internal readonly MountManager Manager; internal readonly LoggerService Logger; internal readonly Factory Factory = new();
        internal Area()
        {
            System.IO.Directory.CreateDirectory(Directory); var local = Path.Combine(Directory,"local"); System.IO.Directory.CreateDirectory(local);
            var store = new Store(); store.SaveDefinition(new(Id,"offline",local,false,Source:new(FileSourceKind.WebDav,"https://never-connect.example.test/"),RegistrationStage:MountRegistrationStage.NotRegistered));
            Repository = new(Path.Combine(Directory,"data"),definitions:store); Logger = new(Path.Combine(Directory,"logs.db"));
            new CredentialStore(Repository.DataDirectory).Save(Id,new("account-secret","password-secret")); Manager = new(Repository,Logger,Factory);
        }
        public async ValueTask DisposeAsync() { await Manager.DisposeAsync(); await Logger.DisposeAsync(); if(System.IO.Directory.Exists(Directory))System.IO.Directory.Delete(Directory,true); }
    }
}
