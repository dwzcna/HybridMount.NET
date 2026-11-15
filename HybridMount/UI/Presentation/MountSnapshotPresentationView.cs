using HybridMount.Models.Protocols.S3;
using HybridMount.Infrastructure.Protocols.S3;
using HybridMount.UI.Localization;
using HybridMount.UI.Presentation;
using HybridMount.Models.Mounting;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class MountSnapshotPresentationView(MountSnapshot value)
{
    private readonly MountSnapshot _value=value;
    private MountDefinition Definition => _value.Definition;
    private string Status => _value.Status;
    private string Detail => _value.Detail;
    private RemoteDetectionStatus? Detection => _value.Detection;
    private bool IsConnected => _value.IsConnected;
    private bool IsConnecting => _value.IsConnecting;
    private bool IsDisconnecting => _value.IsDisconnecting;
    private string? ActiveOperation => _value.ActiveOperation;
    private RemoteCapacitySnapshot? Capacity => _value.Capacity;
    private int UploadIssueCount => _value.UploadIssueCount;
    private ProtocolCapabilitySnapshot? ProtocolCapabilities => _value.ProtocolCapabilities;
/// <summary>连接状态为稳定代码；未知诊断保持原文，不从显示文字反向猜测资源。</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayStatus=>Status switch
    {
        "NotConnected"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NotConnected",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "Checking"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Checking",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "RemovalIncomplete"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemovalIncomplete",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "MigrationOutRequired"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("MigrationOutRequired",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "RecoveryRequired"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RecoveryRequired",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "ConnectionInProgress"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ConnectionInProgress",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "Connected"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Connected",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "Faulted"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Faulted",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "NativeBackendDisconnected"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NativeBackendDisconnected",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "Clearing"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Clearing",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        "Removing"=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Removing",global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,
        _=>Status
    };
public string CapacityDisplay=>Capacity?.Display??(!CapacityRules.Enabled(Definition)?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemoteCapacityNoCapacityLimitSet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:Definition.CapacityAutoDetectEnabled?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemoteCapacityAutomaticDetectionNotQueriedYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("RemoteCapacityManualLimitUsedAndAvailableSpaceHaveNotBeenVerified", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, TaskByteFormat.Format(Definition.MaximumCapacityBytes!.Value)));
public string RemoteDetectionDisplay=>Definition.Source?.Kind is not(FileSourceKind.WebDav or FileSourceKind.S3 or FileSourceKind.Synology)?"":!Definition.RemoteAutoDetectEnabled?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("AutomaticRemoteDetectionOff", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:
         string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("AutomaticRemoteDetectionFullScanEverySecondsLastSuccess", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, Detection?.Mode??global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NotDetectedYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, (Definition.Source?.Kind is FileSourceKind.S3 or FileSourceKind.Synology?"":string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("IncrementalScanEverySeconds", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, Definition.RemoteIncrementalSeconds)), Definition.RemoteFullSeconds, Detection?.LastSuccessUtc?.ToLocalTime().ToString("g")??global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NoneYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, (Detection?.Error is {} error?"；"+error:""));
public string SqsDisplay=>Definition.Source?.Kind!=FileSourceKind.S3?"":Definition.Sqs?.Enabled!=true?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SQSEventAccelerationOffScheduledFullReconciliationRemainsEnabled", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:!Definition.RemoteAutoDetectEnabled?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SQSEventAccelerationPausedAutomaticRemoteDetectionIsOff", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:
        string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SQSLastEventHintsAwaitingChecking", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, Detection?.QueueMode??global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NotStartedYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, Detection?.LastEventUtc?.ToLocalTime().ToString("g")??global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NoneYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, Detection?.PendingHints??0, (Detection?.QueueError is {} error?"；"+error:""));
public string AutoUploadDisplay=>Definition.Source is null?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("LegacySourcesSupportMigrationOutOnly", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NewAndEditedLocalFilesUploadContinuously", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
public string ThumbnailDisplay=>Definition.ThumbnailsEnabled?global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ThumbnailsOnExperimentalGeneratedAccordingToFileAndServiceReadCapabilitiesA", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!:global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ThumbnailsOff", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
public string SourceDisplay => Definition.Source?.Kind switch { FileSourceKind.WebDav=>"WebDAV",FileSourceKind.S3=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("S3ObjectStorage", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,FileSourceKind.Synology=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SynologyNAS", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,FileSourceKind.Smb=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SMBReserved", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,FileSourceKind.Nfs=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NFSReserved", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!,_=>global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("LegacySimulatedSourceMigrationOutOnly", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! };
}
