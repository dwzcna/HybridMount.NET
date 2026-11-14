using System.Net;

namespace HybridMount.Models.Mounting.WebDAV;

public sealed partial class WebDavFileSource
{
    private readonly ProtocolCapabilityTracker _protocolEvidence = new(FileSourceKind.WebDav);
    public ProtocolCapabilitySnapshot ProtocolCapabilities => _protocolEvidence.Snapshot;
    public event Action? ProtocolCapabilitiesChanged { add => _protocolEvidence.Changed += value; remove => _protocolEvidence.Changed -= value; }

    /// <summary>传输层只采集明确拒绝和声明；响应正文的成功由完成解析的业务入口记录。</summary>
    private void ObserveCapabilityHeaders(HttpRequestMessage request, HttpResponseMessage response)
    {
        var scope = HrefPath(request.RequestUri!.AbsoluteUri, _endpoint);
        var method = request.Method.Method.ToLowerInvariant();
        var id = "dav." + (method == "get" && request.Headers.Range is not null ? "range" : method);
        var status = (int)response.StatusCode;
        if (status is 405 or 501) _protocolEvidence.Record(id, ProtocolCapabilityStatus.Unsupported, scope);
        else if (status is 401 or 403) _protocolEvidence.Record(id, ProtocolCapabilityStatus.Unknown, scope, ProtocolCapabilityReason.Permission);
        else if (request.Headers.Range is not null && status == 200)
            _protocolEvidence.Record("dav.range", ProtocolCapabilityStatus.Unsupported, scope, ProtocolCapabilityReason.RangeIgnored);
        else if (response.IsSuccessStatusCode && method is "put" or "delete" or "move" or "mkcol" or "unlock")
            _protocolEvidence.Record(id, ProtocolCapabilityStatus.Supported, scope);
        if (response.Headers.AcceptRanges.Contains("bytes", StringComparer.OrdinalIgnoreCase))
            _protocolEvidence.Record("dav.range", ProtocolCapabilityStatus.Advertised, scope, ProtocolCapabilityReason.Declared);
    }
}
