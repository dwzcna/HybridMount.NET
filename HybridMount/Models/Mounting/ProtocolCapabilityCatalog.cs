namespace HybridMount.Models.Mounting;

/// <summary>当前产品实际调用的接口及用途资源键；不按协议名称推定服务器能力。</summary>
public sealed record ProtocolCapabilityDescriptor(string Id, string Name, string DescriptionKey);
public static class ProtocolCapabilityCatalog
{
    private static ProtocolCapabilityDescriptor Row(string id, string name, string purpose) => new(id, name, "ProtocolPurpose" + purpose);
    private static readonly IReadOnlyList<ProtocolCapabilityDescriptor> Dav = Array.AsReadOnly(new[] {
        Row("dav.options", "RFC 9110 (OPTIONS)", "Discovery"), Row("dav.get", "RFC 9110 (GET)", "Download"),
        Row("dav.range", "RFC 9110 (GET / Range)", "Range"), Row("dav.propfind", "RFC 4918 (PROPFIND)", "Metadata"),
        Row("dav.quota", "RFC 4331 (PROPFIND / quota-available-bytes, quota-used-bytes)", "Quota"),
        Row("dav.report", "RFC 6578 (REPORT / sync-collection)", "Changes"),
        Row("dav.put", "RFC 9110 (PUT)", "Upload"), Row("dav.create", "RFC 9110 (PUT / If-None-Match)", "CreateOnly"),
        Row("dav.update", "RFC 9110 (PUT / If-Match)", "Update"), Row("dav.mkcol", "RFC 4918 (MKCOL)", "Directory"),
        Row("dav.delete", "RFC 4918 (DELETE)", "Delete"), Row("dav.conditionalDelete", "RFC 9110 (DELETE / If-Match)", "ConditionalDelete"),
        Row("dav.move", "RFC 4918 (MOVE)", "Move"), Row("dav.conditionalMove", "RFC 4918 (MOVE / If-Match, Overwrite)", "ConditionalMove"),
        Row("dav.lock", "RFC 4918 (LOCK)", "Lock"), Row("dav.unlock", "RFC 4918 (UNLOCK)", "Unlock") });
    private static readonly IReadOnlyList<ProtocolCapabilityDescriptor> S3 = Array.AsReadOnly(new[] {
        Row("s3.list", "S3 ListObjectsV2", "Metadata"), Row("s3.head", "S3 HeadObject", "Metadata"),
        Row("s3.get", "S3 GetObject", "Download"), Row("s3.range", "S3 GetObject / Range (RFC 9110)", "Range"),
        Row("s3.put", "S3 PutObject", "Upload"), Row("s3.create", "S3 PutObject / If-None-Match", "CreateOnly"),
        Row("s3.update", "S3 PutObject / If-Match", "Update"), Row("s3.delete", "S3 DeleteObject", "Delete"),
        Row("s3.conditionalDelete", "S3 DeleteObject / If-Match", "ConditionalDelete"), Row("s3.copy", "S3 CopyObject", "Move"),
        Row("s3.conditionalMove", "S3 CopyObject / source and destination conditions", "ConditionalMove"),
        Row("s3.initiate", "S3 CreateMultipartUpload", "MultipartStart"), Row("s3.part", "S3 UploadPart", "MultipartPart"),
        Row("s3.copyPart", "S3 UploadPartCopy", "MultipartCopy"), Row("s3.complete", "S3 CompleteMultipartUpload", "MultipartComplete"),
        Row("s3.conditionalComplete", "S3 CompleteMultipartUpload / If-Match, If-None-Match", "Update"), Row("s3.abort", "S3 AbortMultipartUpload", "MultipartAbort") });
    private static readonly IReadOnlyList<ProtocolCapabilityDescriptor> Sqs = Array.AsReadOnly(new[] {
        Row("sqs.attributes", "SQS GetQueueAttributes", "QueueAttributes"), Row("sqs.receive", "SQS ReceiveMessage", "QueueReceive"), Row("sqs.ack", "SQS DeleteMessage", "QueueConfirm") });
    private static readonly IReadOnlyList<ProtocolCapabilityDescriptor> Synology = Array.AsReadOnly(new[] {
        Row("syno.info", "SYNO.API.Info / query", "Discovery"), Row("syno.auth", "SYNO.API.Auth / login, logout", "Authentication"),
        Row("syno.list", "SYNO.FileStation.List / list, getinfo, list_share", "Metadata"), Row("syno.download", "SYNO.FileStation.Download", "Download"),
        Row("syno.md5", "SYNO.FileStation.MD5 / start, status, stop", "Checksum"), Row("syno.upload", "SYNO.FileStation.Upload", "Upload"),
        Row("syno.create", "SYNO.FileStation.Upload / overwrite=false", "CreateOnly"), Row("syno.folder", "SYNO.FileStation.CreateFolder", "Directory"),
        Row("syno.cleanup", "SYNO.FileStation.Delete / start, status", "Cleanup") });
    /// <summary>返回不可修改清单；SQS只在用户配置该功能时显示。</summary>
    public static IReadOnlyList<ProtocolCapabilityDescriptor> For(FileSourceKind kind, bool sqs = false) => kind switch
    {
        FileSourceKind.WebDav => Dav,
        FileSourceKind.S3 => sqs ? Array.AsReadOnly(S3.Concat(Sqs).ToArray()) : S3,
        FileSourceKind.Synology => Synology,
        _ => Array.Empty<ProtocolCapabilityDescriptor>()
    };
}
