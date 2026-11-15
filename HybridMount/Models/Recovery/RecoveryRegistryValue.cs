using HybridMount.Models.Mounting;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Globalization;
using Microsoft.Win32;
namespace HybridMount.Models.Recovery;

internal sealed record RecoveryRegistryValue(string Name,RegistryValueKind Kind,string[] Data);
