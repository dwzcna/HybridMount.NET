using Microsoft.Win32;
using System.Security.Principal;
using System.Text.Json;

namespace HybridMount.Models.Mounting.Recovery;

/// <summary>注册访问边界；测试使用内存存储，不向 Explorer 注册测试根。</summary>
internal interface IExplorerEntryRegistry
{
    string[] Names(RegistryHive hive, string path);
    RecoveryRegistryBackup? Read(RegistryHive hive, string path);
    void Delete(RecoveryRegistryBackup expected);
    void DeleteValue(RecoveryHiddenValue expected);
}

/// <summary>64位注册表精确快照读写；删除前再次比较全部值、类型、子键和所有者。</summary>
internal sealed class ExplorerEntryRegistry : IExplorerEntryRegistry
{
    public string[] Names(RegistryHive hive, string path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path);
        var names = key?.GetSubKeyNames() ?? [];
        if (names.Length > 16384) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError01"));
        return names;
    }
    public RecoveryRegistryBackup? Read(RegistryHive hive, string path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path);
        return key is null ? null : RecoveryShellRegistration.Backup(hive, path, key);
    }
    public void Delete(RecoveryRegistryBackup expected)
    {
        using var baseKey = RegistryKey.OpenBaseKey(expected.Hive, RegistryView.Registry64);
        using (var key = baseKey.OpenSubKey(expected.Path))
        {
            if (key is null) return;
            if (!Same(expected, RecoveryShellRegistration.Backup(expected.Hive, expected.Path, key)))
                throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError02"));
        }
        baseKey.DeleteSubKeyTree(expected.Path, false);
    }
    public void DeleteValue(RecoveryHiddenValue expected)
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = user.OpenSubKey(expected.Path, true);
        if (key is null || !key.GetValueNames().Contains(expected.Value.Name, StringComparer.OrdinalIgnoreCase)) return;
        if (RecoveryShellRegistration.Owner(key) != expected.Owner ||
            JsonSerializer.Serialize(RecoveryShellRegistration.ReadValue(key, expected.Value.Name)) != JsonSerializer.Serialize(expected.Value))
            throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError03"));
        key.DeleteValue(expected.Value.Name, false);
    }
    internal static bool Same(RecoveryRegistryBackup left, RecoveryRegistryBackup right) =>
        left.Hive == right.Hive && left.Path == right.Path && left.Owner == right.Owner &&
        JsonSerializer.Serialize(left.Tree) == JsonSerializer.Serialize(right.Tree);
}

/// <summary>只认识两种现有产品身份；跨模式能力不用于原生注销和目录清空。</summary>
internal sealed class ExplorerEntryRegistration(IExplorerEntryRegistry registry, string sid)
{
    private const string Manager = RecoveryShellRegistration.Manager;
    private const string Namespace = RecoveryShellRegistration.Namespace;
    private const string Classes = RecoveryShellRegistration.Classes;
    private static readonly RegistryHive[] Hives = [RegistryHive.LocalMachine, RegistryHive.CurrentUser];
    internal static bool TryIdentity(string id, string sid, out string mode)
    {
        foreach (var value in new[] { "Packaged", "Unpackaged" })
        {
            var prefix = "HybridMount.NET." + value + "!" + sid + "!";
            if (id.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(id[prefix.Length..], "N", out var guid) && guid != Guid.Empty)
            { mode = value; return true; }
        }
        mode = ""; return false;
    }
    internal static string? Value(RecoveryRegistryTree? tree, string name) =>
        tree?.Values.FirstOrDefault(x => x.Name == name && x.Kind is RegistryValueKind.String or RegistryValueKind.ExpandString)?.Data.SingleOrDefault();
    private static string? PathValue(RecoveryRegistryTree tree, string name)
    {
        var value = tree.Values.FirstOrDefault(x => x.Name == name);
        if (value is {Kind: RegistryValueKind.ExpandString} && value.Data.Any(x => x.Contains('%')))
            throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError09"));
        return Value(tree,name);
    }
    private static string? GuidText(string? raw) => Guid.TryParseExact(raw, "B", out var value) && value != Guid.Empty ? value.ToString("B").ToUpperInvariant() : null;
    private static bool SamePath(string? left, string? right) => left is not null && right is not null &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private IEnumerable<RecoveryRegistryBackup> Roots()
    {
        foreach (var hive in Hives)
            foreach (var id in registry.Names(hive, Manager))
                if (registry.Read(hive, Manager + "\\" + id) is {} root) yield return root;
    }
    /// <summary>只读列举本用户本产品入口；损坏但可识别的项目保留为不可选项。</summary>
    internal ExplorerEntryCandidate[] Discover(CancellationToken token)
    {
        var roots = Roots().ToArray();
        var hints = new Dictionary<ExplorerEntryKey, List<RecoveryRegistryBackup>>();
        void Add(string id, string? ns, RecoveryRegistryBackup key)
        {
            if (!TryIdentity(id, sid, out var mode)) return;
            var identity = new ExplorerEntryKey(mode, id, ns);
            if (!hints.TryGetValue(identity, out var keys)) hints.Add(identity, keys = []);
            keys.Add(key);
        }
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            Add(root.Path[(Manager.Length + 1)..], GuidText(Value(root.Tree, "NamespaceCLSID")), root);
        }
        foreach (var name in registry.Names(RegistryHive.CurrentUser, Namespace.TrimEnd('\\')))
        {
            token.ThrowIfCancellationRequested();
            if (GuidText(name) is not {} ns || registry.Read(RegistryHive.CurrentUser, Namespace + ns) is not {} key) continue;
            if (Value(key.Tree, "") is {} id) Add(id, ns, key);
        }
        var result = new List<ExplorerEntryCandidate>();
        foreach (var (key, keys) in hints)
        {
            token.ThrowIfCancellationRequested();
            var paths = new List<string>(); var names = new List<string>(); var hidden = new List<RecoveryHiddenValue>();
            string? error = null;
            try
            {
                foreach (var root in keys.Where(x => x.Path.StartsWith(Manager + "\\", StringComparison.Ordinal)))
                {
                    if (!root.Tree.Children.TryGetValue("UserSyncRoots", out var users) || users.Values.Length != 1 ||
                        users.Values[0].Name != sid || PathValue(users, sid) is not {} path)
                        throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError04"));
                    paths.Add(path);
                    if (Value(root.Tree, "DisplayNameResource") is {} title) names.Add(title);
                    if (Value(root.Tree, "NamespaceCLSID") is {} raw && GuidText(raw) != key.NamespaceId)
                        throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError05"));
                }
                if (key.NamespaceId is {} ns)
                {
                    var space = registry.Read(RegistryHive.CurrentUser, Namespace + ns);
                    if (space is not null && !keys.Any(x => x.Path == space.Path)) keys.Add(space);
                    if (space is not null && (Value(space.Tree, "") != key.SyncRootId || space.Tree.Children.Count != 0))
                        throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError06"));
                    var cls = registry.Read(RegistryHive.CurrentUser, Classes + ns);
                    if (cls is not null)
                    {
                        keys.Add(cls);
                        if (!cls.Tree.Children.TryGetValue("Instance", out var instance) || !instance.Children.TryGetValue("InitPropertyBag", out var bag) ||
                            PathValue(bag, "TargetFolderPath") is not {} path) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError07"));
                        paths.Add(path); if (Value(cls.Tree, "") is {} title) names.Add(title);
                    }
                    if (roots.Any(x => Value(x.Tree, "NamespaceCLSID") is {} raw && GuidText(raw) == ns && x.Path[(Manager.Length + 1)..] != key.SyncRootId))
                        throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError08"));
                    foreach (var hiddenPath in RecoveryShellRegistration.HiddenPaths)
                    {
                        var tree = registry.Read(RegistryHive.CurrentUser, hiddenPath);
                        if (tree?.Tree.Values.FirstOrDefault(x => x.Name.Equals(ns, StringComparison.OrdinalIgnoreCase)) is {} value)
                            hidden.Add(new(hiddenPath, tree.Owner, value));
                    }
                }
                if (paths.Count == 0 || paths.Any(x => !Path.IsPathFullyQualified(x) || x.StartsWith(@"\\", StringComparison.Ordinal)) ||
                    paths.Any(x => !SamePath(paths[0], x))) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError09"));
                if (keys.Any(x => x.Owner != sid) || hidden.Any(x => x.Owner != sid)) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError10"));
            }
            catch (Exception failure) when (failure is IOException or ArgumentException) { error = failure.Message; }
            var pathValue = paths.FirstOrDefault();
            try { if (pathValue is not null && Path.IsPathFullyQualified(pathValue)) pathValue = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathValue)); }
            catch (ArgumentException failure) { error = failure.Message; pathValue = null; }
            result.Add(new(key, names.FirstOrDefault() ?? key.SyncRootId, pathValue,
                new(keys.DistinctBy(x => (x.Hive, x.Path)).ToArray(), hidden.ToArray()), Error: error));
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key.StableId).ToArray();
    }
    /// <summary>白名单和快照核验在任何删除前完成；重试只允许已备份项目缺失，不接纳新出现的注册。</summary>
    internal void Remove(ExplorerEntryCandidate entry, ExplorerEntryProof proof, Action validate, CancellationToken token)
    {
        ValidateProof(entry, proof);
        using var lease = RootRegistryLease.Acquire();
        validate(); token.ThrowIfCancellationRequested();
        void CheckShared()
        {
            if (entry.Key.NamespaceId is {} ns && Roots().Any(x => Value(x.Tree,"NamespaceCLSID") is {} raw &&
                GuidText(raw) == ns && x.Path[(Manager.Length+1)..] != entry.Key.SyncRootId))
                throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError08"));
        }
        CheckShared();
        var current = Discover(token).FirstOrDefault(x => x.Key == entry.Key);
        if (current?.Error is {} error) throw new IOException(error);
        if (current is not null && !SamePath(current.LocalPath, entry.LocalPath)) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError11"));
        foreach (var key in current?.Local?.Keys ?? [])
            if (!proof.Keys.Any(x => ExplorerEntryRegistry.Same(x, key))) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError12"));
        foreach (var hidden in current?.Local?.Hidden ?? [])
            if (!proof.Hidden.Any(x => JsonSerializer.Serialize(x) == JsonSerializer.Serialize(hidden))) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError13"));
        foreach (var key in proof.Keys)
            if (registry.Read(key.Hive, key.Path) is {} actual && !ExplorerEntryRegistry.Same(key, actual)) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError14"));
        foreach (var key in proof.Keys)
        {
            token.ThrowIfCancellationRequested(); validate(); CheckShared(); registry.Delete(key);
        }
        foreach (var value in proof.Hidden)
        {
            token.ThrowIfCancellationRequested(); validate(); CheckShared(); registry.DeleteValue(value);
        }
    }
    internal string[] Remaining(ExplorerEntryCandidate entry, ExplorerEntryProof proof)
    {
        ValidateProof(entry, proof);
        var result = proof.Keys.Where(x => registry.Read(x.Hive, x.Path) is not null).Select(x => x.Hive + "\\" + x.Path).ToList();
        foreach (var value in proof.Hidden)
            if (registry.Read(RegistryHive.CurrentUser, value.Path)?.Tree.Values.Any(x => x.Name.Equals(value.Value.Name, StringComparison.OrdinalIgnoreCase)) == true)
                result.Add(value.Path + " [" + value.Value.Name + "]");
        if (Discover(default).Any(x => x.Key == entry.Key)) result.Add(entry.Key.SyncRootId);
        return result.Distinct().ToArray();
    }
    private void ValidateProof(ExplorerEntryCandidate entry, ExplorerEntryProof proof)
    {
        if (!TryIdentity(entry.Key.SyncRootId, sid, out var mode) || mode != entry.Key.Mode || entry.Error is not null || entry.LocalPath is null || entry.Evidence is null)
            throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError15"));
        var ns = entry.Key.NamespaceId;
        if (ns is not null && GuidText(ns) != ns) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError16"));
        if (proof.Keys.Length == 0) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError17"));
        foreach (var item in proof.Keys)
        {
            if (item.Owner != sid) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError18"));
            if (Hives.Contains(item.Hive) && item.Path == Manager + "\\" + entry.Key.SyncRootId)
            {
                if (!item.Tree.Children.TryGetValue("UserSyncRoots", out var users) || users.Values.Length != 1 ||
                    users.Values[0].Name != sid || !SamePath(PathValue(users, sid), entry.LocalPath) ||
                    Value(item.Tree, "NamespaceCLSID") is {} raw && GuidText(raw) != ns) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError19"));
            }
            else if (item.Hive == RegistryHive.CurrentUser && ns is not null && item.Path == Namespace + ns)
            {
                if (Value(item.Tree, "") != entry.Key.SyncRootId || item.Tree.Children.Count != 0) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError20"));
            }
            else if (item.Hive == RegistryHive.CurrentUser && ns is not null && item.Path == Classes + ns)
            {
                if (!item.Tree.Children.TryGetValue("Instance", out var instance) || !instance.Children.TryGetValue("InitPropertyBag", out var bag) ||
                    !SamePath(PathValue(bag, "TargetFolderPath"), entry.LocalPath)) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError21"));
                if (!proof.Keys.Any(x => x.Path == Namespace + ns || x.Path == Manager + "\\" + entry.Key.SyncRootId)) throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError22"));
            }
            else throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError23"));
        }
        foreach (var item in proof.Hidden)
            if (item.Owner != sid || ns is null || !RecoveryShellRegistration.HiddenPaths.Contains(item.Path) || !item.Value.Name.Equals(ns, StringComparison.OrdinalIgnoreCase))
                throw new IOException(global::HybridMount.Services.Localization.UiText.Text("ExplorerRecoveryError24"));
    }
}
