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
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void BackupVersionsPreserveOldEmptyAndNewExplicitRules(int version)
    {
        var item=new MountBackupItem("test","D:/test",new(FileSourceKind.WebDav,"https://example.test/dav/"),new("u","p"),ExclusionRules:version==5?FileExclusionRules.Defaults():null);
        var encoded=MountBackupCodec.Encode(new(version,DateTimeOffset.UtcNow,[item]),"long-test-password");
        var restored=MountBackupCodec.Decode(encoded,"long-test-password").Mounts[0];
        Assert.Equal(item,restored);Assert.Equal(item.ExclusionRules?.ToArray(),restored.ExclusionRules?.ToArray());
        if(version<5)Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(new(version,DateTimeOffset.UtcNow,[item with{ExclusionRules=FileExclusionRules.Defaults()}]),"long-test-password"));
    }
}
