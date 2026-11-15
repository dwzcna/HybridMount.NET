using HybridMount.Helpers.Paths;
using HybridMount.Models.Mounting;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace HybridMount.Models.Mounting.Capabilities;

/// <summary>可在关闭和重启后读取的检测清理记录；Scope 只是不含秘密的账号及服务器摘要。</summary>
public sealed record CapabilityProbeRun(Guid RunId, FileSourceKind Kind, string Scope, string Target,
    DateTimeOffset StartedUtc, string Stage, CapabilityProbeCase[] Cases, CapabilityProbeResource[] Resources);
