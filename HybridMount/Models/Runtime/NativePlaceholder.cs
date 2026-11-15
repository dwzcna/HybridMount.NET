using HybridMount.Models.Mounting;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace HybridMount.Models.Runtime;

internal sealed record NativePlaceholder(long OnDiskSize,long ModifiedSize,long FileId,long RootFileId,uint PinState,uint InSyncState,byte[] Identity,long ValidatedSize=0,long PropertiesSize=0);
