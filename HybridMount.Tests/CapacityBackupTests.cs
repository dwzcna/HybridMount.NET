using System.Buffers.Binary;
using System.Text.Json;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>容量备份 v4、旧定义默认行为和只保存导入；凭据仅在隔离目录保存。</summary>
public sealed class CapacityBackupTests
{
    private const string Password="中文 capacity password";
    private static MountBackupItem Item()=>new("容量备份","旧目录",new(FileSourceKind.WebDav,"https://dav.example.test/dav/"),new("user","password"));
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)]
    public void Backup_versions_keep_their_schema_and_old_defaults(int version)
    {
        var item=Item();if(version==4)item=item with{CapacityAutoDetectEnabled=true,MaximumCapacityBytes=123456789};
        var encrypted=MountBackupCodec.Encode(new(version,DateTimeOffset.UtcNow,[item]),Password);Assert.Equal(version,BinaryPrimitives.ReadInt32LittleEndian(encrypted.AsSpan(8,4)));
        var restored=MountBackupCodec.Decode(encrypted,Password).Mounts.Single();Assert.Equal(item,restored);
        if(version<4){Assert.False(restored.CapacityAutoDetectEnabled);Assert.Null(restored.MaximumCapacityBytes);}
    }
    [Fact]
    public void Invalid_capacity_and_old_container_cannot_silently_drop_the_limit()
    {
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(new(3,DateTimeOffset.UtcNow,[Item() with{MaximumCapacityBytes=1}]),Password));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(new(4,DateTimeOffset.UtcNow,[Item() with{MaximumCapacityBytes=0}]),Password));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(new(4,DateTimeOffset.UtcNow,[Item() with{Source=new(FileSourceKind.S3,"https://s3.example.test","bucket"),CapacityAutoDetectEnabled=true}]),Password));
        var old=JsonSerializer.Serialize(new MountDefinition(Guid.NewGuid(),"旧挂载",@"D:\old",Source:new(FileSourceKind.WebDav,"https://example.test")));
        using var json=JsonDocument.Parse(old);var values=json.RootElement.EnumerateObject().Where(x=>x.Name is not("CapacityAutoDetectEnabled" or "MaximumCapacityBytes")).ToDictionary(x=>x.Name,x=>x.Value.Clone());
        var restored=JsonSerializer.Deserialize<MountDefinition>(JsonSerializer.Serialize(values))!;Assert.False(CapacityRules.Enabled(restored));
    }
    [Fact]
    public async Task Import_saves_capacity_and_reexports_v5_without_connection_or_root_registration()
    {
        await using var fixture=new DavFixture();var area=Path.Combine(fixture.Area,"backup");Directory.CreateDirectory(area);var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(area,"data"),registry);await using var logger=new LoggerService(Path.Combine(area,"log.db"));await using var manager=new MountManager(repository,logger);
        var local=Path.Combine(area,"view");Directory.CreateDirectory(local);
        try
        {
            var item=Item() with{Source=fixture.Options,MaximumCapacityBytes=1000000};var result=await manager.ImportConfigurationsAsync([new(item,local)]);Assert.Equal("Saved",Assert.Single(result.Results).State);
            var saved=manager.Mounts.Single().Definition;Assert.Equal(item.MaximumCapacityBytes,saved.MaximumCapacityBytes);Assert.False(saved.CapacityAutoDetectEnabled);Assert.Equal(MountRegistrationStage.NotRegistered,saved.RegistrationStage);
            Assert.Empty(fixture.Server.Requests);Assert.Null(MountPathGuard.ReadRoot(local));Assert.Empty(repository.Uploads());
            var file=Path.Combine(area,"backup.hmconfig");await manager.ExportConfigurationsAsync([saved.Id],Password,file);var restored=await manager.ReadConfigurationBackupAsync(file,Password);Assert.Equal(5,restored.SchemaVersion);Assert.Empty(restored.Mounts.Single().ExclusionRules??[]);Assert.Equal(item.MaximumCapacityBytes,restored.Mounts.Single().MaximumCapacityBytes);Assert.Empty(fixture.Server.Requests);
        }
        finally{await manager.DisposeAsync();Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);}
    }
}
