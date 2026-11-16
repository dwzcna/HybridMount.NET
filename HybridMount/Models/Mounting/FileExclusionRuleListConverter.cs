using System.Text.Json;
using System.Text.Json.Serialization;
namespace HybridMount.Models.Mounting;

/// <summary>当前规则数组的严格边界；不转换旧Enabled，也不忽略未知或重复成员。</summary>
public sealed class FileExclusionRuleListConverter : JsonConverter<IReadOnlyList<FileExclusionRule>>
{
    public override IReadOnlyList<FileExclusionRule>? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if(reader.TokenType==JsonTokenType.Null)return null;
        using var document=JsonDocument.ParseValue(ref reader);
        if(document.RootElement.ValueKind!=JsonValueKind.Array)throw new JsonException("排除规则必须是数组。");
        var strict=new JsonSerializerOptions(options){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
        var rules=new List<FileExclusionRule>();
        foreach(var element in document.RootElement.EnumerateArray())
        {
            if(element.ValueKind!=JsonValueKind.Object)throw new JsonException("排除规则必须是对象。");
            var names=new HashSet<string>(options.PropertyNameCaseInsensitive?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
            foreach(var member in element.EnumerateObject())if(!names.Add(member.Name))throw new JsonException("排除规则包含重复字段。");
            rules.Add(element.Deserialize<FileExclusionRule>(strict)??throw new JsonException("排除规则不能为空。"));
        }
        return Array.AsReadOnly(FileExclusionRules.Validate(rules));
    }
    public override void Write(Utf8JsonWriter writer,IReadOnlyList<FileExclusionRule> value,JsonSerializerOptions options)
    {writer.WriteStartArray();foreach(var rule in value)JsonSerializer.Serialize(writer,rule,options);writer.WriteEndArray();}
}
