using HybridMount.Models.Mounting;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace HybridMount.Models.Runtime;

internal sealed record NativeRuntimeEvent(string MountId,string SessionId,string Event,JsonElement Data);
