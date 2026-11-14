using System.Security.Cryptography;
using System.Text;
using HybridMount.Services.Localization;

namespace HybridMount.Models.Mounting.Recovery;

/// <summary>仅用于导航入口恢复；身份包含模式和命名空间，不以目录或显示名称去重。</summary>
internal sealed record ExplorerEntryKey(string Mode, string SyncRootId, string? NamespaceId)
{
    public string StableId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Mode + "\0" + SyncRootId + "\0" + NamespaceId)));
}

/// <summary>只记录根目录属性身份；缺失目录也有明确状态，不枚举正文或子目录。</summary>
internal sealed record ExplorerPathEvidence(bool Exists, string Identity);

/// <summary>单个注册视图的精确备份；删除时不重新捕获变化后的值。</summary>
internal sealed record ExplorerEntryProof(RecoveryRegistryBackup[] Keys, RecoveryHiddenValue[] Hidden);

/// <summary>独立于清空目录候选。两份证明对应应用视图和维护入口的真实注册视图。</summary>
internal sealed record ExplorerEntryCandidate(ExplorerEntryKey Key, string Name, string? LocalPath,
    ExplorerEntryProof? Local, ExplorerEntryProof? External = null, ExplorerPathEvidence? Evidence = null,
    string? Error = null, int? HResult = null)
{
    public bool CanRemove => Error is null && LocalPath is not null && Evidence is not null;
    public string Display => Name + "\n" + (LocalPath ?? UiText.Text("ExplorerEntryNoPath")) + "\n" +
        UiText.Text(Key.Mode == "Packaged" ? "RecoveryModePackaged" : "RecoveryModeUnpackaged") + " · " +
        (Error ?? UiText.Text("ExplorerEntryReady")) + "\n" + Key.SyncRootId + "\n" + Key.NamespaceId;
}

/// <summary>独立恢复记录保留两视图备份和逐入口阶段；不改变业务数据库。</summary>
internal sealed record ExplorerEntryJournal(int Schema, ExplorerEntryCandidate Entry, string Stage,
    string? Error = null, int? HResult = null);
internal sealed record ExplorerEntryResult(ExplorerEntryCandidate Entry, bool Completed, string Stage,
    string? Error = null, int? HResult = null)
{
    public string Display => Entry.Name + "\n" + Entry.LocalPath + "\n" +
        (Completed ? UiText.Text("ExplorerEntryRemoved") : Error ?? UiText.Text("ExplorerEntryNotStarted")) +
        (HResult is {} code ? $" · 0x{code:X8}" : "");
}
