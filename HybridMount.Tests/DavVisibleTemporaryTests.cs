using System.Security.Cryptography;
using System.Text;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>模拟禁止点开头路径的 DAV；验证安全上传、精确清理与真实文件系统触发，不访问正式服务。</summary>
public sealed class DavVisibleTemporaryTests
{
    /// <summary>只对本地夹具拒绝隐藏路径；其余请求继续执行原协议条件。</summary>
    private static void ForbidHiddenPaths(DavFixture fixture)
    {
        var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>
        {
            var path=Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath);
            return path.Split('/').Any(segment=>segment.StartsWith(".",StringComparison.Ordinal))
                ?Task.FromResult(new HttpTestServer.Response(403,[])):original(request);
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenPathPolicyStillAllowsSafeCreateAndNeverOverwrites(bool fallback)
    {
        await using var fixture=new DavFixture{IgnoreConditions=fallback};ForbidHiddenPaths(fixture);
        await using var source=fixture.Source();
        var bytes=new byte[]{1,2,3};var receipt=await source.UploadAsync("new.bin",new MemoryStream(bytes),null);
        Assert.Equal(bytes.Length,receipt.Size);Assert.Equal(bytes,fixture.Objects["new.bin"].Bytes);
        Assert.Empty(source.PendingTemporaryItems);
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="MKCOL");
        Assert.DoesNotContain(fixture.Server.Requests,request=>Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath).Split('/').Any(segment=>segment.StartsWith(".",StringComparison.Ordinal)));
        Assert.Equal(new[]{"","new.bin","文件.bin","空目录"},fixture.Objects.Keys.Order(StringComparer.Ordinal).ToArray());
        if(fallback)Assert.Contains(fixture.Server.Requests,request=>request.Method=="MOVE"&&request.Headers.GetValueOrDefault("Overwrite")=="F");
        else Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="MOVE");
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("new.bin",new MemoryStream([9]),null));
        Assert.Equal(bytes,fixture.Objects["new.bin"].Bytes);Assert.Empty(source.PendingTemporaryItems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalRestoresBothNamesAndNeverClaimsUntrackedLookalike(bool legacy)
    {
        await using var fixture=new DavFixture();
        var repository=new MountRepository(Path.Combine(fixture.Area,"journal"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));
        var mount=new MountDefinition(Guid.NewGuid(),"命名兼容回归",fixture.Area,Source:fixture.Options);
        var scope=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Options.Endpoint+"\0user")));
        var prefix=legacy?".hybridmount-probe-":"hybridmount-probe-";
        var tracked=prefix+Guid.NewGuid().ToString("N");var untracked=prefix+Guid.NewGuid().ToString("N");
        fixture.Put(tracked,[17,31]);fixture.Put(untracked,[7,8,9]);
        repository.SaveTemporary(new(mount.Id,scope,tracked,"probe","Owned",[Convert.ToHexString(SHA256.HashData(new byte[]{17,31}))]));
        await using var source=fixture.Source();source.AttachTemporaryJournal(mount,repository);await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();
        Assert.Empty(source.PendingTemporaryItems);Assert.Empty(repository.TemporaryItems(mount.Id,scope));
        Assert.False(fixture.Objects.ContainsKey(tracked));Assert.Equal(new byte[]{7,8,9},fixture.Objects[untracked].Bytes);
        Assert.Single(fixture.Server.Requests,request=>request.Method=="DELETE");
    }

    [Fact]
    public async Task GenuineForbiddenTargetIsStillRejectedWithoutAlternateWrite()
    {
        await using var fixture=new DavFixture();ForbidHiddenPaths(fixture);await using var source=fixture.Source();
        var failure=await Assert.ThrowsAsync<SourceRequestRejectedException>(()=>source.UploadAsync(".user.bin",new MemoryStream([4,5]),null));
        Assert.Equal(403,failure.HttpStatus);Assert.False(fixture.Objects.ContainsKey(".user.bin"));
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method is "MOVE" or "LOCK");
        Assert.Empty(source.PendingTemporaryItems);
    }

    [Fact(Timeout=120000)]
    public async Task FileSystemChangesUploadThroughHiddenPathDenyingServerAndReconnect()
    {
        await using var test=await UploadFlowTests.Fixture.Create();ForbidHiddenPaths(test.Dav);
        var path=Path.Combine(test.Local,"FSD.SHX");var first=Encoding.UTF8.GetBytes("普通本地文件");
        await File.WriteAllBytesAsync(path,first);await test.Wait("FSD.SHX",bytes:first);
        Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);
        var second=Encoding.UTF8.GetBytes("再次修改后仍上传");await File.WriteAllBytesAsync(path,second);await test.Wait("FSD.SHX",bytes:second);
        var writes=test.Dav.Server.Requests.Count(request=>request.Method=="PUT"&&request.Target.EndsWith("/FSD.SHX",StringComparison.Ordinal));
        await test.Manager.DisconnectAsync(test.Id);await test.Manager.ConnectAsync(test.Id);await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.Equal("Completed",test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="FSD.SHX").State);
        Assert.Equal(writes,test.Dav.Server.Requests.Count(request=>request.Method=="PUT"&&request.Target.EndsWith("/FSD.SHX",StringComparison.Ordinal)));
        Assert.Empty(test.Repository.TemporaryItems(test.Id,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(test.Dav.Options.Endpoint+"\0user")))));
    }
}
