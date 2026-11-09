using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace HybridMount.Services.Mounting;

/// <summary>内部原生协议与公共IPC分开；long始终写十进制字符串，不经过JSON浮点。</summary>
internal static class NativeRuntimeProtocol
{
    internal const int Version=1,BlockBytes=256*1024;
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=16,Converters={new LongConverter()}};
    internal static JsonElement Element(object value)=>JsonSerializer.SerializeToElement(value,Json);
    internal static T Read<T>(JsonElement value)=>value.Deserialize<T>(Json)??throw new IOException("原生响应为空。");
    private sealed class LongConverter:JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>reader.TokenType==JsonTokenType.String?long.Parse(reader.GetString()!,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture):throw new JsonException("原生64位字段必须为字符串。");
        public override void Write(Utf8JsonWriter writer,long value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
/// <summary>内部文件快照保留现有领域戳及CFAPI状态，不包含进程间句柄。</summary>
internal sealed record NativeFileSnapshot(LocalFileStamp Stamp,uint Attributes,uint Tag,uint State,NativePlaceholder? Placeholder,bool Full);
internal sealed record NativePlaceholder(long OnDiskSize,long ModifiedSize,long FileId,long RootFileId,uint PinState,uint InSyncState,byte[] Identity,long ValidatedSize=0,long PropertiesSize=0);
internal sealed record NativeRuntimeEvent(string MountId,string SessionId,string Event,JsonElement Data);

/// <summary>Windows接入层的进程边界；当前及后续方法均返回模型和回执，不借出原生指针。</summary>
internal interface ICloudFilesRuntime
{
    /// <summary>只执行原生端固定允许的完整领域操作；检查点保存意图，失败返回原代码，不携带裸句柄。</summary>
    Task<JsonElement> InvokeAsync(string operation,object data,Func<string,JsonElement,CancellationToken,Task<object?>>? checkpoint=null,CancellationToken token=default);
    Task<NativeFileSnapshot> InspectAsync(string path,bool persisted=false,bool ranges=false,string? expectedIdentity=null,CancellationToken token=default);
    Task<JsonElement> RootInfoAsync(string path,CancellationToken token=default);
}
