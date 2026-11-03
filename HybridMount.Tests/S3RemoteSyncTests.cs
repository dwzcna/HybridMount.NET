using System.Security.Cryptography;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>真实 AWS SDK 对本地 HTTP 服务验证完整快照；模拟通过不代替真实提供商认证。</summary>
public sealed class S3RemoteSyncTests
{
    private static MountRepository Repository(S3Fixture fixture)=>new(Path.Combine(fixture.Area,"sync-data"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task CompleteFlatPaginationFindsEveryNestedObjectAndAvoidsUnchangedHead()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var repository=Repository(fixture);var id=Guid.NewGuid();
        var engine=new RemoteSyncEngine(id,source.RemoteScope(0),source,source,repository);await engine.PollAsync(true,TimeSpan.Zero,default);
        var prior=repository.RemoteShadow(id,engine.Scope);Assert.Equal(4,prior.Count);Assert.Contains(prior,x=>x.Entry.Path=="dir/child.bin");
        var before=fixture.Server.Requests.Count;await engine.PollAsync(true,TimeSpan.Zero,default);
        Assert.DoesNotContain(fixture.Server.Requests.Skip(before),x=>x.Method=="HEAD");Assert.Equal(prior.Count,repository.RemotePending(id,engine.Scope).Count);
        Assert.Equal("S3 完整对账",engine.Mode);
    }
    [Fact]
    public async Task ListFractionalSecondsAndHttpDateSecondPrecisionAreEquivalentObservations()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var repository=Repository(fixture);var id=Guid.NewGuid();var original=fixture.Server.Handler;
        fixture.Server.Handler=async request=>{var response=await original(request);if(request.Target.Contains("list-type="))response=response with{Body=System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(response.Body).Replace("12:00:00.000Z","12:00:00.123Z"))};return response;};
        var engine=new RemoteSyncEngine(id,"scope",source,source,repository);await engine.PollAsync(true,TimeSpan.Zero,default);
        var file=repository.RemoteShadow(id,"scope").Single(x=>x.Entry.Path=="文件.bin");Assert.Equal(123,file.Entry.ModifiedUtc.Millisecond);
        var before=fixture.Server.Requests.Count;await engine.PollAsync(true,TimeSpan.Zero,default);Assert.DoesNotContain(fixture.Server.Requests.Skip(before),x=>x.Method=="HEAD");
    }
    [Fact]
    public async Task FailedLastPageDoesNotCommitObservedNewObjectsOrMissingFiles()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var repository=Repository(fixture);var id=Guid.NewGuid();var engine=new RemoteSyncEngine(id,"scope",source,source,repository);
        await engine.PollAsync(true,TimeSpan.Zero,default);var before=repository.RemoteCollections(id,"scope").Single();var pending=repository.RemotePending(id,"scope").Count;
        fixture.Objects.TryRemove("remote/文件.bin",out _);fixture.Put("remote/new.bin",[9]);var handler=fixture.Server.Handler;
        fixture.Server.Handler=r=>r.Target.Contains("continuation-token=")?Task.FromResult(new HttpTestServer.Response(503,[])):handler(r);
        await Assert.ThrowsAnyAsync<IOException>(()=>engine.PollAsync(true,TimeSpan.Zero,default));
        Assert.Equal(before,repository.RemoteCollections(id,"scope").Single());Assert.Equal(pending,repository.RemotePending(id,"scope").Count);
        Assert.Contains(repository.RemoteShadow(id,"scope"),x=>x.Entry.Path=="文件.bin");Assert.DoesNotContain(repository.RemoteShadow(id,"scope"),x=>x.Entry.Path=="new.bin");
        fixture.Server.Handler=handler;await new RemoteSyncEngine(id,"scope",source,source,new MountRepository(repository.DataDirectory)).PollAsync(true,TimeSpan.Zero,default);
        Assert.Contains(repository.RemotePending(id,"scope"),x=>x.Path=="文件.bin"&&x.Entry is null);
    }
    [Theory]
    [InlineData(403)][InlineData(503)]
    public async Task MissingListItemRequiresCurrentHeadAndPrefixConfirmation(int status)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var repository=Repository(fixture);var id=Guid.NewGuid();var engine=new RemoteSyncEngine(id,"scope",source,source,repository);
        await engine.PollAsync(true,TimeSpan.Zero,default);fixture.Objects.TryRemove("remote/文件.bin",out _);var handler=fixture.Server.Handler;
        fixture.Server.Handler=r=>r.Method=="HEAD"&&Uri.UnescapeDataString(r.Target).Contains("文件.bin")?Task.FromResult(new HttpTestServer.Response(status,[])):handler(r);
        await Assert.ThrowsAnyAsync<IOException>(()=>engine.PollAsync(true,TimeSpan.Zero,default));Assert.DoesNotContain(repository.RemotePending(id,"scope"),x=>x.Entry is null);
        Assert.Contains(repository.RemoteShadow(id,"scope"),x=>x.Entry.Path=="文件.bin");
    }
    [Fact]
    public async Task VirtualDirectoryPresencePreventsStaleListDeletion()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var repository=Repository(fixture);var id=Guid.NewGuid();var engine=new RemoteSyncEngine(id,"scope",source,source,repository);
        await engine.PollAsync(true,TimeSpan.Zero,default);fixture.Objects.TryRemove("remote/dir/child.bin",out _);var handler=fixture.Server.Handler;var added=false;
        fixture.Server.Handler=async r=>{var response=await handler(r);if(!added&&r.Method=="HEAD"&&r.Target.EndsWith("/dir")){added=true;fixture.Put("remote/dir/late.bin",[7]);}return response;};
        await engine.PollAsync(true,TimeSpan.Zero,default);Assert.True(added);Assert.DoesNotContain(repository.RemotePending(id,"scope"),x=>x.Path=="dir"&&x.Entry is null);
        Assert.Contains(repository.RemoteShadow(id,"scope"),x=>x.Entry.Path=="dir/late.bin");
    }
    [Fact]
    public async Task HeadAfterListCannotSilentlyAcceptConcurrentReplacement()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var repository=Repository(fixture);var id=Guid.NewGuid();var engine=new RemoteSyncEngine(id,"scope",source,source,repository);
        await engine.PollAsync(true,TimeSpan.Zero,default);var old=repository.RemoteShadow(id,"scope").Single(x=>x.Entry.Path=="文件.bin");fixture.Put("remote/文件.bin",[1]);
        var handler=fixture.Server.Handler;var changed=false;fixture.Server.Handler=r=>{if(!changed&&r.Method=="HEAD"&&Uri.UnescapeDataString(r.Target).Contains("文件.bin")){changed=true;fixture.Put("remote/文件.bin",[2]);}return handler(r);};
        var before=fixture.Server.Requests.Count;await engine.PollAsync(true,TimeSpan.Zero,default);
        Assert.True(changed);Assert.NotEqual(old,repository.RemoteShadow(id,"scope").Single(x=>x.Entry.Path=="文件.bin"));
        Assert.True(fixture.Server.Requests.Skip(before).Count(x=>x.Target.Contains("list-type=")&&!x.Target.Contains("continuation-token="))>=2);
    }
    [Fact]
    public async Task ContinuouslyChangingObjectHasBoundedImmediateRescansAndKeepsPreviousSnapshot()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var repository=Repository(fixture);var id=Guid.NewGuid();var engine=new RemoteSyncEngine(id,"scope",source,source,repository);
        await engine.PollAsync(true,TimeSpan.Zero,default);var old=repository.RemoteShadow(id,"scope").Single(x=>x.Entry.Path=="文件.bin");fixture.Put("remote/文件.bin",[1]);
        var original=fixture.Server.Handler;var mutations=0;fixture.Server.Handler=r=>{if(r.Method=="HEAD"&&Uri.UnescapeDataString(r.Target).Contains("文件.bin")){mutations++;fixture.Put("remote/文件.bin",[(byte)mutations]);}return original(r);};
        await Assert.ThrowsAsync<SourceRescanRequiredException>(()=>engine.PollAsync(true,TimeSpan.Zero,default));
        Assert.Equal(3,mutations);Assert.Equal(old,repository.RemoteShadow(id,"scope").Single(x=>x.Entry.Path=="文件.bin"));Assert.DoesNotContain(repository.RemotePending(id,"scope"),x=>x.Entry is null);
    }
    [Fact(Timeout=45000)]
    public async Task ManagerReusesNativePinnedUpdateAndRemoteDeletionWithoutUploadingEcho()
    {
        await using var fixture=new S3Fixture();var repository=Repository(fixture);await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));await using var manager=new MountManager(repository,logger);
        var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);var mount=await manager.AddAsync("S3 自动对账",local,false,source:fixture.Options,credentials:S3Fixture.Credentials);
        try
        {
            Assert.True(mount.RemoteAutoDetectEnabled);Assert.Equal(300,mount.RemoteFullSeconds);await manager.SetRemoteDetectionAsync(mount.Id,false,60,300);
            var path=Path.Combine(local,"文件.bin");Assert.Equal("Completed",(await WebDavTests.Finished(manager,manager.Queue(mount.Id,"keepOnDevice",[path]))).State);
            var identity=LocalFileState.Read(path).Identity;var bytes=Enumerable.Repeat((byte)76,32993).ToArray();fixture.Put("remote/文件.bin",bytes);var before=fixture.Server.Requests.Count;
            var refreshed=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"refresh"));Assert.True(refreshed.State=="Completed",refreshed.Error);
            Assert.Equal(Digest(bytes),Digest(await File.ReadAllBytesAsync(path)));Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
            fixture.Objects.TryRemove("remote/文件.bin",out _);Assert.Equal("Completed",(await WebDavTests.Finished(manager,manager.Queue(mount.Id,"refresh"))).State);Assert.False(File.Exists(path));
            await manager.ScanUploadChangesAsync(mount.Id);await Task.Delay(2200);Assert.DoesNotContain(fixture.Server.Requests.Skip(before),x=>x.Method is "PUT" or "POST");
        }
        finally{Assert.Equal("Completed",(await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"))).State);}
    }
    private static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
}
