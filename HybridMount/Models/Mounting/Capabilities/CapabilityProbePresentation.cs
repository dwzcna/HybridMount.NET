namespace HybridMount.Models.Mounting;

/// <summary>清理详情可复制；没有回执的资源明确要求核对，不能误报已安全清理。</summary>
internal static class CapabilityProbePresentation
{
    internal static string ResourceDetails(CapabilityProbeResource item) => string.Join("\n", new[]
    {
        global::HybridMount.Services.Localization.UiText.Text(item.Stage == "Owned" ? "CapabilityProbeManualOwned" : "CapabilityProbeManualUnknown"),
        item.Kind, item.RemotePath, item.Reason,
        item.VersionIds is { Length: > 0 } ? "VersionId: " + string.Join(", ", item.VersionIds) : null,
        item.UploadIds is { Length: > 0 } ? "UploadId: " + string.Join(", ", item.UploadIds) : null,
        item.DeleteMarkerIds is { Length: > 0 } ? "DeleteMarker VersionId: " + string.Join(", ", item.DeleteMarkerIds) : null,
        item.LockExpiresUtc is {} expiry ? "Lock expires: " + expiry.ToString("O") : null
    }.Where(value => !string.IsNullOrEmpty(value)));
}
