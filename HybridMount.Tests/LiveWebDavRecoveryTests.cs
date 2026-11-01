using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>公开恢复入口只清理完整登记的数据；未知文件或已变化的探测内容不能被删除。</summary>
public sealed class LiveWebDavRecoveryTests
{
    [Theory]
    [InlineData("known")]
    [InlineData("unknown")]
    [InlineData("changed")]
    [InlineData("active")]
    public async Task RecoveryVerifiesJournalAndNeverDeletesForeignContent(string mode)
    {
        await using var fixture=new DavFixture();var initial=fixture.Objects.Keys.Order(StringComparer.Ordinal).ToArray();var id=Guid.NewGuid();var work=Path.Combine(fixture.Area,"recovery");Directory.CreateDirectory(Path.Combine(work,"mount"));new CredentialStore(work).Save(id,new("user","app-password"));
        var root="hm-validation-0053-"+id.ToString("N");var probe=".hybridmount-probe-"+Guid.NewGuid().ToString("N");fixture.Objects[root]=new([],"\"root\"",true);fixture.Objects[root+"/mount"]=new([],"\"dir\"",true);fixture.Put(root+"/known.txt",[1,2,3]);fixture.Put(root+"/mount/"+probe,mode=="changed"?[99]:[17,31]);if(mode=="unknown")fixture.Put(root+"/foreign.txt",[9]);
        var report=new{RunId=id,RemoteRoot=root,Endpoint=fixture.Options.Endpoint,Phase=mode=="active"?"ContinuousSoak":"StoppedBeforeRequestedDuration",EndedUtc=mode=="active"?(DateTimeOffset?)null:DateTimeOffset.UtcNow,StartedUtc=DateTimeOffset.UtcNow,KnownPaths=new[]{"known.txt","mount"}};await File.WriteAllTextAsync(Path.Combine(work,"live-report.json"),JsonSerializer.Serialize(report));
        var registry=@"Software\HybridMount.NET.Tests\LiveDav-"+id.ToString("N");var repository=new MountRepository(Path.Combine(work,"data"),registry);var scope=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Options.Endpoint+root+"/mount/\0user")));var mount=Guid.NewGuid();repository.SaveTemporary(new(mount,scope,probe,"probe","Foreign",[Convert.ToHexString(SHA256.HashData(new byte[]{17,31}))]));
        if(mode=="known")
        {
            var result=await LiveWebDavRecovery.RecoverAsync(work);Assert.True(File.Exists(result));Assert.Equal(initial,fixture.Objects.Keys.Order(StringComparer.Ordinal).ToArray());Assert.Empty(repository.TemporaryItems(mount,scope));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(()=>LiveWebDavRecovery.RecoverAsync(work));Assert.DoesNotContain(fixture.Server.Requests,x=>x.Method=="DELETE");Assert.Equal(mode=="changed"?new byte[]{99}:new byte[]{17,31},fixture.Objects[root+"/mount/"+probe].Bytes);
        }
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
    }
}
