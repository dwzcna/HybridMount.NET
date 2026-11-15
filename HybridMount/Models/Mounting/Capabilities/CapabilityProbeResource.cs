using HybridMount.Helpers.Paths;
using HybridMount.Models.Mounting;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace HybridMount.Models.Mounting.Capabilities;

/// <summary>一次显式检测的持久资源；只保存精确路径和公开回执，不保存账号密码、认证头或锁令牌。</summary>
public sealed record CapabilityProbeResource(string Path, string RemotePath, string Kind, string Stage,
    string[] Hashes, string? ETag = null, string[]? VersionIds = null, string[]? UploadIds = null,
    string? Reason = null, DateTimeOffset? LockExpiresUtc = null, string[]? DeleteMarkerIds = null)
{
    /// <summary>现有详情的展示转发；模型不访问资源或界面。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal string Details => HybridMount.Commons.Presentation.ModelPresentation.Render(this, nameof(Details));
}
