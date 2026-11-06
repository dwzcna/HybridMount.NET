using System.Security.Cryptography;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>临时项必须有精确意图和可核对内容；断线残留可恢复，外部内容及目录不可删除。</summary>
public sealed class DavTemporaryTests
{
    private static MountDefinition Definition(DavFixture fixture)=>new(Guid.NewGuid(),"临时项回归",fixture.Area,Source:fixture.Options);
    [Fact]
    public async Task UploadCleansAllTrackedProbesAndNeverCreatesAuxiliaryDirectories()
    {
        await using var fixture=new DavFixture();var repository=new MountRepository(Path.Combine(fixture.Area,"journal"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));var mount=Definition(fixture);
        await using var source=fixture.Source();source.AttachTemporaryJournal(mount,repository);await source.ProbeAsync();
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method is "PUT" or "MKCOL" or "DELETE" or "LOCK");
        await source.UploadAsync("user.bin",new MemoryStream([1,2,3]),null);await source.DrainTemporaryCleanupAsync();
        Assert.Empty(source.PendingTemporaryItems);Assert.DoesNotContain(fixture.Objects.Keys,k=>k.StartsWith(".hybridmount-",StringComparison.Ordinal)||k.StartsWith("hybridmount-",StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="MKCOL");Assert.Equal(new byte[]{1,2,3},fixture.Objects["user.bin"].Bytes);
    }
    [Fact]
    public async Task FailedCleanupIsPersistedAndRecoveredWithoutRepeatingUpload()
    {
        await using var fixture=new DavFixture();var repository=new MountRepository(Path.Combine(fixture.Area,"journal"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));var mount=Definition(fixture);var handler=fixture.Server.Handler;var reject=true;
        fixture.Server.Handler=request=>reject&&request.Method=="DELETE"?Task.FromResult(new HttpTestServer.Response(409,[])):handler(request);
        await using(var source=fixture.Source()){source.AttachTemporaryJournal(mount,repository);await source.UploadAsync("user.bin",new MemoryStream([1,2,3]),null);Assert.NotEmpty(source.PendingTemporaryItems);}
        var writes=fixture.Server.Requests.Count(r=>r.Method=="PUT"&&r.Target.EndsWith("/user.bin",StringComparison.Ordinal));reject=false;
        await using var recovered=fixture.Source();recovered.AttachTemporaryJournal(mount,repository);await recovered.RecoverTemporaryItemsAsync();
        Assert.Empty(recovered.PendingTemporaryItems);Assert.Equal(writes,fixture.Server.Requests.Count(r=>r.Method=="PUT"&&r.Target.EndsWith("/user.bin",StringComparison.Ordinal)));
        Assert.Equal(new byte[]{1,2,3},fixture.Objects["user.bin"].Bytes);
    }
    [Fact]
    public async Task ChangedOrDirectoryTemporaryTargetsRemainProtectedAcrossRestart()
    {
        await using var fixture=new DavFixture();var repository=new MountRepository(Path.Combine(fixture.Area,"journal"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));var mount=Definition(fixture);var handler=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="DELETE"?Task.FromResult(new HttpTestServer.Response(409,[])):handler(request);
        string path;await using(var source=fixture.Source()){source.AttachTemporaryJournal(mount,repository);await source.UploadAsync("user.bin",new MemoryStream([1]),null);path=source.PendingTemporaryItems.First().Path;}
        fixture.Put(path,[7,7,7,7]);fixture.Server.Handler=handler;var before=fixture.Server.Requests.Count(r=>r.Method=="DELETE");
        await using var recovered=fixture.Source();recovered.AttachTemporaryJournal(mount,repository);await recovered.RecoverTemporaryItemsAsync();
        Assert.Equal(new byte[]{7,7,7,7},fixture.Objects[path].Bytes);Assert.Contains(recovered.PendingTemporaryItems,x=>x.Path==path&&x.Stage=="Foreign");Assert.Equal(before,fixture.Server.Requests.Count(r=>r.Method=="DELETE"));
        var row=recovered.PendingTemporaryItems.First();var directory=".hybridmount-probe-"+Guid.NewGuid().ToString("N");repository.SaveTemporary(row with{Path=directory,Stage="Owned"});
        fixture.Objects[directory]=new([],"\"foreign-directory\"",true);
        await using var again=fixture.Source();again.AttachTemporaryJournal(mount,repository);await again.RecoverTemporaryItemsAsync();Assert.True(fixture.Objects[directory].Directory);Assert.Contains(again.PendingTemporaryItems,x=>x.Path==directory&&x.Stage=="Foreign");
    }
    [Fact(Timeout=30000)]
    public async Task CanceledCreateWithLostResponseCleansKnownContentUsingIndependentToken()
    {
        await using var fixture=new DavFixture();var handler=fixture.Server.Handler;var sent=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var first=0;
        fixture.Server.Handler=async request=>{var response=await handler(request);if(request.Method=="PUT"&&Interlocked.Increment(ref first)==1){sent.TrySetResult();await release.Task;}return response;};
        await using var source=fixture.Source();using var cancel=new CancellationTokenSource();
        try{var upload=source.UploadAsync("user.bin",new MemoryStream([1,2]),null,cancel.Token);await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));cancel.Cancel();await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>upload);await source.DrainTemporaryCleanupAsync();
            Assert.Empty(source.PendingTemporaryItems);Assert.DoesNotContain(fixture.Objects.Keys,k=>k.StartsWith(".hybridmount-",StringComparison.Ordinal)||k.StartsWith("hybridmount-",StringComparison.Ordinal));}
        finally{release.TrySetResult();}
    }
    [Fact]
    public async Task PlannedButNeverSentResourceIsNotDeletedAndAnotherAccountCannotClaimJournal()
    {
        await using var fixture=new DavFixture();var repository=new MountRepository(Path.Combine(fixture.Area,"journal"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));var mount=Definition(fixture);var handler=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="DELETE"?Task.FromResult(new HttpTestServer.Response(409,[])):handler(request);
        RemoteTemporaryItem row;await using(var source=fixture.Source()){source.AttachTemporaryJournal(mount,repository);await source.UploadAsync("user.bin",new MemoryStream([1]),null);row=source.PendingTemporaryItems.First();}
        fixture.Server.Handler=handler;repository.SaveTemporary(row with{Stage="Planned"});
        await using var other=new WebDavFileSource(fixture.Options,new("other-account","password"),new WholeFileCache(Path.Combine(fixture.Area,"other-cache")));other.AttachTemporaryJournal(mount,repository);Assert.Empty(other.PendingTemporaryItems);
        await using var recovered=fixture.Source();recovered.AttachTemporaryJournal(mount,repository);await recovered.RecoverTemporaryItemsAsync();Assert.True(fixture.Objects.ContainsKey(row.Path));Assert.Equal("Foreign",recovered.PendingTemporaryItems.Single(x=>x.Path==row.Path).Stage);
    }
}
