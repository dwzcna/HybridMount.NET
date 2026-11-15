using HybridMount.Helpers.Paths;
using HybridMount.Models.Mounting;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace HybridMount.Models.Mounting.Capabilities;

/// <summary>检测步骤的实际结果；Skipped 表示缺少前置证据，不能据此宣布整个服务器不支持。</summary>
public sealed record CapabilityProbeCase(string Id, ProtocolCapabilityStatus Status, string Scope, string Reason,
    long Milliseconds, bool Skipped = false, bool PermissionDenied = false);
