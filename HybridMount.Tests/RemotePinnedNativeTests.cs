using System.Security.Cryptography;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>真实 NTFS/CFAPI 技术验证；原位占位版本更新、固定状态、长度和摘要独立验收。</summary>
public sealed class RemotePinnedNativeTests
{
    [Theory(Timeout=45000)]
    [InlineData(0,false)][InlineData(3,false)][InlineData(700000,false)][InlineData(4096,true)]
    public async Task PinnedVersionSwitchRetainsIdentityAndPin(int length,bool interruption)
    {
        await using var dav=new DavFixture();var local=Path.Combine(dav.Area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(dav.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(dav.Area,"logs.db"));
        var mount=new MountDefinition(Guid.NewGuid(),"固定更新原生验证",local,RootIdentity:MountPathGuard.FileIdentity(local),Source:dav.Options,RegistrationStage:MountRegistrationStage.NotRegistered);
        repository.SaveDefinition(mount);new CredentialStore(repository.DataDirectory).Save(mount.Id,new("user","app-password"));
        try
        {
            await CloudRootRegistration.EnsureAsync(mount,default);
            await using(var source=dav.Source())
            await using(var session=new CloudMountSession(mount,source,repository,logger))
            {
                session.Connect();var initial=await source.GetMetadataAsync("文件.bin");var entry=new MountEntry(Guid.NewGuid().ToString("N"),initial.Path,initial.Path,initial.Version,false);
                repository.SaveEntry(mount.Id,entry);session.CreatePlaceholder(entry,initial);entry=repository.Entries(mount.Id).Single();
                var path=Path.Combine(local,"文件.bin");
                using(var handle=CloudFilesNative.Open(path)){CloudFilesNative.Check(CloudFilesNative.CfSetPinState(handle,1,0,0));await CloudFilesNative.HydrateAsync(handle,default);}
                // 原生完成可能先于回调 finally，等待其归还读取计数。
                for(var i=0;session.IsActive(entry.Id)&&i<100;i++)await Task.Delay(10);
                var before=LocalFileState.Read(path);var identity=CloudFilesNative.Inspect(path)!.Value.Identity;
                var bytes=Enumerable.Range(0,length).Select(x=>(byte)(x*7)).ToArray();dav.Put("文件.bin",bytes);
                var metadata=await source.GetMetadataAsync("文件.bin");var staged=Path.Combine(dav.Area,"remote-snapshot.bin");await File.WriteAllBytesAsync(staged,bytes);
                var pending=new RemotePendingChange(Guid.NewGuid().ToString("N"),mount.Id,"native",metadata.Path,metadata,SnapshotPath:staged,
                    Sha256:Convert.ToHexString(SHA256.HashData(bytes)),OriginalStamp:before.Key,EntryId:entry.Id);
                repository.SaveRemotePending(pending);
                if(interruption)
                {
                    await Assert.ThrowsAsync<IOException>(()=>CloudRemoteUpdate.ApplyAsync(mount,entry,pending,session,(change,current)=>
                    {repository.CommitRemotePlaceholder(change,current);if(change.State=="Hydrating")throw new IOException("受控提交后中断");}));
                    pending=repository.RemotePending(mount.Id,"native").Single();entry=repository.Entries(mount.Id).Single();
                    for(var i=0;session.IsActive(entry.Id)&&i<200;i++)await Task.Delay(10);
                }
                await CloudRemoteUpdate.ApplyAsync(mount,entry,pending,session,(change,current)=>repository.CommitRemotePlaceholder(change,current));
                Assert.Equal(identity,CloudFilesNative.Inspect(path)!.Value.Identity);Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);
                Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);
                Assert.Equal(bytes,await File.ReadAllBytesAsync(path));Assert.Equal("Completed",repository.RemotePending(mount.Id,"native").Single().State);
            }
        }
        finally
        {
            await using var cleanup=new MountManager(repository,logger);
            var result=await WebDavTests.Finished(cleanup,cleanup.Queue(mount.Id,"clearAndRemove"));
            Assert.True(result.State=="Completed",result.Error);Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }
}
