using HybridMount.Models.Mounting;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Globalization;
namespace HybridMount.Models.Recovery;

internal sealed record RecoveryHiddenValue(string Path,string Owner,RecoveryRegistryValue Value);
