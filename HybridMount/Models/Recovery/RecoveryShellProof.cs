using HybridMount.Models.Mounting;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Globalization;
namespace HybridMount.Models.Recovery;

internal sealed record RecoveryShellProof(RecoveryRegistryBackup[] Roots,RecoveryRegistryBackup[] Namespace,string? NamespaceId,
    RecoveryHiddenValue[]? Hidden=null,RecoveryShellProof? External=null);
