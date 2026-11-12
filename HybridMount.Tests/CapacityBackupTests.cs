using System.Buffers.Binary;
using System.Text.Json;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>JSON 容量备份、旧定义默认行为和只保存导入；凭据仅在隔离目录保存。</summary>
public sealed class CapacityBackupTests
{
    private const string Password="中文 capacity password";
    private static MountBackupItem Item()=>new("容量备份","旧目录",new(FileSourceKind.WebDav,"https://dav.example.test/dav/"),new("user","password"));
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void HmxbkKeepsCapacityOptions(bool automatic)
    {
        var item=Item() with{CapacityAutoDetectEnabled=automatic,MaximumCapacityBytes=automatic?null:123456789};
        var bytes=MountBackupCodec.Encode(new(1,DateTimeOffset.UtcNow,[item]),Password);
        Assert.Equal(item,MountBackupCodec.Decode(bytes,Password).Mounts.Single());
    }
    [Fact]
    public void InvalidCapacityIsRejectedAndOldLocalDefinitionsRetainDefaults()
    {
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(new(1,DateTimeOffset.UtcNow,[Item() with{MaximumCapacityBytes=0}]),Password));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(new(1,DateTimeOffset.UtcNow,[Item() with{Source=new(FileSourceKind.S3,"https://s3.example.test","bucket"),CapacityAutoDetectEnabled=true}]),Password));
        var old=JsonSerializer.Serialize(new MountDefinition(Guid.NewGuid(),"旧挂载",@"D:\old",Source:new(FileSourceKind.WebDav,"https://example.test")));
        using var json=JsonDocument.Parse(old);var values=json.RootElement.EnumerateObject().Where(x=>x.Name is not("CapacityAutoDetectEnabled" or "MaximumCapacityBytes")).ToDictionary(x=>x.Name,x=>x.Value.Clone());
        var restored=JsonSerializer.Deserialize<MountDefinition>(JsonSerializer.Serialize(values))!;Assert.False(CapacityRules.Enabled(restored));
    }
    [Fact]
    public async Task Import_saves_capacity_and_reexports_hmxbk_without_connection_or_root_registration()
    {
        await using var fixture=new DavFixture();var area=Path.Combine(fixture.Area,"backup");Directory.CreateDirectory(area);var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(area,"data"),registry);await using var logger=new LoggerService(Path.Combine(area,"log.db"));await using var manager=new MountManager(repository,logger);
        var local=Path.Combine(area,"view");Directory.CreateDirectory(local);
        try
        {
            var item=Item() with{Source=fixture.Options,MaximumCapacityBytes=1000000};var result=await manager.ImportConfigurationsAsync([new(item,local)]);Assert.Equal("Saved",Assert.Single(result.Results).State);
            var saved=manager.Mounts.Single().Definition;Assert.Equal(item.MaximumCapacityBytes,saved.MaximumCapacityBytes);Assert.False(saved.CapacityAutoDetectEnabled);Assert.Equal(MountRegistrationStage.NotRegistered,saved.RegistrationStage);
            Assert.Empty(fixture.Server.Requests);Assert.Null(MountPathGuard.ReadRoot(local));Assert.Empty(repository.Uploads());
            var file=Path.Combine(area,"backup.hmxbk");await manager.ExportConfigurationsAsync([saved.Id],Password,file);var restored=await manager.ReadConfigurationBackupAsync(file,Password);Assert.Equal(1,restored.SchemaVersion);Assert.Empty(restored.Mounts.Single().ExclusionRules??[]);Assert.Equal(item.MaximumCapacityBytes,restored.Mounts.Single().MaximumCapacityBytes);Assert.Empty(fixture.Server.Requests);
        }
        finally{await manager.DisposeAsync();Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
}
