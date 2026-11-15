namespace HybridMount.Models.UI;

/// <summary>协议表单的纯内存业务投影；模态选择器占用有独立所有者，不参与视觉禁用。</summary>
public sealed record ProtocolInteractionState(
    bool Stopped = false, bool Existing = false, bool SavedBusy = false, bool Submitting = false,
    bool Testing = false, bool TestCanceling = false, bool Fetching = false, bool Probing = false,
    bool SqsTesting = false, bool SqsCanceling = false, bool SqsSaving = false,
    bool CapacityAutomatic = false, bool CapacityAvailable = false, bool RootEditable = true,
    bool Https = false, bool HasTrustedCertificate = false, bool HasCandidate = false,
    bool ThumbnailsAvailable = true, bool SeparateSqsKeys = false, bool UseDefaultCredentials = false)
{
    private bool ServerWork => Testing || Fetching || Probing;
    private bool QueueWork => SqsTesting || SqsSaving;
    public bool CanEditDraft => !Stopped && !Submitting;
    public bool CanEditConnection => CanEditDraft && !SavedBusy && !ServerWork && !QueueWork;
    public bool CanEditSettings => CanEditDraft;
    public bool CanEditSqs => CanEditDraft && !ServerWork && !QueueWork;
    public bool CanEditRootOptions => CanEditConnection && RootEditable;
    public bool CanEditCapacityValue => CanEditSettings && !CapacityAutomatic;
    public bool CanToggleSettings => Existing ? CanChangeSettings : CanEditDraft;
    public bool CanEditThumbnails => CanToggleSettings && ThumbnailsAvailable;
    public bool CanEditS3Secrets => CanEditConnection && !UseDefaultCredentials;
    public bool CanEditSqsSecrets => CanEditSqs && SeparateSqsKeys;
    public bool CanSubmit => CanEditDraft && !SavedBusy && !ServerWork && !QueueWork;
    public bool CanTest => !Stopped && (Testing ? !TestCanceling : !Submitting && !QueueWork && !Fetching && !Probing && !SavedBusy);
    public bool CanChooseFolder => CanEditDraft && !Existing;
    public bool CanChangeSettings => CanEditSettings && !SavedBusy && !ServerWork && !QueueWork;
}
