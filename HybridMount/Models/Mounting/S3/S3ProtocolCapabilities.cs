using System.Net;
using Amazon.S3.Model;

namespace HybridMount.Models.Mounting.S3;

public sealed partial class S3FileSource
{
    private readonly ProtocolCapabilityTracker _protocolEvidence = new(FileSourceKind.S3);
    internal void ClearQueueCapabilities() => _protocolEvidence.ClearPrefix("sqs.");
    internal void RecordQueueCapability(string id) => _protocolEvidence.Record(id, ProtocolCapabilityStatus.Supported, "@queue");
    private string _capabilityEndpointPath = "";
    private bool _capabilityPathStyle;
    public ProtocolCapabilitySnapshot ProtocolCapabilities => _protocolEvidence.Snapshot;
    public event Action? ProtocolCapabilitiesChanged { add => _protocolEvidence.Changed += value; remove => _protocolEvidence.Changed -= value; }

    /// <summary>从SDK实际请求中只提取接口种类和配置根内的相对路径；不保存URL、签名、正文或查询凭据。</summary>
    private void ObserveCapabilityRequest(HttpRequestMessage request, HttpResponseMessage response)
    {
        if (Hints.Value is not { } hints || request.RequestUri is not { } address) return;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (!fields.TryAdd(Uri.UnescapeDataString(parts[0]), parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "")) return;
        }
        var method = request.Method.Method;
        var id = method switch
        {
            "GET" when fields.GetValueOrDefault("list-type") == "2" => "s3.list",
            "GET" => request.Headers.Range is null ? "s3.get" : "s3.range",
            "HEAD" => "s3.head",
            "PUT" when request.Headers.Contains("x-amz-copy-source") => fields.ContainsKey("uploadId") ? "s3.copyPart" : "s3.copy",
            "PUT" => fields.ContainsKey("partNumber") ? "s3.part" : "s3.put",
            "POST" => fields.ContainsKey("uploads") ? "s3.initiate" : fields.ContainsKey("uploadId") ? "s3.complete" : null,
            "DELETE" => fields.ContainsKey("uploadId") ? "s3.abort" : "s3.delete",
            _ => null
        };
        if (id is null) return;
        var key = Uri.UnescapeDataString(address.AbsolutePath).TrimStart('/');
        if (_capabilityEndpointPath.Length != 0)
        {
            if (!key.StartsWith(_capabilityEndpointPath, StringComparison.Ordinal)) return;
            key = key[_capabilityEndpointPath.Length..];
        }
        if (_capabilityPathStyle)
        {
            if (key == _bucket) key = "";
            else if (key.StartsWith(_bucket + "/", StringComparison.Ordinal)) key = key[(_bucket.Length + 1)..];
            else return;
        }
        if (id == "s3.list") key = fields.GetValueOrDefault("prefix") ?? "";
        if (key == _prefix.TrimEnd('/')) key = "";
        else if (_prefix.Length > 0)
        {
            if (!key.StartsWith(_prefix, StringComparison.Ordinal)) return;
            key = key[_prefix.Length..];
        }
        hints.CapabilityId = id; hints.CapabilityScope = key;
        if ((int)response.StatusCode is 405 or 501) _protocolEvidence.Record(id, ProtocolCapabilityStatus.Unsupported, key);
        else if ((int)response.StatusCode is 401 or 403) _protocolEvidence.Record(id, ProtocolCapabilityStatus.Unknown, key, ProtocolCapabilityReason.Permission);
        else if (id == "s3.range" && response.StatusCode == HttpStatusCode.OK)
            _protocolEvidence.Record(id, ProtocolCapabilityStatus.Unsupported, key, ProtocolCapabilityReason.RangeIgnored);
    }

    /// <summary>SDK已解析的响应需具备协议必要字段；正文读取由消费结束入口记录，不以收到响应头判成功。</summary>
    private void ObserveCapabilityResult(object result, ResponseHints hints)
    {
        var valid = result switch
        {
            ListObjectsV2Response page => page.IsTruncated is not null,
            GetObjectMetadataResponse head => head.ContentLength >= 0 && head.LastModified is not null && !string.IsNullOrEmpty(head.ETag),
            PutObjectResponse put => !string.IsNullOrEmpty(put.ETag),
            CopyObjectResponse copy => !string.IsNullOrEmpty(copy.ETag),
            InitiateMultipartUploadResponse start => !string.IsNullOrEmpty(start.UploadId),
            UploadPartResponse part => !string.IsNullOrEmpty(part.ETag),
            CopyPartResponse part => !string.IsNullOrEmpty(part.ETag),
            CompleteMultipartUploadResponse complete => !string.IsNullOrEmpty(complete.ETag),
            DeleteObjectResponse deleted => (int)deleted.HttpStatusCode is >= 200 and < 300,
            AbortMultipartUploadResponse aborted => (int)aborted.HttpStatusCode is >= 200 and < 300,
            _ => false
        };
        if (valid && hints.CapabilityId is { } id) _protocolEvidence.Record(id, ProtocolCapabilityStatus.Supported, hints.CapabilityScope);
    }
}
