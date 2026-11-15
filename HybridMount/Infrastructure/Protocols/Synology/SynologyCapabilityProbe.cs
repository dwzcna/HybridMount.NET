using HybridMount.Infrastructure.Protocols.Capabilities;
using HybridMount.Models.Mounting;
using System.Security.Cryptography;

namespace HybridMount.Infrastructure.Protocols.Synology;

public sealed partial class SynologyFileSource : ICapabilityProbeSource
{
    private string _capabilityProbeScope = "";

    /// <summary>只在当前共享根创建本次随机资源；不增加对用户内容覆盖、移动或删除的协议能力。</summary>
    async Task ICapabilityProbeSource.DetectCapabilitiesAsync(CapabilityProbeSession session, CancellationToken token)
    {
        await session.CaseAsync("syno.auth", "@server", async cancel => _ = await EnsureSessionAsync(cancel).ConfigureAwait(false), token).ConfigureAwait(false);
        await session.CaseAsync("syno.list", "", async cancel =>
        {
            _ = await ListSharedFoldersAsync(cancel).ConfigureAwait(false);
            if (!(await GetMetadataAsync("", cancel).ConfigureAwait(false)).IsDirectory) throw new IOException("配置根不是目录。");
        }, token).ConfigureAwait(false);
        var bytes = RandomNumberGenerator.GetBytes(32);
        var changed = RandomNumberGenerator.GetBytes(33);
        var file = session.Plan("file", RemotePath(session.Prefix + "file"), "File", bytes, changed);
        var folder = session.Plan("directory", RemotePath(session.Prefix + "directory"), "Directory");
        await session.CaseAsync("syno.upload", file, async cancel =>
        {
            if (await TryGetMetadataAsync(file, cancel).ConfigureAwait(false) is not null)
            {
                session.Update(file, item => item with { Stage = "Foreign", Reason = "AlreadyExists" });
                throw new CapabilityProbeSkipException("AlreadyExists");
            }
            session.Update(file, item => item with { Stage = "Sending" });
            using var content = new MemoryStream(bytes, false);
            var receipt = await UploadFileAsync(file, content, null, cancel).ConfigureAwait(false);
            session.Update(file, item => item with { Stage = "Owned", ETag = receipt.Entry.Version });
        }, token).ConfigureAwait(false);
        void RequireFile() { if (session.Resource(file).Stage != "Owned") throw new CapabilityProbeSkipException("CreateRequired"); }
        await session.CaseAsync("syno.create", file, async cancel =>
        {
            RequireFile();
            var rejected = false;
            try { using var content = new MemoryStream(changed, false); await UploadFileAsync(file, content, null, cancel).ConfigureAwait(false); }
            catch (SourceConflictException) { rejected = true; }
            var actual = await Md5Async(file, cancel).ConfigureAwait(false);
            if (!rejected || actual != Convert.ToHexString(MD5.HashData(bytes))) throw new CapabilityProbeUnsupportedException("ConditionIgnored");
        }, token).ConfigureAwait(false);
        await session.CaseAsync("syno.md5", file, async cancel =>
        {
            RequireFile();
            if (await Md5Async(file, cancel).ConfigureAwait(false) != Convert.ToHexString(MD5.HashData(bytes))) throw new IOException("MD5内容不符。");
        }, token).ConfigureAwait(false);
        await session.CaseAsync("syno.download", file, async cancel =>
        {
            RequireFile();
            var entry = await GetMetadataAsync(file, cancel).ConfigureAwait(false);
            using var response = await DownloadAsync(entry, cancel).ConfigureAwait(false);
            await using var input = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            var actual = new byte[bytes.Length];
            await input.ReadExactlyAsync(actual, cancel).ConfigureAwait(false);
            if (!actual.AsSpan().SequenceEqual(bytes) || await input.ReadAsync(new byte[1], cancel).ConfigureAwait(false) != 0) throw new IOException("下载内容不符。");
        }, token).ConfigureAwait(false);
        await session.CaseAsync("syno.folder", folder, async cancel =>
        {
            if (await TryGetMetadataAsync(folder, cancel).ConfigureAwait(false) is not null)
            {
                session.Update(folder, item => item with { Stage = "Foreign", Reason = "AlreadyExists" });
                throw new CapabilityProbeSkipException("AlreadyExists");
            }
            session.Update(folder, item => item with { Stage = "Sending" });
            var receipt = await CreateDirectoryAsync(folder, cancel).ConfigureAwait(false);
            session.Update(folder, item => item with { Stage = "Owned", ETag = receipt.Version });
        }, token).ConfigureAwait(false);
        await session.CaseAsync("syno.cleanup", file, cancel => ProbeCleanupItemAsync(session, file, cancel), token).ConfigureAwait(false);
    }

    /// <summary>不调用用户 DeleteAsync；专用清理仅接受当前检测日志拥有的精确资源，递归删除固定关闭。</summary>
    async Task ICapabilityProbeSource.CleanupCapabilitiesAsync(CapabilityProbeSession session, CancellationToken token)
    {
        foreach (var item in session.Run.Resources.OrderBy(item => item.Kind == "Directory" ? 1 : 0).ToArray())
        {
            if (item.Stage is "Planned" or "Foreign" or "Cleaned") continue;
            try { await ProbeCleanupItemAsync(session, item.Path, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException or NotSupportedException)
            { session.Update(item.Path, value => value with { Reason = CapabilityProbeSession.FailureReason(error) }); }
        }
    }

    private async Task ProbeCleanupItemAsync(CapabilityProbeSession session, string path, CancellationToken token)
    {
        var item = session.Resource(path);
        if (item.Stage is "Planned" or "Foreign" or "Cleaned") throw new CapabilityProbeSkipException("NotOwned");
        if (item.RemotePath != RemotePath(path)) throw new IOException("检测项越过当前共享根。");
        var entry = await TryGetMetadataAsync(path, token).ConfigureAwait(false);
        if (entry is null) { session.Update(path, value => value with { Stage = "Cleaned", Reason = null }); return; }
        if (item.Kind == "Directory")
        {
            if (item.Stage != "Owned" || !entry.IsDirectory || entry.Version != item.ETag) throw new SourceConflictException("目录身份变化。");
            await foreach (var _ in ListAsync(path, token).ConfigureAwait(false)) throw new SourceConflictException("测试目录非空。");
        }
        else
        {
            if (entry.IsDirectory || entry.Size > 1024) throw new SourceConflictException("检测文件长度或类型变化。");
            using var response = await DownloadAsync(entry, token).ConfigureAwait(false);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var bytes = new byte[checked((int)entry.Size)];
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0 || !item.Hashes.Contains(CapabilityProbeSession.Hash(bytes), StringComparer.Ordinal))
                throw new SourceConflictException("检测内容变化。");
            var after = await GetMetadataAsync(path, token).ConfigureAwait(false);
            if (after.Version != entry.Version) throw new SourceConflictException("检测核对期间版本变化。");
        }
        var started = await ApiAsync("SYNO.FileStation.Delete", "start", new() { ["path"] = Paths(RemotePath(path)), ["recursive"] = "false" }, true, token).ConfigureAwait(false);
        var id = started.GetProperty("taskid").GetString() ?? throw new IOException("清理缺少任务回执。");
        while (true)
        {
            var progress = await ApiAsync("SYNO.FileStation.Delete", "status", new() { ["taskid"] = id }, false, token).ConfigureAwait(false);
            if (progress.GetProperty("finished").GetBoolean()) break;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        if (await TryGetMetadataAsync(path, token).ConfigureAwait(false) is not null) throw new IOException("检测项仍存在。");
        session.Update(path, value => value with { Stage = "Cleaned", Reason = null });
    }
}
