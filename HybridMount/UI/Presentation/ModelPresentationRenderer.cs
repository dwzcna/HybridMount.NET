using HybridMount.Models.Mounting.Capabilities;
using System.Globalization;
using System.Runtime.CompilerServices;
using HybridMount.Commons.Presentation;
using HybridMount.Models.Localization;
using HybridMount.UI.Localization;
using HybridMount.Models.Diagnostics;
using HybridMount.Models.Mounting;
using HybridMount.Models.Recovery;
namespace HybridMount.UI.Presentation;

/// <summary>兼容旧JSON派生字段的明确投影；弱键缓存只复用同一不可变快照的视图，不延长快照寿命。</summary>
public sealed class ModelPresentationRenderer:IModelPresentation
{
    private static readonly ConditionalWeakTable<DiagnosticEntry,DiagnosticEntryPresentationView> DiagnosticEntryViews=new();
    private static readonly ConditionalWeakTable<OrphanedMountCandidate,OrphanedMountCandidatePresentationView> OrphanedMountCandidateViews=new();
    private static readonly ConditionalWeakTable<OrphanedMountBatchResult,OrphanedMountBatchResultPresentationView> OrphanedMountBatchResultViews=new();
    private static readonly ConditionalWeakTable<OrphanedMountInspection,OrphanedMountInspectionPresentationView> OrphanedMountInspectionViews=new();
    private static readonly ConditionalWeakTable<OperationStepSnapshot,OperationStepSnapshotPresentationView> OperationStepSnapshotViews=new();
    private static readonly ConditionalWeakTable<MountSnapshot,MountSnapshotPresentationView> MountSnapshotViews=new();
    private static readonly ConditionalWeakTable<MountItemResult,MountItemResultPresentationView> MountItemResultViews=new();
    private static readonly ConditionalWeakTable<MountOperationSnapshot,MountOperationSnapshotPresentationView> MountOperationSnapshotViews=new();
    private static readonly ConditionalWeakTable<ConnectionDiagnostic,ConnectionDiagnosticPresentationView> ConnectionDiagnosticViews=new();
    private static readonly ConditionalWeakTable<RemoteCapacitySnapshot,RemoteCapacitySnapshotPresentationView> RemoteCapacitySnapshotViews=new();
    private static readonly ConditionalWeakTable<ExplorerEntryCandidate,ExplorerEntryCandidatePresentationView> ExplorerEntryCandidateViews=new();
    private static readonly ConditionalWeakTable<ExplorerEntryResult,ExplorerEntryResultPresentationView> ExplorerEntryResultViews=new();
    public string Render(object model,string member,CultureInfo? culture=null) => model switch
    {
        ResourceMessage message when member=="Message" => MessageRenderer.Render(message,culture),
            DiagnosticEntry value => member switch
            {
                nameof(DiagnosticEntry.DisplayTime) => DiagnosticEntryViews.GetValue(value,static item=>new(item)).DisplayTime,
                nameof(DiagnosticEntry.DisplaySeverity) => DiagnosticEntryViews.GetValue(value,static item=>new(item)).DisplaySeverity,
                nameof(DiagnosticEntry.DisplayText) => DiagnosticEntryViews.GetValue(value,static item=>new(item)).DisplayText,
                _ => throw Unknown()
            },
            OrphanedMountCandidate value => member switch
            {
                nameof(OrphanedMountCandidate.ActionText) => OrphanedMountCandidateViews.GetValue(value,static item=>new(item)).ActionText,
                nameof(OrphanedMountCandidate.Display) => OrphanedMountCandidateViews.GetValue(value,static item=>new(item)).Display,
                _ => throw Unknown()
            },
            OrphanedMountBatchResult value => member switch
            {
                nameof(OrphanedMountBatchResult.Summary) => OrphanedMountBatchResultViews.GetValue(value,static item=>new(item)).Summary,
                nameof(OrphanedMountBatchResult.Detail) => OrphanedMountBatchResultViews.GetValue(value,static item=>new(item)).Detail,
                _ => throw Unknown()
            },
            OrphanedMountInspection value => member switch
            {
                nameof(OrphanedMountInspection.Message) => OrphanedMountInspectionViews.GetValue(value,static item=>new(item)).Message,
                _ => throw Unknown()
            },
            OperationStepSnapshot value => member switch
            {
                nameof(OperationStepSnapshot.DisplayName) => OperationStepSnapshotViews.GetValue(value,static item=>new(item)).DisplayName,
                nameof(OperationStepSnapshot.DisplayState) => OperationStepSnapshotViews.GetValue(value,static item=>new(item)).DisplayState,
                _ => throw Unknown()
            },
            MountSnapshot value => member switch
            {
                nameof(MountSnapshot.DisplayStatus) => MountSnapshotViews.GetValue(value,static item=>new(item)).DisplayStatus,
                nameof(MountSnapshot.CapacityDisplay) => MountSnapshotViews.GetValue(value,static item=>new(item)).CapacityDisplay,
                nameof(MountSnapshot.RemoteDetectionDisplay) => MountSnapshotViews.GetValue(value,static item=>new(item)).RemoteDetectionDisplay,
                nameof(MountSnapshot.SqsDisplay) => MountSnapshotViews.GetValue(value,static item=>new(item)).SqsDisplay,
                nameof(MountSnapshot.AutoUploadDisplay) => MountSnapshotViews.GetValue(value,static item=>new(item)).AutoUploadDisplay,
                nameof(MountSnapshot.ThumbnailDisplay) => MountSnapshotViews.GetValue(value,static item=>new(item)).ThumbnailDisplay,
                nameof(MountSnapshot.SourceDisplay) => MountSnapshotViews.GetValue(value,static item=>new(item)).SourceDisplay,
                _ => throw Unknown()
            },
            MountItemResult value => member switch
            {
                nameof(MountItemResult.DisplayStatus) => MountItemResultViews.GetValue(value,static item=>new(item)).DisplayStatus,
                _ => throw Unknown()
            },
            MountOperationSnapshot value => member switch
            {
                nameof(MountOperationSnapshot.CardOperation) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).CardOperation,
                nameof(MountOperationSnapshot.CardPath) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).CardPath,
                nameof(MountOperationSnapshot.CardSchedule) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).CardSchedule,
                nameof(MountOperationSnapshot.CardState) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).CardState,
                nameof(MountOperationSnapshot.SchedulingText) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).SchedulingText,
                nameof(MountOperationSnapshot.DisplayTarget) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).DisplayTarget,
                nameof(MountOperationSnapshot.ProgressText) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).ProgressText,
                nameof(MountOperationSnapshot.ProgressDetail) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).ProgressDetail,
                nameof(MountOperationSnapshot.DisplayOperation) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).DisplayOperation,
                nameof(MountOperationSnapshot.IssueDetails) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).IssueDetails,
                nameof(MountOperationSnapshot.DisplayIssueReason) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).DisplayIssueReason,
                nameof(MountOperationSnapshot.DisplayWaitDetails) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).DisplayWaitDetails,
                nameof(MountOperationSnapshot.IssueTimeText) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).IssueTimeText,
                nameof(MountOperationSnapshot.IssueStage) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).IssueStage,
                nameof(MountOperationSnapshot.DisplayState) => MountOperationSnapshotViews.GetValue(value,static item=>new(item)).DisplayState,
                _ => throw Unknown()
            },
            ConnectionDiagnostic value => member switch
            {
                nameof(ConnectionDiagnostic.Display) => ConnectionDiagnosticViews.GetValue(value,static item=>new(item)).Display,
                _ => throw Unknown()
            },
            RemoteCapacitySnapshot value => member switch
            {
                nameof(RemoteCapacitySnapshot.Display) => RemoteCapacitySnapshotViews.GetValue(value,static item=>new(item)).Display,
                _ => throw Unknown()
            },
            ExplorerEntryCandidate value => member switch
            {
                nameof(ExplorerEntryCandidate.Display) => ExplorerEntryCandidateViews.GetValue(value,static item=>new(item)).Display,
                _ => throw Unknown()
            },
            ExplorerEntryResult value => member switch
            {
                nameof(ExplorerEntryResult.Display) => ExplorerEntryResultViews.GetValue(value,static item=>new(item)).Display,
                _ => throw Unknown()
            },
        HybridMount.Models.Mounting.Capabilities.CapabilityProbeResource value when member == nameof(value.Details) => CapabilityProbePresentation.ResourceDetails(value),
        long value when member == "Bytes" => TaskByteFormat.Format(value),
        _ => throw Unknown()
    };
    private static ArgumentException Unknown()=>new("未登记的模型展示成员。");
}
