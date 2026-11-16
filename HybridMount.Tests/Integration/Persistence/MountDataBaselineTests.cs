using HybridMount.Infrastructure.Persistence;
using HybridMount.Models.Mounting;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using System.Text.Json;

namespace HybridMount.Tests.Integration.Persistence;

/// <summary>只操作独立临时目录及HKCU测试键；拒绝旧数据不创建、升级或覆盖任何现场文件。</summary>
public sealed class MountDataBaselineTests
{
    [Fact]
    public void NewIndexUsesNineAndDisappearingIndexIsNeverRecreated()
    {
        using var scope=new Scope();var repository=new MountRepository(scope.Data,scope.Registry);
        Assert.Equal(9,MountIndexFormat.Inspect(scope.Data).Version);
        var file=Path.Combine(scope.Data,"mounts.db");File.Delete(file);
        Assert.Throws<SqliteException>(()=>repository.LoadOperations());Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("uploads")][InlineData("operation_steps")][InlineData("remote_temporary")]
    public void IncompleteCurrentIndexIsRejectedWithoutCreatingMissingTables(string table)
    {
        using var scope=new Scope();_=new MountRepository(scope.Data,scope.Registry);
        var file=Path.Combine(scope.Data,"mounts.db");
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,Pooling=false}.ToString()))
        {db.Open();using var command=db.CreateCommand();command.CommandText="DROP TABLE "+table;command.ExecuteNonQuery();}
        var before=File.ReadAllBytes(file);
        Assert.Equal("Corrupt",MountIndexFormat.Inspect(scope.Data).State);
        Assert.Throws<MountIndexFormatException>(()=>new MountRepository(scope.Data,scope.Registry));
        Assert.Equal(before,File.ReadAllBytes(file));Assert.False(Directory.Exists(Path.Combine(scope.Data,"Backups")));
    }

    [Theory]
    [InlineData(11,"Old")][InlineData(13,"Newer")]
    public void NoncurrentConfigurationIsRejectedBeforeCreatingAnyIndex(int marker,string state)
    {
        using var scope=new Scope();scope.Seed(marker);var before=scope.Json;
        var store=new RegistryMountDefinitionStore(scope.Registry);
        Assert.Equal(state,store.InspectApplicationData(scope.Data).State);
        Assert.Equal(state,Assert.Throws<MountIndexFormatException>(()=>new MountRepository(scope.Data,scope.Registry)).Format.State);
        Assert.Equal(before,scope.Json);Assert.False(Directory.Exists(scope.Data));
    }

    [Fact]
    public void CurrentConfigurationWithMissingIndexKeepsConfigurationAndDoesNotStartOver()
    {
        using var scope=new Scope();scope.Seed(MountDefinitionRules.RegistrySchemaVersion);var before=scope.Json;
        Assert.Equal("MissingIndex",new RegistryMountDefinitionStore(scope.Registry).InspectApplicationData(scope.Data).State);
        Assert.Equal("MissingIndex",Assert.Throws<MountIndexFormatException>(()=>new MountRepository(scope.Data,scope.Registry)).Format.State);
        Assert.Equal(before,scope.Json);Assert.False(Directory.Exists(scope.Data));
    }

    [Theory]
    [InlineData("Source")][InlineData("RegistrationStage")]
    public void MissingRequiredDefinitionMemberIsCorruptAndNotInferred(string member)
    {
        using var scope=new Scope();scope.Seed(MountDefinitionRules.RegistrySchemaVersion);
        var node=System.Text.Json.Nodes.JsonNode.Parse(scope.Json)!.AsObject();Assert.True(node.Remove(member));scope.Json=node.ToJsonString();
        var before=scope.Json;
        Assert.Equal("Corrupt",new RegistryMountDefinitionStore(scope.Registry).InspectApplicationData(scope.Data).State);
        Assert.Throws<MountIndexFormatException>(()=>new MountRepository(scope.Data,scope.Registry));
        Assert.Equal(before,scope.Json);Assert.False(Directory.Exists(scope.Data));
    }

    [Fact]
    public void CurrentDefinitionsAndTasksRoundTripWithoutMigration()
    {
        using var scope=new Scope();var repository=new MountRepository(scope.Data,scope.Registry);
        repository.SaveDefinition(scope.Definition);repository.SaveOperation(new("current","refresh","Canceled",0,0,[],null));
        var reopened=new MountRepository(scope.Data,scope.Registry);
        Assert.Equal(scope.Definition,Assert.Single(reopened.LoadDefinitions()));
        Assert.Equal("Canceled",reopened.FindOperation("current")!.State);
        Assert.Equal("Current",new RegistryMountDefinitionStore(scope.Registry).InspectApplicationData(scope.Data).State);
    }

    private sealed class Scope:IDisposable
    {
        private readonly string _area=Directory.CreateTempSubdirectory("HybridMount.DataBaseline.").FullName;
        internal string Registry{get;}=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        internal MountDefinition Definition{get;}
        internal string Data=>Path.Combine(_area,"data");
        internal Scope()=>Definition=new(Guid.NewGuid(),"baseline",Path.Combine(_area,"view"),Source:new(FileSourceKind.WebDav,"https://fixture.invalid/"));
        private string Child=>Registry+"\\"+Definition.Id.ToString("N");
        internal string Json
        {
            get{using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Child)!;return (string)key.GetValue("Definition")!;}
            set{using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Child,true)!;key.SetValue("Definition",value);}
        }
        internal void Seed(int marker)
        {using var key=Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Child,true);key.SetValue("MountSchemaVersion",marker,RegistryValueKind.DWord);key.SetValue("Definition",JsonSerializer.Serialize(Definition));}
        public void Dispose(){Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Registry,false);Directory.Delete(_area,true);}
    }
}
