using HybridMount.Models.Mounting;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace HybridMount.Infrastructure.Native;

/// <summary>内部原生协议与公共IPC分开；long始终写十进制字符串，不经过JSON浮点。</summary>
internal static class NativeRuntimeProtocol
{
    internal const int Version=2,BlockBytes=256*1024;
    internal static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,MaxDepth=16,Converters={new LongConverter(),new GuidConverter()}};
    internal static JsonElement Element(object value)=>JsonSerializer.SerializeToElement(value,Json);
    internal static T Read<T>(JsonElement value)=>value.Deserialize<T>(Json)??throw new IOException("原生响应为空。");
    /// <summary>私有原生边界兼容Windows GUID的N／D格式，不改变公共JSON或备份。</summary>
    private sealed class GuidConverter:JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
        {
            var value=reader.GetString();return Guid.TryParseExact(value,"N",out var result)||Guid.TryParseExact(value,"D",out result)?result:throw new JsonException("原生GUID无效。");
        }
        public override void Write(Utf8JsonWriter writer,Guid value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString("D"));
    }
    private sealed class LongConverter:JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>reader.TokenType==JsonTokenType.String?long.Parse(reader.GetString()!,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture):throw new JsonException("原生64位字段必须为字符串。");
        public override void Write(Utf8JsonWriter writer,long value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
