using HybridMount.Infrastructure.Diagnostics;
using HybridMount.Infrastructure.Files;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Windows.CloudFiles;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using HybridMount.Tests.Fixtures.Protocols;
using HybridMount.Tests.Unit.Mounting;
using System.Security.Cryptography;

namespace HybridMount.Tests.Integration.Protocols;

/// <summary>持久保护合并及网络取消/重定向边界。</summary>
public sealed class ProtocolRecoveryTests
{
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
