using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>验证字面文件名规则、暂停边界及旧配置／备份语义，不访问正式挂载或服务。</summary>
public sealed class FileExclusionRulesTests
{
    [Theory]
    [InlineData("sub/~report.xlsx", true)] [InlineData("sub/a$b.txt", true)]
    [InlineData("sub/a.BAK", true)] [InlineData("sub/-草稿.txt", true)]
    [InlineData("-folder/a.txt", false)] [InlineData("~folder/a.txt", false)]
    [InlineData("a.bak.txt", false)] [InlineData("a-b.txt", false)]
    public void DefaultsMatchOnlyBasename(string path,bool expected)
    {Assert.Equal(expected,FileExclusionRules.Matches(FileExclusionRules.Defaults(),path));Assert.False(FileExclusionRules.Matches(FileExclusionRules.Defaults(),path,true));}

    [Fact]
    public void EmptyAndLiteralRulesKeepTheirMeaning()
    {
        Assert.False(FileExclusionRules.Matches(null,"~a"));
        Assert.False(FileExclusionRules.Matches([new(FileExclusionMatchKind.Contains,"*.bak")],"a.bak"));
        Assert.Equal(4,FileExclusionRules.Validate(FileExclusionRules.Defaults()).Length);
    }
    [Theory] [InlineData("")] [InlineData(" ")] [InlineData("a/b")] [InlineData("a\\b")]
    public void InvalidTextIsRejected(string text)=>Assert.Throws<ArgumentException>(()=>FileExclusionRules.Validate([new(FileExclusionMatchKind.Contains,text)]));
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void LegacyDisabledRulesAreFilteredAtConfigurationAndBackupBoundaries(bool backup)
    {
        var jsonRules="""[{"Match":2,"Text":".bak","Enabled":false},{"Match":0,"Text":"~","Enabled":true}]""";
        var definition=new MountDefinition(Guid.NewGuid(),"test","D:/test");
        var item=new MountBackupItem("test","D:/test",new(FileSourceKind.WebDav,"https://example.test/dav/"),new("u","p"));
        var node=System.Text.Json.Nodes.JsonNode.Parse(backup?JsonSerializer.Serialize(item):JsonSerializer.Serialize(definition))!;
        node["ExclusionRules"]=System.Text.Json.Nodes.JsonNode.Parse(jsonRules);
        IReadOnlyList<FileExclusionRule>? rules=backup?JsonSerializer.Deserialize<MountBackupItem>(node.ToJsonString())!.ExclusionRules:JsonSerializer.Deserialize<MountDefinition>(node.ToJsonString())!.ExclusionRules;
        Assert.Single(rules!);Assert.False(FileExclusionRules.Matches(rules,"file.bak"));Assert.True(FileExclusionRules.Matches(rules,"~file"));
        Assert.DoesNotContain("\"Enabled\"",JsonSerializer.Serialize(rules));
    }
    [Theory]
    [InlineData(false,"enabled")][InlineData(true,"enabled")][InlineData(false,"Enabled")][InlineData(true,"Enabled")]
    public void LegacyEnabledIsConsumedBeforeStrictCamelCaseModelValidation(bool backup,string legacyName)
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};
        var definition=new MountDefinition(Guid.NewGuid(),"test","D:/test");var item=new MountBackupItem("test","D:/test",new(FileSourceKind.WebDav,"https://example.test/dav/"),new("u","p"));
        var node=System.Text.Json.Nodes.JsonNode.Parse(backup?JsonSerializer.Serialize(item,options):JsonSerializer.Serialize(definition,options))!;
        node["exclusionRules"]=System.Text.Json.Nodes.JsonNode.Parse("[{\"match\":2,\"text\":\".bak\",\""+legacyName+"\":false},{\"match\":0,\"text\":\"~\",\""+legacyName+"\":true}]");
        var rules=backup?JsonSerializer.Deserialize<MountBackupItem>(node.ToJsonString(),options)!.ExclusionRules:JsonSerializer.Deserialize<MountDefinition>(node.ToJsonString(),options)!.ExclusionRules;
        Assert.Single(rules!);Assert.False(FileExclusionRules.Matches(rules,"file.bak"));Assert.True(FileExclusionRules.Matches(rules,"~file"));
        Assert.DoesNotContain("enabled",JsonSerializer.Serialize(rules,options),StringComparison.OrdinalIgnoreCase);
    }
    [Theory]
    [InlineData("{\"match\":2,\"text\":\".bak\",\"enabled\":1}")]
    [InlineData("{\"match\":2,\"text\":\".bak\",\"enabled\":null}")]
    [InlineData("{\"match\":2,\"text\":\".bak\",\"enabled\":false,\"unexpected\":true}")]
    [InlineData("{\"match\":2,\"text\":\".bak\",\"enabled\":false,\"Enabled\":true}")]
    public void LegacyCompatibilityStillRejectsInvalidAndUnknownFields(string rule)
    {
        var options=new JsonSerializerOptions(JsonSerializerDefaults.Web){UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow};options.Converters.Add(new FileExclusionRuleListConverter());
        Assert.Throws<JsonException>(()=>JsonSerializer.Deserialize<IReadOnlyList<FileExclusionRule>>("["+rule+"]",options));
    }
    [Fact]
    public void DuplicateRulesAreCaseInsensitive()=>Assert.Throws<ArgumentException>(()=>FileExclusionRules.Validate([new(FileExclusionMatchKind.Suffix,".bak"),new(FileExclusionMatchKind.Suffix,".BAK")]));
    [Theory]
    [InlineData("Queued",true)] [InlineData("Prepared",true)] [InlineData("WaitingCapacity",true)]
    [InlineData("Sending",false)] [InlineData("RemoteCommitted",false)] [InlineData("OutcomeUnknown",false)]
    [InlineData("Failed",false)] [InlineData("Conflict",false)] [InlineData("Canceled",false)]
    public void PausePreservesOriginalStageAndSafety(string state,bool pauses)
    {
        var item=new LocalUpload("id",Guid.NewGuid(),"~a","~a",false,"stamp",2,state,DateTimeOffset.UtcNow,Error:"原原因");
        var paused=FileExclusionRules.Pause(item);Assert.Equal(pauses?"Excluded":state,paused.State);
        Assert.Equal(item,FileExclusionRules.Resume(paused));
    }
    [Fact]
    public void LegacyDefinitionHasNoImplicitDefaults()
    {
        var definition=JsonSerializer.Deserialize<MountDefinition>("{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Name\":\"旧挂载\",\"LocalPath\":\"D:\\\\test\"}")!;
        Assert.Null(definition.ExclusionRules);Assert.False(FileExclusionRules.Matches(definition.ExclusionRules,"~a"));
    }
    [Fact]
    public void ConfigurationRulesAreImmutableValuesAcrossJsonAndWithClones()
    {
        var array=FileExclusionRules.Defaults();var definition=new MountDefinition(Guid.NewGuid(),"test","D:/test",ExclusionRules:array);
        array[0]=new(FileExclusionMatchKind.Prefix,"changed");Assert.Equal("~",definition.ExclusionRules![0].Text);
        var restored=JsonSerializer.Deserialize<MountDefinition>(JsonSerializer.Serialize(definition))!;
        Assert.Equal(definition,restored);Assert.Equal(definition.GetHashCode(),restored.GetHashCode());
        Assert.Equal(definition,definition with{ExclusionRules=FileExclusionRules.Defaults()});
        Assert.NotEqual(definition,definition with{ExclusionRules=[]});
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void HmxbkPreservesEmptyAndExplicitRules(bool hasRules)
    {
        var item=new MountBackupItem("test","D:/test",new(FileSourceKind.WebDav,"https://example.test/dav/"),new("u","p"),ExclusionRules:hasRules?FileExclusionRules.Defaults():null);
        var encoded=MountBackupCodec.Encode(new(1,DateTimeOffset.UtcNow,[item]),"long-test-password");
        var restored=MountBackupCodec.Decode(encoded,"long-test-password").Mounts[0];
        Assert.Equal(item,restored);Assert.Equal(item.ExclusionRules?.ToArray(),restored.ExclusionRules?.ToArray());
    }
}
