using HybridMount.Models.Mounting;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace HybridMount.Models.Runtime;

/// <summary>内部文件快照保留现有领域戳及CFAPI状态，不包含进程间句柄。</summary>
internal sealed record NativeFileSnapshot(LocalFileStamp Stamp,uint Attributes,uint Tag,uint State,NativePlaceholder? Placeholder,bool Full);
