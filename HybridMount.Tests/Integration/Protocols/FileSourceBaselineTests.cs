using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Protocols;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests.Integration.Protocols;

/// <summary>旧无协议源和数字0不得触发恢复读取；旧模拟数据原字节保持。</summary>
public sealed class FileSourceBaselineTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task MissingOrRemovedProtocolCannotReadOrChangeOldSource(bool removed)
    {
        var area=Directory.CreateTempSubdirectory("HybridMount.SourceBaseline.").FullName;
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        try
        {
            var repository=new MountRepository(Path.Combine(area,"data"),registry);var mount=Guid.NewGuid();
            var directory=Path.Combine(repository.DataDirectory,"MockSources",mount.ToString("N"));
            using(var seed=new MockFileSource(directory))await seed.UploadAsync("keep.bin",new MemoryStream(new byte[]{1,2,3}),null);
            var files=Directory.GetFiles(directory,"*",SearchOption.AllDirectories).ToDictionary(path=>path,File.ReadAllBytes);
            var definition=new MountDefinition(mount,"old",Path.Combine(area,"view"),Source:removed?new((FileSourceKind)0):null);
            await Assert.ThrowsAsync<NotSupportedException>(()=>new FileSourceFactory().CreateAsync(definition,repository,default));
            Assert.Equal(files.Count,Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Length);
            foreach(var file in files)Assert.Equal(file.Value,File.ReadAllBytes(file.Key));
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);Directory.Delete(area,true);}
    }
}
