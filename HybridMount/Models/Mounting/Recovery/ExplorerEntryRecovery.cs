using System.Security.Principal;
using System.Text.Json;
using HybridMount.Models.Persistence;

namespace HybridMount.Models.Mounting.Recovery;

/// <summary>目录属性核验边界；隔离测试不查询或修改真实挂载。</summary>
internal interface IExplorerEntryPathGuard { ExplorerPathEvidence Inspect(string path); }

/// <summary>只打开属性身份并查询同步根；不枚举子目录，不读正文，不连接云端。</summary>
internal sealed class ExplorerEntryPathGuard : IExplorerEntryPathGuard
{
    public ExplorerPathEvidence Inspect(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError25"));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var volume = Path.GetPathRoot(full)!;
        if (full.Equals(Path.TrimEndingDirectorySeparator(volume), StringComparison.OrdinalIgnoreCase)) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError26"));
        if (!new DriveInfo(volume).IsReady) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError27"));
        for (var cursor = full; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if (LocalFileMetadata.TryRead(cursor) is not {} metadata) continue;
            if ((metadata.Attributes & FileAttributes.Directory) == 0) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError28"));
            if ((metadata.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError29"));
        }
        var exists = LocalFileMetadata.TryRead(full) is not null;
        // 缺失子目录也需核对最近存在的父目录，防止把有效挂载的子路径误当遗留入口。
        var query = full;
        while (LocalFileMetadata.TryRead(query) is null) query = Path.GetDirectoryName(query) ?? throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError30"));
        if (CloudFilesNative.RootIdentity(query, strict: true) is not null) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError31"));
        return new(exists, exists ? MountPathGuard.FileIdentity(full) : "");
    }
}

/// <summary>应用视图与真实视图访问边界；所有清理都使用同一份用户确认的证明。</summary>
internal interface IExplorerEntryAccess
{
    ExplorerEntryCandidate[] Discover(CancellationToken token);
    void Remove(ExplorerEntryCandidate entry, Action validate, CancellationToken token);
    string[] Remaining(ExplorerEntryCandidate entry);
}

/// <summary>默认只读两模式；打包运行额外通过已认证维护入口访问真实 HKCU。</summary>
internal sealed class ExplorerEntryAccess : IExplorerEntryAccess
{
    private readonly ExplorerEntryRegistration _local = new(new ExplorerEntryRegistry(), WindowsIdentity.GetCurrent().User!.Value);
    private static bool External => WinRegisterService.IsPackaged();
    public ExplorerEntryCandidate[] Discover(CancellationToken token)
    {
        var found = _local.Discover(token).ToDictionary(x => x.Key);
        if (External)
            foreach (var item in ShellMaintenanceProcess.SendExplorer("explorerDiscover").Entries ?? throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError32")))
            {
                token.ThrowIfCancellationRequested();
                if (found.TryGetValue(item.Key, out var previous))
                {
                    var samePath = string.Equals(previous.LocalPath, item.LocalPath, StringComparison.OrdinalIgnoreCase);
                    found[item.Key] = previous with { External = item.Local, Error = previous.Error ?? item.Error ?? (samePath ? null : global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError33")) };
                }
                else found.Add(item.Key, item with { Local = null, External = item.Local });
            }
        return found.Values.ToArray();
    }
    public void Remove(ExplorerEntryCandidate entry, Action validate, CancellationToken token)
    {
        if (entry.External is {} external)
        {
            validate(); token.ThrowIfCancellationRequested();
            var reply = ShellMaintenanceProcess.SendExplorer("explorerRemove", entry, external);
            if (reply.Remaining is not { Length: 0 }) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError34"));
        }
        if (entry.Local is {} local) _local.Remove(entry, local, validate, token);
    }
    public string[] Remaining(ExplorerEntryCandidate entry)
    {
        var result = entry.Local is {} local ? _local.Remaining(entry, local).ToList() : [];
        if (entry.External is {} external)
            result.AddRange(ShellMaintenanceProcess.SendExplorer("explorerVerify", entry, external).Remaining ?? throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError35")));
        return result.Distinct().ToArray();
    }
}

/// <summary>专用入口恢复器。应用配置锁保护本进程，两端均复核另一模式配置；不调用原生注销或内容清理器。</summary>
internal sealed class ExplorerEntryRecovery(string directory, Func<IReadOnlyList<MountDefinition>> managed,
    IExplorerEntryAccess access, IExplorerEntryPathGuard paths, Action<string> notify)
{
    private string FileName(ExplorerEntryCandidate entry) => Path.Combine(directory, entry.Key.StableId + ".json");
    internal static void VerifyManaged(ExplorerEntryCandidate entry, IReadOnlyList<MountDefinition> definitions)
    {
        if (!ExplorerEntryRegistration.TryIdentity(entry.Key.SyncRootId, WindowsIdentity.GetCurrent().User!.Value, out var mode) || mode != entry.Key.Mode)
            throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError36"));
        var id = Guid.ParseExact(entry.Key.SyncRootId[(entry.Key.SyncRootId.LastIndexOf('!') + 1)..], "N");
        if (definitions.Any(x => x.Id == id || entry.LocalPath is {} path &&
            (MountPathGuard.IsUnder(x.LocalPath, path) || MountPathGuard.IsUnder(path, x.LocalPath))))
            throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError37"));
    }
    internal static IReadOnlyList<MountDefinition> ReadPhysicalDefinitions()
    {
        var result = new List<MountDefinition>();
        foreach (var packaged in new[] { true, false })
        {
            var path = ApplicationPaths.MountDefinitionsRegistryPathFor(packaged);
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path);
            if (key is not null)
                foreach (var name in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(name);
                    if (child?.GetValue("Definition") is not string)
                        throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError37"));
                }
            result.AddRange(new RegistryMountDefinitionStore(path).LoadDefinitions());
        }
        return result;
    }
    /// <summary>未完成的入口备份不能被设置重置静默删除；只读核对当前模式的数据目录。</summary>
    internal static void EnsureNoPending(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory,"*.json"))
        {
            if (new FileInfo(file).Length > ShellMaintenanceProcess.MaximumBytes) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError42"));
            var journal = JsonSerializer.Deserialize<ExplorerEntryJournal>(File.ReadAllBytes(file),new JsonSerializerOptions{MaxDepth=32});
            if (journal is null || journal.Schema != 1 || journal.Stage != "Completed" || Path.GetFileNameWithoutExtension(file) != journal.Entry.Key.StableId)
                throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_4057AC6E04F7"));
        }
    }

    internal ExplorerEntryCandidate[] Discover(CancellationToken token)
    {
        var definitions = managed(); // 任一模式读取失败整轮报告，不能把失败当作没有配置。
        var found = access.Discover(token).ToDictionary(x => x.Key);
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory,"*.json"))
            {
                token.ThrowIfCancellationRequested();
                if (new FileInfo(file).Length > ShellMaintenanceProcess.MaximumBytes) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError42"));
                var old = JsonSerializer.Deserialize<ExplorerEntryJournal>(File.ReadAllBytes(file),new JsonSerializerOptions{MaxDepth=32}) ?? throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError43"));
                if (old.Schema != 1 || Path.GetFileNameWithoutExtension(file) != old.Entry.Key.StableId || old.Stage is not ("Confirmed" or "Removing" or "Completed"))
                    throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError44"));
                if (old.Stage != "Completed" && !found.ContainsKey(old.Entry.Key)) found.Add(old.Entry.Key,old.Entry with{Error=null,HResult=null});
            }
        return found.Values.Select(entry =>
        {
            token.ThrowIfCancellationRequested();
            if (entry.Error is not null) return entry;
            try
            {
                VerifyManaged(entry, definitions);
                if (entry.LocalPath is null) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError38"));
                return entry with { Evidence = paths.Inspect(entry.LocalPath) };
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
            { return entry with { Error = error.Message, HResult = OrphanedMountBatch.Code(error) }; }
        }).ToArray();
    }
    internal ExplorerEntryResult Remove(ExplorerEntryCandidate selected, CancellationToken token)
    {
        if (!selected.CanRemove) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError39"));
        var journal = Read(selected) ?? new ExplorerEntryJournal(1, selected, "Confirmed");
        // 未完成记录必须保留原备份，重新检索也不能覆盖部分清理前的证据。
        var entry = journal.Stage == "Completed" ? selected : journal.Entry;
        if (entry.Key != selected.Key || entry.LocalPath != selected.LocalPath || entry.Evidence != selected.Evidence)
            throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError40"));
        journal = journal with { Entry = entry, Stage = "Confirmed", Error = null, HResult = null };
        void Validate()
        {
            VerifyManaged(entry, managed());
            if (entry.LocalPath is null || paths.Inspect(entry.LocalPath) != entry.Evidence) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError41"));
        }
        try
        {
            token.ThrowIfCancellationRequested(); Validate(); Save(journal);
            journal = journal with { Stage = "Removing" }; Save(journal);
            access.Remove(entry, Validate, token);
            Validate();
            var remaining = access.Remaining(entry);
            if (remaining.Length != 0) throw new IOException("Explorer 注册仍有残留：" + string.Join("；", remaining));
            journal = journal with { Stage = "Completed" }; Save(journal);
            // 注册核验成功与 Explorer 的异步可见刷新是不同阶段。
            notify(entry.LocalPath!);
            return new(entry, true, "Completed");
        }
        catch (Exception error)
        {
            journal = journal with { Error = error.Message, HResult = OrphanedMountBatch.Code(error) }; Save(journal);
            if (error is OperationCanceledException) throw;
            return new(entry, false, journal.Stage, error.Message, journal.HResult);
        }
    }
    private ExplorerEntryJournal? Read(ExplorerEntryCandidate entry)
    {
        var file = FileName(entry);
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > ShellMaintenanceProcess.MaximumBytes) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError42"));
        var result = JsonSerializer.Deserialize<ExplorerEntryJournal>(File.ReadAllBytes(file), new JsonSerializerOptions { MaxDepth = 32 }) ?? throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError43"));
        if (result.Schema != 1 || result.Entry.Key != entry.Key || result.Stage is not ("Confirmed" or "Removing" or "Completed")) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError44"));
        return result;
    }
    private void Save(ExplorerEntryJournal journal)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        if (bytes.Length > ShellMaintenanceProcess.MaximumBytes) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError45"));
        Directory.CreateDirectory(directory);
        var file = FileName(journal.Entry); var temp = file + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
