using HybridMount.Models.Mounting;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Globalization;
using Microsoft.Win32;
namespace HybridMount.Models.Recovery;

internal sealed record RecoveryRegistryBackup(RegistryHive Hive,string Path,string Owner,RecoveryRegistryTree Tree);
