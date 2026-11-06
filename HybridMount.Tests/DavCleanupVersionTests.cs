using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>临时项强验证器延迟、取消、重启及条件删除竞态；仅使用隔离HTTP服务。</summary>
public sealed class DavCleanupVersionTests
{
    private static (MountRepository Repository,MountDefinition Mount,string Path) Owned(DavFixture fixture)
    {
        var repository=new MountRepository(Path.Combine(fixture.Area,"journal"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));
        var mount=new MountDefinition(Guid.NewGuid(),"强版本清理回归",fixture.Area,Source:fixture.Options);
        var path="hybridmount-probe-"+Guid.NewGuid().ToString("N");fixture.Put(path,[17,31]);
        var scope=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fixture.Options.Endpoint+"\0user")));
        repository.SaveTemporary(new(mount.Id,scope,path,"probe","Owned",[Convert.ToHexString(SHA256.HashData(new byte[]{17,31}))]));
        return(repository,mount,path);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak\"")]
    [InlineData("bare-tag")]
    public async Task PersistentlyMissingStrongTagIsNeverDeletedAndReconnectRecovers(string? tag)
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var reads=0;
        fixture.XmlTag=_=>{Interlocked.Increment(ref reads);return tag;};
        await using(var source=fixture.Source())
        {
            source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();
            Assert.True(fixture.Objects.ContainsKey(owned.Path));Assert.Equal("CleanupPending",Assert.Single(source.PendingTemporaryItems).Stage);Assert.Equal(3,reads);
            Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE");
        }
        fixture.XmlTag=null;await using var recovered=fixture.Source();recovered.AttachTemporaryJournal(owned.Mount,owned.Repository);await recovered.RecoverTemporaryItemsAsync();await recovered.DrainTemporaryCleanupAsync();
        Assert.Empty(recovered.PendingTemporaryItems);Assert.False(fixture.Objects.ContainsKey(owned.Path));
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method is "PUT" or "MKCOL");
        Assert.Equal(fixture.Server.Requests.Single(r=>r.Method=="DELETE").Headers["If-Match"],fixture.Server.Requests.Last(r=>r.Method=="GET").Headers.GetValueOrDefault("If-Match"));
    }
    [Fact]
    public async Task ThirdMetadataReadSuppliesStrongTagThenDigestAndConditionalDeleteSucceed()
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var reads=0;
        fixture.XmlTag=item=>Interlocked.Increment(ref reads)<=2?null:item.ETag;
        await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();
        Assert.Empty(source.PendingTemporaryItems);Assert.Equal(4,reads);Assert.False(fixture.Objects.ContainsKey(owned.Path));
        Assert.True(fixture.Server.Requests.Single(r=>r.Method=="DELETE").Headers.ContainsKey("If-Match"));
    }
    [Theory(Timeout=15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationAndFiveSecondDeadlineKeepJournalAndReleaseHttp(bool callerCancel)
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var handler=fixture.Server.Handler;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Handler=async request=>{if(request.Method=="PROPFIND"){entered.TrySetResult();await release.Task;}return await handler(request);};
        await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);using var cancel=new CancellationTokenSource();
        var elapsed=Stopwatch.StartNew();var work=source.RecoverTemporaryItemsAsync(cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));if(callerCancel)cancel.Cancel();await work.WaitAsync(TimeSpan.FromSeconds(7));
            Assert.InRange(elapsed.ElapsedMilliseconds,0,callerCancel?2500:6500);Assert.True(fixture.Objects.ContainsKey(owned.Path));Assert.Equal("CleanupPending",Assert.Single(source.PendingTemporaryItems).Stage);
            Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE");
        }
        finally{release.TrySetResult();fixture.Server.Handler=handler;}
        await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();Assert.Empty(source.PendingTemporaryItems);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedContentOrVersionDuringDeleteNeverRemovesForeignObject(bool duringDelete)
    {
        await using var fixture=new DavFixture();var owned=Owned(fixture);var handler=fixture.Server.Handler;
        fixture.Server.Handler=async request=>
        {
            if(duringDelete&&request.Method=="DELETE")fixture.Put(owned.Path,[99,98]);
            var response=await handler(request);
            if(!duringDelete&&request.Method=="GET")fixture.Put(owned.Path,[99,98]);
            return response;
        };
        await using var source=fixture.Source();source.AttachTemporaryJournal(owned.Mount,owned.Repository);await source.RecoverTemporaryItemsAsync();await source.DrainTemporaryCleanupAsync();
        Assert.Equal(new byte[]{99,98},fixture.Objects[owned.Path].Bytes);Assert.NotEmpty(source.PendingTemporaryItems);
        if(!duringDelete)Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE");
        else Assert.True(fixture.Server.Requests.Single(r=>r.Method=="DELETE").Headers.ContainsKey("If-Match"));
    }
}
