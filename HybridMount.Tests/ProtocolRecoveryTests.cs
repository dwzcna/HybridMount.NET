using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using System.Security.Cryptography;

namespace HybridMount.Tests;

/// <summary>跨版本只读迁出、持久保护合并及网络取消/重定向边界。</summary>
public sealed class ProtocolRecoveryTests
{
    [Fact(Timeout=120000)]
    public async Task LegacyMountMigratesThroughProductionReadOnlyRecovery()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.LegacyTests",Guid.NewGuid().ToString("N"));
        var local=Path.Combine(area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(area,"data"),registry);await using var logger=new LoggerService(Path.Combine(area,"log.db"));
        Guid id;
        await using(var previous=new MountManager(repository,logger,TestSourceFactory.Instance))
        {
            var mount=await previous.AddAsync("旧版迁出测试",local,true);id=mount.Id;
            Assert.Null(mount.Source);Assert.False(CloudFilesNative.HasFullContent(Path.Combine(local,"范围读取.bin"),32*1024*1024+37));
        }
        var sourceDb=Path.Combine(repository.DataDirectory,"MockSources",id.ToString("N"),"source.db");var before=SHA256.HashData(await File.ReadAllBytesAsync(sourceDb));
        await using(var production=new MountManager(repository,logger))
        {
            await production.StartAsync();Assert.Equal("需要迁出",Assert.Single(production.Mounts).Status);
            var result=await WebDavTests.Finished(production,production.Queue(id,"remove"));Assert.True(result.State=="Completed",result.Error);
            Assert.Empty(production.Mounts);Assert.Null(MountPathGuard.ReadRoot(local));
            Assert.Contains("按需下载",await File.ReadAllTextAsync(Path.Combine(local,"说明.txt")));
            Assert.Equal(before,SHA256.HashData(await File.ReadAllBytesAsync(sourceDb)));
        }
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);await logger.DisposeAsync();Directory.Delete(area,true);
    }
    [Fact]
    public void StaleWorkerCannotClearLocalProtectionDeletionOrMaterialization()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.MergeTests",Guid.NewGuid().ToString("N"));
        var repository=new MountRepository(area,@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));var mount=Guid.NewGuid();
        try
        {
            var old=new MountEntry("id","file","file","old",false);repository.SaveEntry(mount,old);
            repository.SaveEntry(mount,old with{Protected=true,Deleted=true,Materialized=true});repository.SaveEntry(mount,old with{Version="new"});
            var merged=Assert.Single(repository.Entries(mount));Assert.True(merged.Protected);Assert.True(merged.Deleted);Assert.True(merged.Materialized);
        }
        finally{Directory.Delete(area,true);}
    }
    [Fact]
    public async Task WebDavDoesNotForwardCredentialsOnRedirectAndReadCancellationWorks()
    {
        await using var fixture=new DavFixture();await using var redirected=new HttpTestServer();await using var source=fixture.Source();
        var metadata=await source.GetMetadataAsync("文件.bin");var original=fixture.Server.Handler;
        fixture.Server.Handler=async request=>{if(request.Method=="GET")await Task.Delay(200);return await original(request);};
        using(var cancel=new CancellationTokenSource(30))await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await source.ReadRangeAsync(metadata,0,new byte[3],cancel.Token));
        fixture.Server.Handler=request=>Task.FromResult(new HttpTestServer.Response(302,[],new(){["Location"]=redirected.Address.AbsoluteUri}));
        await Assert.ThrowsAsync<IOException>(()=>source.ProbeAsync());Assert.Empty(redirected.Requests);
    }
}
