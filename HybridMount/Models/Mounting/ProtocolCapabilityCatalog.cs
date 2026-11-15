namespace HybridMount.Models.Mounting;

/// <summary>当前产品实际调用的接口及用途资源键；不按协议名称推定服务器能力。</summary>
public sealed record ProtocolCapabilityDescriptor(string Id, string Name, string DescriptionKey);
public static class ProtocolCapabilityCatalog
{
    private static ProtocolCapabilityDescriptor Row(string id, string name, string purpose) => new(id, name, purpose);
    private static readonly IReadOnlyList<ProtocolCapabilityDescriptor> Dav = Array.AsReadOnly(new[] {
        Row("dav.options", "RFC 9110 (OPTIONS)", "ProtocolPurposeDiscovery"), Row("dav.get", "RFC 9110 (GET)", "ProtocolPurposeDownload"),
        Row("dav.redirect", "RFC 9110 (301 / 308, same-origin)", "ProtocolPurposeRedirect"),
        Row("dav.gzip", "RFC 9110 (PROPFIND / REPORT, gzip)", "ProtocolPurposeMetadataCompression"),
        Row("dav.privileges", "RFC 3744 (PROPFIND / current-user-privilege-set)", "ProtocolPurposeAccess"),
        Row("dav.head", "RFC 9110 (HEAD)", "ProtocolPurposeMetadata"), Row("dav.digest", "RFC 9530 (Repr-Digest / Want-Repr-Digest)", "ProtocolPurposeRepresentationDigest"),
        Row("dav.range", "RFC 9110 (GET / Range)", "ProtocolPurposeRange"), Row("dav.propfind", "RFC 4918 (PROPFIND)", "ProtocolPurposeMetadata"),
        Row("dav.quota", "RFC 4331 (PROPFIND / quota-available-bytes, quota-used-bytes)", "ProtocolPurposeQuota"),
        Row("dav.report", "RFC 6578 (REPORT / sync-collection)", "ProtocolPurposeChanges"),
        Row("dav.put", "RFC 9110 (PUT)", "ProtocolPurposeUpload"), Row("dav.create", "RFC 9110 (PUT / If-None-Match)", "ProtocolPurposeCreateOnly"),
        Row("dav.update", "RFC 9110 (PUT / If-Match)", "ProtocolPurposeUpdate"), Row("dav.mkcol", "RFC 4918 (MKCOL)", "ProtocolPurposeDirectory"),
        Row("dav.delete", "RFC 4918 (DELETE)", "ProtocolPurposeDelete"), Row("dav.conditionalDelete", "RFC 9110 (DELETE / If-Match)", "ProtocolPurposeConditionalDelete"),
        Row("dav.move", "RFC 4918 (MOVE)", "ProtocolPurposeMove"), Row("dav.conditionalMove", "RFC 4918 (MOVE / If-Match, Overwrite)", "ProtocolPurposeConditionalMove"),
        Row("dav.lock", "RFC 4918 (LOCK)", "ProtocolPurposeLock"), Row("dav.unlock", "RFC 4918 (UNLOCK)", "ProtocolPurposeUnlock") });
    private static readonly IReadOnlyList<ProtocolCapabilityDescriptor> S3 = Array.AsReadOnly(new[] {
        Row("s3.list", "S3 ListObjectsV2", "ProtocolPurposeMetadata"), Row("s3.versions", "S3 ListObjectVersions", "ProtocolPurposeProbeInventory"), Row("s3.parts", "S3 ListParts", "ProtocolPurposeProbeInventory"), Row("s3.head", "S3 HeadObject", "ProtocolPurposeMetadata"),
        Row("s3.get", "S3 GetObject", "ProtocolPurposeDownload"), Row("s3.range", "S3 GetObject / Range (RFC 9110)", "ProtocolPurposeRange"),
        Row("s3.put", "S3 PutObject", "ProtocolPurposeUpload"), Row("s3.create", "S3 PutObject / If-None-Match", "ProtocolPurposeCreateOnly"),
        Row("s3.update", "S3 PutObject / If-Match", "ProtocolPurposeUpdate"), Row("s3.delete", "S3 DeleteObject", "ProtocolPurposeDelete"),
        Row("s3.conditionalDelete", "S3 DeleteObject / If-Match", "ProtocolPurposeConditionalDelete"), Row("s3.copy", "S3 CopyObject", "ProtocolPurposeMove"),
        Row("s3.conditionalMove", "S3 CopyObject / source and destination conditions", "ProtocolPurposeConditionalMove"),
        Row("s3.initiate", "S3 CreateMultipartUpload", "ProtocolPurposeMultipartStart"), Row("s3.part", "S3 UploadPart", "ProtocolPurposeMultipartPart"),
        Row("s3.copyPart", "S3 UploadPartCopy", "ProtocolPurposeMultipartCopy"), Row("s3.complete", "S3 CompleteMultipartUpload", "ProtocolPurposeMultipartComplete"),
        Row("s3.conditionalComplete", "S3 CompleteMultipartUpload / If-Match, If-None-Match", "ProtocolPurposeUpdate"), Row("s3.abort", "S3 AbortMultipartUpload", "ProtocolPurposeMultipartAbort") });
    private static readonly IReadOnlyList<ProtocolCapabilityDescriptor> Sqs = Array.AsReadOnly(new[] {
        Row("sqs.attributes", "SQS GetQueueAttributes", "ProtocolPurposeQueueAttributes"), Row("sqs.receive", "SQS ReceiveMessage", "ProtocolPurposeQueueReceive"), Row("sqs.ack", "SQS DeleteMessage", "ProtocolPurposeQueueConfirm") });
    private static readonly IReadOnlyList<ProtocolCapabilityDescriptor> Synology = Array.AsReadOnly(new[] {
        Row("syno.info", "SYNO.API.Info / query", "ProtocolPurposeDiscovery"), Row("syno.auth", "SYNO.API.Auth / login, logout", "ProtocolPurposeAuthentication"),
        Row("syno.list", "SYNO.FileStation.List / list, getinfo, list_share", "ProtocolPurposeMetadata"), Row("syno.download", "SYNO.FileStation.Download", "ProtocolPurposeDownload"),
        Row("syno.md5", "SYNO.FileStation.MD5 / start, status, stop", "ProtocolPurposeChecksum"), Row("syno.upload", "SYNO.FileStation.Upload", "ProtocolPurposeUpload"),
        Row("syno.create", "SYNO.FileStation.Upload / overwrite=false", "ProtocolPurposeCreateOnly"), Row("syno.folder", "SYNO.FileStation.CreateFolder", "ProtocolPurposeDirectory"),
        Row("syno.cleanup", "SYNO.FileStation.Delete / start, status", "ProtocolPurposeCleanup") });
    /// <summary>返回不可修改清单；SQS只在用户配置该功能时显示。</summary>
    public static IReadOnlyList<ProtocolCapabilityDescriptor> For(FileSourceKind kind, bool sqs = false) => kind switch
    {
        FileSourceKind.WebDav => Dav,
        FileSourceKind.S3 => sqs ? Array.AsReadOnly(S3.Concat(Sqs).ToArray()) : S3,
        FileSourceKind.Synology => Synology,
        _ => Array.Empty<ProtocolCapabilityDescriptor>()
    };
}
