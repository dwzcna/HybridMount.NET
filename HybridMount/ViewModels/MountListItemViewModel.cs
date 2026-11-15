using HybridMount.Models.Mounting;
using HybridMount.UI.Localization;
using HybridMount.UI.Presentation;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
namespace HybridMount.ViewModels;

/// <summary>挂载行的稳定展示对象，由唯一页面模型更新；不订阅服务、不保存配置或发起网络。</summary>
public sealed class MountListItemViewModel(MountSnapshot snapshot) : ObservableObject
{
    private MountSnapshot _snapshot = snapshot;
    private bool _selected, _busy, _stopping, _dialogBusy, _canceling;
    public Guid Id => _snapshot.Definition.Id;
    public MountSnapshot Snapshot => _snapshot;
    public string Name => _snapshot.Definition.Name;
    public string SourceAndPath => _snapshot.SourceDisplay + "  " + _snapshot.Definition.LocalPath;
    public bool IsSelected => _selected;
    public MountActionState Actions => MountActionState.From(_snapshot, _busy, _stopping);
    public int UploadIssueCount => _snapshot.UploadIssueCount;
    public string IssueBadge => UploadIssueCount > 99 ? "99+" : UploadIssueCount.ToString(CultureInfo.CurrentCulture);
    public bool HasUploadIssues => UploadIssueCount > 0;
    public string DiagnosticTooltip => HasUploadIssues ? string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SyncIssuesClickToViewRightClickForFullDiagnostics", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, UploadIssueCount) : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("FullDiagnosticsDetails", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public bool CanOpen => Actions.CanOpen && !_dialogBusy;
    public bool CanInspect => !_stopping && !_dialogBusy;
    public bool CanEdit => Actions.CanEdit && !_dialogBusy;
    public bool CanRun => Actions.CanRunConnected && !_dialogBusy;
    public bool CanRemove => Actions.CanRemove && !_dialogBusy;
    public bool CanRecheck => CanRun && CapacityRules.Enabled(_snapshot.Definition);
    public string RemoveText => Actions.RemoveText;
    public bool IsConnected => _snapshot.IsConnected;
    public bool CanConfigureThumbnails { get; private set; } = true;
    public bool ThumbnailsEnabled => _snapshot.Definition.ThumbnailsEnabled;
    public bool RemoteDetectionEnabled => _snapshot.Definition.RemoteAutoDetectEnabled;
    public string ThumbnailText => ThumbnailsEnabled ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("HideThumbnails", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ShowThumbnailsExperimental", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public string DetectionText => RemoteDetectionEnabled ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("DisableAutomaticRemoteDetection", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("EnableAutomaticRemoteDetection", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
    public string ConnectionActionGlyph => _snapshot.IsConnecting ? "\uE711" : _snapshot.IsConnected ? "\uE8CD" : "\uE768";
    public string ConnectionText => _snapshot.IsDisconnecting ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Disconnecting", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : _snapshot.IsConnecting ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("CancelConnection", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : _snapshot.IsConnected ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Disconnect", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : Actions.ConnectText;
    public bool CanChangeConnection => !_dialogBusy && (_snapshot.IsConnecting ? Actions.CanCancelConnection && !_canceling : _snapshot.IsConnected ? Actions.CanDisconnect : Actions.CanConnect);
    public string ConnectionStatus => _snapshot.IsDisconnecting ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("DisconnectingDetails", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : _snapshot.IsConnecting ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Connecting", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : _snapshot.DisplayStatus;
    public string ConnectionGlyph => _snapshot.IsConnecting || _snapshot.IsDisconnecting ? "\uE895" : _snapshot.Status is "Faulted" or "RecoveryRequired" ? "\uE7BA" : _snapshot.IsConnected ? "\uE73E" : "\uE8CD";
    private long? Used => _snapshot.Capacity?.Value.UsedBytes is >= 0 ? _snapshot.Capacity.Value.UsedBytes : null;
    private long? Total => _snapshot.Capacity?.Value.TotalBytes ?? (!_snapshot.Definition.CapacityAutoDetectEnabled ? _snapshot.Definition.MaximumCapacityBytes : null);
    public string UsedText => string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Used", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, FormatBytes(Used));
    public string TotalText => string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("TotalSpace", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, (Total is { } total ? FormatBytes(total) : CapacityRules.Enabled(_snapshot.Definition) ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Unknown", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Unlimited", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!));
    public string PercentageText => Used is { } used && Total is > 0 ? $"{((decimal)used / Total.Value * 100).ToString("0.0", CultureInfo.CurrentCulture)}%" : "—";
    public string CapacityDetails => _snapshot.CapacityDisplay;
    /// <summary>卡片以十进制容量单位固定两位小数；未知值不冒充零，精确值保留在容量诊断。</summary>
    public static string FormatBytes(long? bytes)
    {
        if (bytes is null or < 0) return global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Unknown", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
        string[] units = ["kB", "MB", "GB", "TB", "PB", "EB"]; decimal value = bytes.Value / 1000m; var index = 0;
        while (value >= 1000 && index < units.Length - 1) { value /= 1000; index++; }
        value = decimal.Round(value, 2, MidpointRounding.AwayFromZero); if (value >= 1000 && index < units.Length - 1) { value /= 1000; index++; }
        return value.ToString("0.00", CultureInfo.CurrentCulture) + " " + units[index];
    }
    /// <summary>UI线程一次更新所有派生属性；虚拟化重用对象，保持选择和滚动，不增加事件订阅。</summary>
    internal void Update(MountSnapshot value, bool selected, bool busy, bool stopping, bool dialogBusy, bool canceling, bool thumbnailsAvailable = true)
    {
        if (CanConfigureThumbnails == thumbnailsAvailable && _snapshot == value && (_selected, _busy, _stopping, _dialogBusy, _canceling) == (selected, busy, stopping, dialogBusy, canceling)) return;
        CanConfigureThumbnails = thumbnailsAvailable; _snapshot = value; (_selected, _busy, _stopping, _dialogBusy, _canceling) = (selected, busy, stopping, dialogBusy, canceling); OnPropertyChanged(string.Empty);
    }
}
