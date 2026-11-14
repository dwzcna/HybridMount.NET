using System.Text.Json;

namespace HybridMount.Models.Mounting.Synology;

public sealed partial class SynologyFileSource
{
    private readonly ProtocolCapabilityTracker _protocolEvidence = new(FileSourceKind.Synology);
    public ProtocolCapabilitySnapshot ProtocolCapabilities => _protocolEvidence.Snapshot;
    public event Action? ProtocolCapabilitiesChanged { add => _protocolEvidence.Changed += value; remove => _protocolEvidence.Changed -= value; }
    private static string? CapabilityId(string api) => api switch
    {
        "SYNO.API.Info" => "syno.info", "SYNO.API.Auth" => "syno.auth", "SYNO.FileStation.List" => "syno.list",
        "SYNO.FileStation.Download" => "syno.download", "SYNO.FileStation.MD5" => "syno.md5", "SYNO.FileStation.Upload" => "syno.upload",
        "SYNO.FileStation.CreateFolder" => "syno.folder", "SYNO.FileStation.Delete" => "syno.cleanup", _ => null
    };
    private string CapabilityScope(Dictionary<string, string> fields)
    {
        foreach (var key in new[] { "path", "file_path", "folder_path" })
        {
            if (!fields.TryGetValue(key, out var path)) continue;
            try
            {
                if (path.StartsWith('[')) { using var document = JsonDocument.Parse(path); path = document.RootElement[0].GetString() ?? ""; }
                if (path == _root) return "";
                if (path.StartsWith(_root + "/", StringComparison.Ordinal)) return path[(_root.Length + 1)..];
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or IndexOutOfRangeException) { }
        }
        return "@server";
    }
    /// <summary>只观察已有的有效JSON回执；会话和共享发现明确标为服务端范围，不冒充所选目录已获验证。</summary>
    private void ObserveCapabilityJson(Dictionary<string, string> fields, JsonElement data)
    {
        var api = fields.GetValueOrDefault("api") ?? "";
        var id = CapabilityId(api);
        if (id is null || id == "syno.info") return;
        if (id == "syno.auth" && (!data.TryGetProperty("sid", out var sid) || sid.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(sid.GetString()))) return;
        if (id == "syno.list" && !((data.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array &&
            (fields.GetValueOrDefault("method") != "getinfo" || files.GetArrayLength() > 0)) ||
            (data.TryGetProperty("shares", out var shares) && shares.ValueKind == JsonValueKind.Array))) return;
        _protocolEvidence.Record(id, ProtocolCapabilityStatus.Supported, CapabilityScope(fields));
    }
}
