using System.Text.Json;
using System.Text.Json.Serialization;
namespace HybridMount.Models.Mounting;

/// <summary>仅在配置及备份边界兼容旧Enabled字段；旧停用项过滤，新规则不再携带启停状态。</summary>
public sealed class FileExclusionRuleListConverter : JsonConverter<IReadOnlyList<FileExclusionRule>>
{
    /// <summary>读取并校验旧数组；停用项不自动激活，未知启停类型拒绝加载。</summary>
    public override IReadOnlyList<FileExclusionRule>? Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
    {
        if(reader.TokenType==JsonTokenType.Null)return null;
        using var document=JsonDocument.ParseValue(ref reader);if(document.RootElement.ValueKind!=JsonValueKind.Array)throw new JsonException("排除规则必须是数组。");
        var all=new List<FileExclusionRule>();var active=new List<FileExclusionRule>();
        foreach(var element in document.RootElement.EnumerateArray())
        {
            var rule=element.Deserialize<FileExclusionRule>(options)??throw new JsonException("排除规则不能为空。");all.Add(rule);
            var enabled=true;
            foreach(var member in element.EnumerateObject())
                if((member.Name.Equals("Enabled",options.PropertyNameCaseInsensitive?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||member.Name==options.PropertyNamingPolicy?.ConvertName("Enabled")))
                {if(member.Value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new JsonException("旧排除规则的启停值无效。");enabled=member.Value.GetBoolean();}
            if(enabled)active.Add(rule);
        }
        FileExclusionRules.Validate(all);return Array.AsReadOnly(active.ToArray());
    }
    /// <summary>写入仅包含匹配方式和文本的当前列表；旧版本缺少Enabled时默认启用，保持备份版本兼容。</summary>
    public override void Write(Utf8JsonWriter writer,IReadOnlyList<FileExclusionRule> value,JsonSerializerOptions options)
    {
        writer.WriteStartArray();foreach(var rule in value)JsonSerializer.Serialize(writer,rule,options);writer.WriteEndArray();
    }
}
