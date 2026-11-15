using HybridMount.Models.Mounting.Capabilities;
using HybridMount.Commons.Protocols;
using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Files.Content;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Infrastructure.Protocols.Capabilities;
using HybridMount.Infrastructure.Protocols.Synology;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.Services.Scheduling;
using HybridMount.Tests.Fixtures.Protocols;
using HybridMount.UI.Presentation;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
namespace HybridMount.Tests.Integration.Protocols;

/// <summary>显式检测的真实HTTP、持久归属、取消与独立清理；不注册根，不使用生产目录或真实队列。</summary>
public sealed class CapabilityProbeTests
{
    [Fact]
    public async Task DavDetectionUsesOnlyOwnedBodiesAndCleansAfterRestart()
    {
        await using var fixture = new DavFixture();
        var directory = Path.Combine(fixture.Area, "probe-journal");
        var credentials = new SourceCredentials("user", "app-password");
        var before = fixture.Objects.ToDictionary(pair => pair.Key, pair => pair.Value);
        var session = new CapabilityProbeSession(directory, fixture.Options, credentials);
        await using (var source = fixture.Source())
        {
            await using var queue = new TasksQueueService(1);
            var binding = queue.StartMount(session.Run.RunId);
            ((IMountNetworkSource)source).BindQueue(binding);
            await ((ICapabilityProbeSource)source).DetectCapabilitiesAsync(session, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            session.CompleteMissing(source.ProtocolCapabilities, false);
            Assert.Contains(session.Run.Cases, item => item.Id == "dav.put" && item.Status == ProtocolCapabilityStatus.Supported);
            Assert.Contains(session.Run.Cases, item => item.Id == "dav.conditionalDelete" && item.Status == ProtocolCapabilityStatus.Supported);
            Assert.Contains(session.Run.Cases, item => item.Id == "dav.lock" && item.Status == ProtocolCapabilityStatus.Unsupported);
        }
        var saved = Assert.Single(CapabilityProbeSession.Read(directory));
        Assert.Equal(session.Run.RunId, saved.RunId);
        Assert.DoesNotContain("app-password", await File.ReadAllTextAsync(Path.Combine(directory, saved.RunId.ToString("N") + ".json")));
        await using var resumed = fixture.Source();
        var recovering = new CapabilityProbeSession(directory, fixture.Options, credentials, resume: saved);
        await ((ICapabilityProbeSource)resumed).CleanupCapabilitiesAsync(recovering, CancellationToken.None);
        Assert.DoesNotContain(recovering.Run.Resources, item => item.Stage is not ("Cleaned" or "Planned"));
        Assert.Equal(before.Keys.Order(), fixture.Objects.Keys.Order());
        foreach (var pair in before) Assert.Equal(pair.Value.Bytes, fixture.Objects[pair.Key].Bytes);
        Assert.All(fixture.Server.Requests.Where(request => request.Method == "GET"), request => Assert.Contains(session.Prefix, request.Target));
    }

    [Fact]
    public async Task DavCancelStopsNewCasesAndIndependentCleanupStillRuns()
    {
        await using var fixture = new DavFixture();
        using var cancel = new CancellationTokenSource();
        var session = new CapabilityProbeSession(Path.Combine(fixture.Area, "journal"), fixture.Options, new("user", "app-password"),
            run => { if (run.Cases.Any(item => item.Id == "dav.conditionalDelete")) cancel.Cancel(); });
        await using var source = fixture.Source();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ((ICapabilityProbeSource)source).DetectCapabilitiesAsync(session, cancel.Token));
        var count = session.Run.Cases.Length;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ((ICapabilityProbeSource)source).CleanupCapabilitiesAsync(session, cleanup.Token);
        Assert.Equal(count, session.Run.Cases.Length);
        Assert.DoesNotContain(fixture.Objects.Keys, key => key.StartsWith(session.Prefix, StringComparison.Ordinal));
        Assert.DoesNotContain(session.Run.Cases, item => item.Id is "dav.move" or "dav.lock");
    }

    [Fact]
    public async Task ChangedOwnedContentIsKeptAndReportedPrecisely()
    {
        await using var fixture = new DavFixture();
        var session = new CapabilityProbeSession(Path.Combine(fixture.Area, "journal"), fixture.Options, new("user", "app-password"));
        await using var source = fixture.Source();
        await ((ICapabilityProbeSource)source).DetectCapabilitiesAsync(session, CancellationToken.None);
        var candidate = session.Run.Resources.First(item => item.Kind == "File" && item.Stage == "Owned" && fixture.Objects.ContainsKey(item.Path));
        fixture.Put(candidate.Path, [1, 2, 3, 4]);
        await ((ICapabilityProbeSource)source).CleanupCapabilitiesAsync(session, CancellationToken.None);
        var retained = session.Resource(candidate.Path);
        Assert.Equal("IdentityChanged", retained.Reason);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, fixture.Objects[candidate.Path].Bytes);
        Assert.Contains(candidate.RemotePath, CapabilityProbePresentation.ResourceDetails(retained));
    }

    [Fact]
    public async Task WritePermissionFailureDoesNotDisableReadsOrClaimUnsupported()
    {
        await using var fixture = new DavFixture();
        var handle = fixture.Server.Handler;
        fixture.Server.Handler = request => request.Method is "PUT" or "MKCOL"
            ? Task.FromResult(new HttpTestServer.Response(403, [])) : handle(request);
        var session = new CapabilityProbeSession(Path.Combine(fixture.Area, "journal"), fixture.Options, new("user", "app-password"));
        await using var source = fixture.Source();
        await ((ICapabilityProbeSource)source).DetectCapabilitiesAsync(session, CancellationToken.None);
        Assert.Contains(session.Run.Cases, item => item.Id == "dav.put" && item.PermissionDenied && item.Status == ProtocolCapabilityStatus.Unknown);
        Assert.Contains(session.Run.Cases, item => item.Id == "dav.propfind" && item.Status == ProtocolCapabilityStatus.Supported);
        Assert.Equal(fixture.Bytes, fixture.Objects["文件.bin"].Bytes);
    }

    [Fact]
    public async Task PlannedAndForeignResourcesNeverGrantCleanupOwnership()
    {
        await using var fixture = new DavFixture();
        var session = new CapabilityProbeSession(Path.Combine(fixture.Area, "journal"), fixture.Options, new("user", "app-password"));
        var path = session.Plan("collision", new Uri(new Uri(fixture.Options.Endpoint), session.Prefix + "collision").AbsoluteUri, "File", new byte[] { 1 });
        fixture.Put(path, [1]);
        session.Update(path, item => item with { Stage = "Foreign" });
        await using var source = fixture.Source();
        await ((ICapabilityProbeSource)source).CleanupCapabilitiesAsync(session, CancellationToken.None);
        Assert.True(fixture.Objects.ContainsKey(path));
        Assert.DoesNotContain(fixture.Server.Requests, request => request.Method != "PROPFIND");
        Assert.False(CapabilityProbeOwnership.Contains(session.Run.Scope, session.Resource(path).RemotePath));
        Assert.Throws<IOException>(() => new CapabilityProbeSession(Path.Combine(fixture.Area, "journal"), fixture.Options, new("different-user", "password"), resume: session.Run));
    }

    [Fact]
    public async Task SynologyOwnedDownloadAndCancelCleanupDoNotTouchUserFiles()
    {
        var area = Path.Combine(Path.GetTempPath(), "HybridMount.CapabilityProbeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var server = new SynologyFakeDsm();
            var before = server.Files.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
            using var cancel = new CancellationTokenSource();
            var session = new CapabilityProbeSession(area, SynologyTests.Options, new("user", "password"),
                run => { if (run.Cases.Any(item => item.Id == "syno.download")) cancel.Cancel(); });
            await using var source = new SynologyFileSource(SynologyTests.Options, new("user", "password"), new WholeFileCache(Path.Combine(area, "cache")), server, null);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ((ICapabilityProbeSource)source).DetectCapabilitiesAsync(session, cancel.Token));
            await ((ICapabilityProbeSource)source).CleanupCapabilitiesAsync(session, CancellationToken.None);
            Assert.Equal(before.Keys.Order(), server.Files.Keys.Order());
            foreach (var pair in before) Assert.Equal(pair.Value, server.Files[pair.Key]);
            Assert.Contains(session.Run.Cases, item => item.Id == "syno.download" && item.Status == ProtocolCapabilityStatus.Supported);
            Assert.All(server.Requests.Where(request => request["api"] == "SYNO.FileStation.Delete" && request["method"] == "start"), request => Assert.Contains(session.Prefix, request["path"]));
        }
        finally { if (Directory.Exists(area)) Directory.Delete(area, true); }
    }

    [Fact]
    public async Task ApplicationStopOwnsCleanupWithoutCreatingMountConfiguration()
    {
        await using var fixture = new DavFixture();
        var directory = Path.Combine(fixture.Area, "app-data");
        var repository = new MountRepository(directory, @"Software\HybridMount.NET.Tests\" + Guid.NewGuid().ToString("N"));
        await using var logger = new LoggerService(Path.Combine(fixture.Area, "log.db"));
        var factory = new FileSourceFactory();
        await using var queue = new TasksQueueService(1);
        await using var manager = new MountManager(repository, logger, factory, tasksQueue: queue);
        var progress = new ImmediateProgress(run =>
        {
            if (run.Cases.Any(item => item.Id == "dav.conditionalDelete")) manager.RequestStop();
        });
        var result = await manager.DetectCapabilitiesAsync(fixture.Options, new("user", "app-password"), null, null, null, progress, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("Canceled", result.Run.Stage);
        Assert.DoesNotContain(result.Run.Resources, item => item.Stage is not ("Cleaned" or "Planned"));
        Assert.Empty(repository.LoadDefinitions());
        Assert.Equal("Canceled", Assert.Single(repository.LoadOperations(), item => item.Id == "probe:" + result.Run.RunId.ToString("N")).State);
        Assert.DoesNotContain(fixture.Objects.Keys, key => key.StartsWith("hybridmount-probe-" + result.Run.RunId.ToString("N"), StringComparison.Ordinal));
    }
    [Fact]
    public async Task TaskPageCancelPreservesEarlyUnverifiedCleanupAndReportsFailure()
    {
        await using var fixture = new DavFixture();
        var repository = new MountRepository(Path.Combine(fixture.Area, "data"), @"Software\HybridMount.NET.Tests\" + Guid.NewGuid().ToString("N"));
        await using var logger = new LoggerService(Path.Combine(fixture.Area, "log.db"));
        await using var manager = new MountManager(repository, logger);
        var accepted = false;
        var progress = new ImmediateProgress(run =>
        {
            if (!accepted && run.Cases.Any(item => item.Id == "dav.put")) accepted = manager.TasksQueue.Cancel("probe:" + run.RunId.ToString("N"));
        });
        var result = await manager.DetectCapabilitiesAsync(fixture.Options, new("user", "app-password"), null, null, null, progress, CancellationToken.None);
        Assert.True(accepted);
        Assert.Equal("CanceledCleanupPending", result.Run.Stage);
        Assert.Contains(result.Run.Resources, item => item.Stage == "Owned" && item.Reason == "ConditionalDeleteUnverified");
        Assert.Equal("PartialFailure", Assert.Single(repository.LoadOperations()).State);
        Assert.True(Assert.Single(result.Run.Cases, item => item.Id == "dav.update").Skipped);
        Assert.Single(fixture.Server.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task PersistedJournalFailurePreventsAnyNewRemoteMutation()
    {
        await using var fixture = new DavFixture();
        var area = Path.Combine(fixture.Area, "journal");
        var session = new CapabilityProbeSession(area, fixture.Options, new("user", "app-password"));
        var file = Path.Combine(area, session.Run.RunId.ToString("N") + ".json");
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await using var source = fixture.Source();
            var error = await Record.ExceptionAsync(() => ((ICapabilityProbeSource)source).DetectCapabilitiesAsync(session, CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.DoesNotContain(fixture.Server.Requests, request => request.Method is "PUT" or "MOVE" or "MKCOL" or "DELETE" or "LOCK");
        var value = JsonSerializer.Deserialize<CapabilityProbeRun>(await File.ReadAllBytesAsync(file))!;
        await File.WriteAllBytesAsync(file, JsonSerializer.SerializeToUtf8Bytes(value with { Resources = [new("../escape", "outside", "File", "Owned", [])] }));
        Assert.Throws<IOException>(() => CapabilityProbeSession.Read(area));
    }

    private sealed class ImmediateProgress(Action<CapabilityProbeRun> publish) : IProgress<CapabilityProbeRun>
    {
        public void Report(CapabilityProbeRun value) => publish(value);
    }

    [Fact]
    public async Task S3UsesRealMultipartAndCleansEveryOwnedVersion()
    {
        await using var fixture = new S3Fixture();
        var owner = new Dictionary<(string Key, string Version), string>();
        var uploads = new Dictionary<string, string>();
        var handle = fixture.Server.Handler;
        XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
        fixture.Server.Handler = async request =>
        {
            var uri = new Uri(fixture.Server.Address, request.Target);
            var fields = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(item => item.Split('=', 2))
                .ToDictionary(item => Uri.UnescapeDataString(item[0]), item => item.Length == 2 ? Uri.UnescapeDataString(item[1]) : "");
            var key = Uri.UnescapeDataString(uri.AbsolutePath)["/test-bucket/".Length..];
            HttpTestServer.Response Error() => HttpTestServer.Response.Text(404, "<Error><Code>NoSuchUpload</Code></Error>");
            if (request.Method == "GET" && fields.ContainsKey("versions"))
                return HttpTestServer.Response.Text(200, new XElement(ns + "ListVersionsResult", new XElement(ns + "IsTruncated", "false"),
                    fixture.Versions.Where(pair => pair.Key.Item1.StartsWith(fields.GetValueOrDefault("prefix") ?? "", StringComparison.Ordinal)).Select(pair =>
                        new XElement(ns + "Version", new XElement(ns + "Key", pair.Key.Item1), new XElement(ns + "VersionId", pair.Key.Item2),
                            new XElement(ns + "ETag", pair.Value.ETag), new XElement(ns + "Size", pair.Value.Bytes.Length), new XElement(ns + "IsLatest", "true")))).ToString());
            if (request.Method == "DELETE" && fields.TryGetValue("versionId", out var removed))
            {
                fixture.Versions.TryRemove((key, removed), out _);
                if (fixture.Objects.TryGetValue(key, out var current) && current.VersionId == removed) fixture.Objects.TryRemove(key, out _);
                return new(204, []);
            }
            if (request.Method == "GET" && fields.TryGetValue("uploadId", out var pending))
                return fixture.Uploads.ContainsKey(pending) ? HttpTestServer.Response.Text(200, $"<ListPartsResult xmlns=\"{ns}\"><IsTruncated>false</IsTruncated></ListPartsResult>") : Error();
            if (request.Method == "PUT" && fields.TryGetValue("uploadId", out var copyId) && request.Headers.TryGetValue("x-amz-copy-source", out var origin))
            {
                var originalKey = Uri.UnescapeDataString(new Uri(fixture.Server.Address, origin.TrimStart('/')).AbsolutePath).TrimStart('/')["test-bucket/".Length..];
                fixture.Uploads[copyId].Parts[1] = fixture.Objects[originalKey].Bytes;
                return HttpTestServer.Response.Text(200, $"<CopyPartResult xmlns=\"{ns}\"><ETag>\"part-copy\"</ETag><LastModified>2026-10-29T12:00:00Z</LastModified></CopyPartResult>");
            }
            var response = await handle(request);
            if (request.Method == "POST" && fields.ContainsKey("uploads") && response.Status == 200)
            {
                var id = XDocument.Parse(System.Text.Encoding.UTF8.GetString(response.Body)).Descendants(ns + "UploadId").Single().Value;
                uploads[id] = request.Headers.GetValueOrDefault("x-amz-meta-hybridmount-probe-run") ?? "";
            }
            if (request.Method is "PUT" or "POST" && response.Status == 200 && fixture.Objects.TryGetValue(key, out var created))
                owner[(key, created.VersionId)] = request.Headers.GetValueOrDefault("x-amz-meta-hybridmount-probe-run") ??
                    (fields.TryGetValue("uploadId", out var completeId) ? uploads.GetValueOrDefault(completeId) ?? "" : "");
            if (request.Method is "GET" or "HEAD" && response.Status is 200 or 206)
            {
                fixture.Objects.TryGetValue(key, out var item);
                var version = fields.GetValueOrDefault("versionId") ?? item?.VersionId ?? "";
                if (owner.TryGetValue((key, version), out var identity)) response.Headers!["x-amz-meta-hybridmount-probe-run"] = identity;
            }
            return response;
        };
        var originals = fixture.Versions.ToDictionary(pair => pair.Key, pair => pair.Value);
        var session = new CapabilityProbeSession(Path.Combine(fixture.Area, "journal"), fixture.Options, S3Fixture.Credentials);
        await using var source = fixture.Source();
        await ((ICapabilityProbeSource)source).DetectCapabilitiesAsync(session, CancellationToken.None);
        await ((ICapabilityProbeSource)source).CleanupCapabilitiesAsync(session, CancellationToken.None);
        Assert.Contains(session.Run.Cases, item => item.Id == "s3.copyPart" && item.Status == ProtocolCapabilityStatus.Supported);
        Assert.Contains(session.Run.Cases, item => item.Id == "s3.complete" && item.Status == ProtocolCapabilityStatus.Supported);
        Assert.Contains(session.Run.Cases, item => item.Id == "s3.abort" && item.Status == ProtocolCapabilityStatus.Supported);
        Assert.Empty(fixture.Uploads);
        Assert.DoesNotContain(session.Run.Resources, item => item.Stage is not ("Cleaned" or "Planned"));
        Assert.Equal(originals.Keys.Order(), fixture.Versions.Keys.Order());
        foreach (var pair in originals) Assert.Equal(pair.Value.Bytes, fixture.Versions[pair.Key].Bytes);
        Assert.All(fixture.Server.Requests, S3Fixture.VerifySignature);
        Assert.DoesNotContain(fixture.Server.Requests, request => request.Method == "GET" && request.Target.Contains("文件.bin", StringComparison.Ordinal));
    }
}
