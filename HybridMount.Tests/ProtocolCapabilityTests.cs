using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Models.Mounting.Synology;

namespace HybridMount.Tests;

/// <summary>能力展示不授予业务权限；验证声明优先级、精确范围、只读请求、正文消费和草稿代次。</summary>
public sealed class ProtocolCapabilityTests
{
    [Fact]
    public void ExplicitUnsupportedOverridesAdvertisementAndSnapshotsStayImmutable()
    {
        var tracker = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        tracker.Record("dav.range", ProtocolCapabilityStatus.Advertised, "folder", ProtocolCapabilityReason.Declared);
        var initial = tracker.Snapshot;
        tracker.Record("dav.range", ProtocolCapabilityStatus.Unsupported, "folder", ProtocolCapabilityReason.RangeIgnored);
        tracker.Record("dav.range", ProtocolCapabilityStatus.Advertised, "folder", ProtocolCapabilityReason.Declared);
        Assert.Equal(ProtocolCapabilityStatus.Unsupported, ProtocolCapabilitySummary.Read(tracker.Snapshot, "dav.range").Status);
        Assert.Equal(ProtocolCapabilityStatus.Advertised, ProtocolCapabilitySummary.Read(initial, "dav.range").Status);
        Assert.Throws<NotSupportedException>(() => ((IList<ProtocolCapabilityEvidence>)initial.Evidence).Clear());
        var latest = tracker.Snapshot;
        tracker.Record("dav.range", ProtocolCapabilityStatus.Unsupported, "folder", ProtocolCapabilityReason.RangeIgnored);
        Assert.Same(latest, tracker.Snapshot);
    }
    [Fact]
    public void DifferentDirectoriesDoNotBecomeWholeMountSupport()
    {
        var tracker = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        tracker.Verified("dav.update", CapabilitySupport.Supported, "");
        tracker.Verified("dav.update", CapabilitySupport.Unsupported, "child");
        var value = ProtocolCapabilitySummary.Read(tracker.Snapshot, "dav.update");
        Assert.Equal(ProtocolCapabilityStatus.Unknown, value.Status);
        Assert.Equal(ProtocolCapabilityReason.MixedScopes, value.Reason);
        Assert.Equal(new[] { "", "child" }, value.Evidence.Select(e => e.Scope));
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(tracker.Snapshot, "dav.delete").Status);
    }
    [Fact]
    public void PermissionFailureIsUnknownEvenWhenAdvertised()
    {
        var tracker = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        tracker.Record("dav.put", ProtocolCapabilityStatus.Advertised);
        tracker.Record("dav.put", ProtocolCapabilityStatus.Unknown, "", ProtocolCapabilityReason.Permission);
        tracker.Record("dav.put", ProtocolCapabilityStatus.Advertised);
        var value = ProtocolCapabilitySummary.Read(tracker.Snapshot, "dav.put");
        Assert.Equal(ProtocolCapabilityStatus.Unknown, value.Status);
        Assert.Equal(ProtocolCapabilityReason.Permission, value.Reason);
    }
    [Fact]
    public async Task ConcurrentObservationsRemainBoundedAndDisplayObserversCannotFailOperations()
    {
        var tracker = new ProtocolCapabilityTracker(FileSourceKind.S3);
        tracker.Changed += () => throw new IOException("display observer");
        await Task.WhenAll(Enumerable.Range(0, 600).Select(i => Task.Run(() => tracker.Record("s3.head", ProtocolCapabilityStatus.Supported, "folder/" + i))));
        Assert.Equal(256, tracker.Snapshot.Evidence.Count);
        Assert.All(tracker.Snapshot.Evidence, item => Assert.StartsWith("folder/", item.Scope));
    }
    [Fact]
    public void LateTestAndCredentialChangesDoNotPublishToNewDraft()
    {
        var settings = new FileSourceConfiguration(FileSourceKind.WebDav, "https://example.test/");
        var definition = new MountDefinition(Guid.NewGuid(), "mount", "D:\\unused", Source: settings, CredentialRevision: 2);
        var tracker = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        tracker.Record("dav.propfind", ProtocolCapabilityStatus.Supported);
        var mount = new MountSnapshot(definition, "connected", "", IsConnected: true, ProtocolCapabilities: tracker.Snapshot);
        var draft = new ProtocolCapabilityDraftSession(FileSourceKind.WebDav, "original", 2, settings);
        Assert.Same(tracker.Snapshot, draft.Select("original", mount));
        Assert.Null(draft.Select("changed", mount));
        draft.Begin("original"); Assert.False(draft.Complete("changed", tracker.Snapshot));
        Assert.Null(draft.Select("changed", mount));
        Assert.Null(draft.Select("original", mount with { Definition = definition with { CredentialRevision = 3 } }));
        Assert.Null(draft.Select("original", mount with { Definition = definition with { Source = settings with { Endpoint = "https://other.test/" } } }));
    }
    [Fact]
    public void FailedTestEnablesUnknownRowsAndCancellationKeepsEarlierResult()
    {
        var draft = new ProtocolCapabilityDraftSession(FileSourceKind.S3, "one", 0, null);
        Assert.Null(draft.Select("one", null));
        draft.Begin("one"); Assert.True(draft.Complete("one", null));
        var previous = draft.Select("one", null);
        Assert.NotNull(previous); Assert.Empty(previous.Evidence);
        draft.Begin("one"); draft.Cancel(); Assert.False(draft.Complete("one", null));
        Assert.Same(previous, draft.Select("one", null));
        Assert.Null(draft.Select("two", null));
    }
    [Fact]
    public void DraftChangedAwayAndBackStillRejectsOldTestAndQueueResetPreservesFileEvidence()
    {
        var draft = new ProtocolCapabilityDraftSession(FileSourceKind.S3, "one", 0, null);
        draft.Begin("one"); draft.Observe("two"); draft.Observe("one");
        Assert.False(draft.Complete("one", null)); Assert.Null(draft.Select("one", null));
        var tracker = new ProtocolCapabilityTracker(FileSourceKind.S3);
        tracker.Record("s3.head", ProtocolCapabilityStatus.Supported, "file");
        tracker.Record("sqs.attributes", ProtocolCapabilityStatus.Supported, "@queue");
        var previous = tracker.Snapshot; tracker.ClearPrefix("sqs.");
        Assert.Equal(ProtocolCapabilityStatus.Supported, ProtocolCapabilitySummary.Read(tracker.Snapshot, "s3.head").Status);
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(tracker.Snapshot, "sqs.attributes").Status);
        Assert.Equal(2, previous.Evidence.Count);
    }
    [Fact]
    public void SynchronousInputInvalidationRejectsPendingResultEvenWhenDigestReturnsToOriginal()
    {
        var draft = new ProtocolCapabilityDraftSession(FileSourceKind.WebDav, "one", 0, null);
        draft.Begin("one");
        var revision = draft.Observe("one");
        draft.Invalidate(); draft.Invalidate();
        Assert.True(draft.Observe("one") > revision);
        Assert.False(draft.Complete("one", null));
        draft.Begin("one"); Assert.True(draft.Complete("one", null));
        draft.Invalidate(); Assert.Null(draft.Select("one", null));
    }
    [Fact]
    public void ReadOnlyDraftTestDoesNotFreezeConnectedRuntimeEvidence()
    {
        var source = new FileSourceConfiguration(FileSourceKind.WebDav, "https://example.test/");
        var definition = new MountDefinition(Guid.NewGuid(), "mount", "D:\\unused", Source: source);
        var tracker = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        tracker.Record("dav.put", ProtocolCapabilityStatus.Supported, "folder");
        var first = tracker.Snapshot;
        var draft = new ProtocolCapabilityDraftSession(FileSourceKind.WebDav, "same", 0, source);
        draft.Begin("same"); Assert.True(draft.Complete("same", null));
        var mount = new MountSnapshot(definition, "", "", IsConnected: true, ProtocolCapabilities: first);
        Assert.Same(first, draft.Select("same", mount));
        tracker.Record("dav.range", ProtocolCapabilityStatus.Supported, "folder/file");
        Assert.Same(tracker.Snapshot, draft.Select("same", mount with { ProtocolCapabilities = tracker.Snapshot }));
    }
    [Fact]
    public void ExplicitQueueEvidenceMergesWithLiveFilesAndReconnectRetiresOldTests()
    {
        var source = new FileSourceConfiguration(FileSourceKind.S3, "https://s3.example.test/", "bucket");
        var definition = new MountDefinition(Guid.NewGuid(), "mount", "D:\\unused", Source: source);
        var live = new ProtocolCapabilityTracker(FileSourceKind.S3); live.Record("s3.head", ProtocolCapabilityStatus.Supported, "file");
        var mount = new MountSnapshot(definition, "", "", IsConnected: true, ProtocolCapabilities: live.Snapshot);
        var draft = new ProtocolCapabilityDraftSession(FileSourceKind.S3, "same", 0, source); Assert.NotNull(draft.Select("same", mount));
        var queue = new ProtocolCapabilityTracker(FileSourceKind.S3); queue.Record("sqs.attributes", ProtocolCapabilityStatus.Unknown, "@queue", ProtocolCapabilityReason.Permission);
        draft.Begin("same"); Assert.True(draft.Complete("same", queue.Snapshot));
        var combined = Assert.IsType<ProtocolCapabilitySnapshot>(draft.Select("same", mount));
        Assert.Equal(ProtocolCapabilityStatus.Supported, ProtocolCapabilitySummary.Read(combined, "s3.head").Status);
        Assert.Equal(ProtocolCapabilityReason.Permission, ProtocolCapabilitySummary.Read(combined, "sqs.attributes").Reason);
        Assert.Same(combined, draft.Select("same", mount));
        var reconnected = new ProtocolCapabilityTracker(FileSourceKind.S3); reconnected.Record("s3.list", ProtocolCapabilityStatus.Supported);
        Assert.Same(reconnected.Snapshot, draft.Select("same", mount with { ProtocolCapabilities = reconnected.Snapshot }));
    }
    [Fact]
    public void PublicMountJsonDoesNotGainPrivateCapabilityPayload()
    {
        var tracker = new ProtocolCapabilityTracker(FileSourceKind.WebDav);
        tracker.Record("dav.propfind", ProtocolCapabilityStatus.Supported);
        var mount = new MountSnapshot(new(Guid.NewGuid(), "mount", "D:\\unused"), "", "", ProtocolCapabilities: tracker.Snapshot);
        Assert.DoesNotContain("ProtocolCapabilities", JsonSerializer.Serialize(mount));
        Assert.DoesNotContain("Capabilities", JsonSerializer.Serialize(new ConnectionTestResult(true, "", Capabilities: tracker.Snapshot)));
    }
    [Theory]
    [InlineData(FileSourceKind.WebDav)] [InlineData(FileSourceKind.S3)] [InlineData(FileSourceKind.Synology)]
    public void CatalogContainsOnlyUniqueImplementedInterfaces(FileSourceKind kind)
    {
        var rows = ProtocolCapabilityCatalog.For(kind);
        Assert.Equal(rows.Count, rows.Select(item => item.Id).Distinct().Count());
        Assert.DoesNotContain(rows, item => item.Name.Contains("PROPPATCH") || item.Name.Contains("RFC 9100"));
        Assert.All(rows, row => Assert.NotEqual(row.DescriptionKey, HybridMount.Services.Localization.UiText.Text(row.DescriptionKey)));
        Assert.DoesNotContain(ProtocolCapabilityCatalog.For(FileSourceKind.S3), row => row.Id.StartsWith("sqs."));
        Assert.Equal(3, ProtocolCapabilityCatalog.For(FileSourceKind.S3, true).Count(row => row.Id.StartsWith("sqs.")));
    }
    [Fact]
    public async Task ReadOnlyDavTestRetainsClaimsWithoutTestingConditionalWritesOrReadingBodies()
    {
        await using var fixture = new DavFixture();
        var original = fixture.Server.Handler;
        fixture.Server.Handler = request => request.Method == "OPTIONS"
            ? Task.FromResult(HttpTestServer.Response.Text(200, "", new() { ["Allow"] = "OPTIONS, GET, PUT, DELETE, PROPFIND, REPORT, PROPPATCH" }))
            : original(request);
        var result = await new FileSourceFactory().TestConnectionAsync(fixture.Options, new("user", "app-password"));
        Assert.True(result.Success, result.Message);
        var snapshot = Assert.IsType<ProtocolCapabilitySnapshot>(result.Capabilities);
        Assert.Equal(ProtocolCapabilityStatus.Supported, ProtocolCapabilitySummary.Read(snapshot, "dav.propfind").Status);
        Assert.Equal(ProtocolCapabilityStatus.Advertised, ProtocolCapabilitySummary.Read(snapshot, "dav.put").Status);
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(snapshot, "dav.update").Status);
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(snapshot, "dav.range").Status);
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(snapshot, "dav.report").Status);
        Assert.All(fixture.Server.Requests, request => Assert.Contains(request.Method, new[] { "OPTIONS", "PROPFIND" }));
        Assert.Equal(2, fixture.Server.Requests.Count);
    }
    [Fact]
    public async Task DavRangeEvidenceRequiresConsumedBodyAndIgnoredRangeOverridesHeaderClaim()
    {
        await using var fixture = new DavFixture(); await using var source = fixture.Source();
        var entry = await source.GetMetadataAsync("文件.bin");
        var good = await source.TryReadPreviewRangeAsync(entry, 1, new byte[12]);
        Assert.True(good.Supported);
        Assert.Equal(ProtocolCapabilityStatus.Supported, ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "dav.range").Status);
        var original = fixture.Server.Handler;
        fixture.Server.Handler = request => request.Method == "GET"
            ? Task.FromResult(HttpTestServer.Response.Text(200, "body", new() { ["Accept-Ranges"] = "bytes", ["ETag"] = fixture.Objects["文件.bin"].ETag })) : original(request);
        var bad = await source.TryReadPreviewRangeAsync(entry, 1, new byte[12]);
        Assert.False(bad.Supported);
        var value = ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "dav.range");
        Assert.Equal(ProtocolCapabilityStatus.Unsupported, value.Status);
        Assert.Equal("文件.bin", Assert.Single(value.Evidence).Scope);
        var requests = fixture.Server.Requests.Count;
        for (var i = 0; i < 100; i++) _ = source.ProtocolCapabilities;
        Assert.Equal(requests, fixture.Server.Requests.Count);
    }
    [Fact]
    public async Task S3ReadOnlyTestRecordsActualListScopeAndLeavesBodyAndWritesUnknown()
    {
        await using var fixture = new S3Fixture();
        var result = await new FileSourceFactory().TestConnectionAsync(fixture.Options, S3Fixture.Credentials);
        Assert.True(result.Success, result.Message);
        var snapshot = Assert.IsType<ProtocolCapabilitySnapshot>(result.Capabilities);
        Assert.Equal(ProtocolCapabilityStatus.Supported, ProtocolCapabilitySummary.Read(snapshot, "s3.list").Status);
        Assert.Equal("", Assert.Single(snapshot.Evidence).Scope);
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(snapshot, "s3.put").Status);
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(snapshot, "s3.range").Status);
        Assert.Single(fixture.Server.Requests);
    }
    [Fact]
    public async Task ConditionalWriteEvidenceStaysInActuallyProbedParentDirectory()
    {
        await using var fixture = new DavFixture(); await using var source = fixture.Source();
        fixture.Objects["other"] = new([], "\"other\"", true);
        using (var body = new MemoryStream([1, 2, 3])) await source.UploadAsync("空目录/one.bin", body, null);
        var evidence = ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "dav.create");
        Assert.Equal(ProtocolCapabilityStatus.Supported, evidence.Status);
        Assert.Equal("空目录", Assert.Single(evidence.Evidence).Scope);
        using (var body = new MemoryStream([4, 5])) await source.UploadAsync("other/two.bin", body, null);
        Assert.Equal("空目录", Assert.Single(ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "dav.create").Evidence).Scope);
    }
    [Fact]
    public async Task S3RangeSupportAppearsOnlyAfterValidatedReadAtExactPath()
    {
        await using var fixture = new S3Fixture(); await using var source = fixture.Source();
        var entry = await source.GetMetadataAsync("文件.bin");
        Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "s3.range").Status);
        Assert.Equal(12, await source.ReadRangeAsync(entry, 2, new byte[12]));
        var evidence = ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "s3.range");
        Assert.Equal(ProtocolCapabilityStatus.Supported, evidence.Status);
        Assert.Equal("文件.bin", Assert.Single(evidence.Evidence).Scope);
    }
    [Fact]
    public async Task FailedReadOnlyProbeRetainsSafePartialEvidence()
    {
        await using var fixture = new DavFixture(); var original = fixture.Server.Handler;
        fixture.Server.Handler = request => request.Method == "PROPFIND"
            ? Task.FromResult(HttpTestServer.Response.Text(403, "PRIVATE-RESPONSE")) : original(request);
        var result = await new FileSourceFactory().TestConnectionAsync(fixture.Options, new("user", "app-password"));
        Assert.False(result.Success); Assert.NotNull(result.Capabilities);
        Assert.Equal(ProtocolCapabilityStatus.Supported, ProtocolCapabilitySummary.Read(result.Capabilities, "dav.options").Status);
        var value = ProtocolCapabilitySummary.Read(result.Capabilities, "dav.propfind");
        Assert.Equal(ProtocolCapabilityStatus.Unknown, value.Status); Assert.Equal(ProtocolCapabilityReason.Permission, value.Reason);
        Assert.DoesNotContain("PRIVATE-RESPONSE", JsonSerializer.Serialize(result.Capabilities));
    }
    [Fact]
    public async Task SynologyApiDiscoveryIsAdvertisementUntilActuallyUsed()
    {
        var area = Path.Combine(Path.GetTempPath(), "HybridMount.CapabilityTests", Guid.NewGuid().ToString("N"));
        try
        {
            var dsm = new SynologyFakeDsm();
            await using var source = new SynologyFileSource(SynologyTests.Options, new("user", "password"), new WholeFileCache(area), dsm, null);
            await source.ProbeAsync();
            Assert.Equal(ProtocolCapabilityStatus.Supported, ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "syno.info").Status);
            Assert.Equal(ProtocolCapabilityStatus.Supported, ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "syno.auth").Status);
            Assert.Equal(ProtocolCapabilityStatus.Advertised, ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "syno.upload").Status);
            Assert.Equal(ProtocolCapabilityStatus.Unknown, ProtocolCapabilitySummary.Read(source.ProtocolCapabilities, "syno.create").Status);
            Assert.DoesNotContain(dsm.Requests, request => request["api"] == "SYNO.FileStation.Upload" || request["api"] == "SYNO.FileStation.Delete");
        }
        finally { if (Directory.Exists(area)) Directory.Delete(area, true); }
    }
}
