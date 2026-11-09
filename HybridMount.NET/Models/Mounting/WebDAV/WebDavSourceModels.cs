using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using HybridMount.Commons;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>协议内按职责拆分的 Models 实现；公开入口与版本字段保持兼容。</summary>
public sealed partial class WebDavFileSource
{
    private sealed record VersionInfo(string? ETag, long Size, long ModifiedTicks, long Observation=0);
    private static VersionInfo Version(SourceEntry entry) => JsonSerializer.Deserialize<VersionInfo>(entry.Version) ?? throw new IOException("DAV 版本标记无效。");
    /// <summary>空值保留为缺失；裸值不加引号、不获得强验证能力。</summary>
    private static string? NormalizeTag(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return EntityTagHeaderValue.TryParse(value, out var parsed) && parsed.Tag != "*" ? parsed.ToString() : value;
    }
    private static bool ValidTag(string? value) => EntityTagHeaderValue.TryParse(NormalizeTag(value), out var parsed) && parsed.Tag != "*";
    private static bool Strong(string? tag) => ValidTag(tag) && EntityTagHeaderValue.TryParse(NormalizeTag(tag), out var parsed) && !parsed.IsWeak;
    /// <summary>兼容旧令牌的外围空白；保留强弱、标签值、大小和时间差异。</summary>
    public bool IsSameVersion(string left, string right)
    {
        if (left == right) return true;
        try
        {
            var a = JsonSerializer.Deserialize<VersionInfo>(left);
            var b = JsonSerializer.Deserialize<VersionInfo>(right);
            return a is not null && b is not null && a.Size == b.Size && a.ModifiedTicks == b.ModifiedTicks && a.Observation==b.Observation &&
                NormalizeTag(a.ETag) == NormalizeTag(b.ETag);
        }
        catch (JsonException) { return false; }
    }

}
