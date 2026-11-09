using System.Reflection;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class ProbeObjectScopeTests
{
    [Fact]
    public async Task S3PutCopyDeleteAndMultipartProbesStayAtConfiguredRoot()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/child/",[]);await using var source=fixture.Source();var entry=await source.UploadAsync("child/file",new MemoryStream(new byte[9*1024*1024]),null);await source.MoveAsync(entry.Path,"child/moved",entry.Version);
        var probes=fixture.Server.Requests.Select(request=>S3Key(fixture,request)).Where(key=>key.Contains(".hybridmount-probe-",StringComparison.Ordinal)).Distinct().ToArray();Assert.True(probes.Length>=5);Assert.Contains(probes,key=>key.Contains("-multipart-",StringComparison.Ordinal));Assert.Contains(probes,key=>key.EndsWith("-destination",StringComparison.Ordinal));Assert.All(probes,key=>{Assert.StartsWith("remote/",key);Assert.DoesNotContain('/',key[7..]);});
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task S3ActiveAndCleanupFailedProbesAreHiddenButForeignLookalikesStayVisible(bool failCleanup)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/child/",[]);await using var source=fixture.Source();var original=fixture.Server.Handler;var ready=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Handler=async request=>
        {
            var key=S3Key(fixture,request);
            if(failCleanup&&request.Method=="DELETE"&&key.Contains(".hybridmount-probe-",StringComparison.Ordinal))return HttpTestServer.Response.Text(503,"<Error><Code>ServiceUnavailable</Code></Error>");
            var response=await original(request);
            if(request.Method=="PUT"&&key.Contains(".hybridmount-probe-",StringComparison.Ordinal)&&ready.TrySetResult(key))await release.Task;
            return response;
        };
        var upload=source.UploadAsync("child/file",new MemoryStream([1]),null);var probe=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));var relative=probe[7..];fixture.Put(probe+"-foreign",[9]);
        try
        {
            Assert.DoesNotContain(await All(source.ListAsync("")),entry=>entry.Path==relative);Assert.DoesNotContain(await source.ReadSnapshotAsync([],default),entry=>entry.Path==relative);Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="HEAD"&&S3Key(fixture,request)==probe);
        }
        finally{release.TrySetResult();}
        await upload;await Drain(source);var listed=await All(source.ListAsync(""));Assert.DoesNotContain(listed,entry=>entry.Path==relative);Assert.Contains(listed,entry=>entry.Path==relative+"-foreign");
        var count=fixture.Server.Requests.Count;var changes=await Hints(source,new([new("created",relative),new("deleted",relative)]));Assert.Equal(2,changes.Count);Assert.All(changes,change=>{Assert.Empty(change.Entries);Assert.Empty(change.Deleted);});Assert.Equal(count,fixture.Server.Requests.Count);
        await using var other=fixture.Source();Assert.Equal(failCleanup,(await All(other.ListAsync(""))).Any(entry=>entry.Path==relative));
    }
    [Fact]
    public async Task S3RegisteredDerivedAndMultipartKeysDoNotLeakIntoSnapshotsOrHints()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var keys=new[]{"remote/.hybridmount-probe-"+Guid.NewGuid().ToString("N"),"remote/.hybridmount-probe-multipart-"+Guid.NewGuid().ToString("N")};keys=[..keys,keys[0]+"-destination"];
        foreach(var key in keys){Call(source,"TrackProbeKey",key);fixture.Put(key,[1]);}fixture.Put(keys[0]+"-user",[9]);
        var listed=await All(source.ListAsync(""));var snapshot=await source.ReadSnapshotAsync([],default);foreach(var key in keys){Assert.DoesNotContain(listed,entry=>entry.Path==key[7..]);Assert.DoesNotContain(snapshot,entry=>entry.Path==key[7..]);}Assert.Contains(snapshot,entry=>entry.Path==keys[0][7..]+"-user");
        var changes=await Hints(source,new([new("user",keys[0][7..]+"-user")]));Assert.Single(Assert.Single(changes).Entries);
    }
    [Fact]
    public async Task S3RejectedInitialProbeDoesNotClaimOrDeleteForeignObject()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var handler=fixture.Server.Handler;string? collision=null;
        fixture.Server.Handler=request=>{var key=S3Key(fixture,request);if(request.Method=="PUT"&&key.Contains(".hybridmount-probe-",StringComparison.Ordinal)&&collision is null){collision=key;fixture.Put(key,[9]);}return handler(request);};
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("file",new MemoryStream([1]),null));Assert.NotNull(collision);Assert.Equal(new byte[]{9},fixture.Objects[collision].Bytes);Assert.Contains(await All(source.ListAsync("")),entry=>entry.Path==collision[7..]);Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&S3Key(fixture,request)==collision);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task DavNestedCreatesProbeAtRootWithoutMovingUploadTemporaryOutOfParent(bool fallback)
    {
        await using var fixture=new DavFixture{IgnoreConditions=fallback};fixture.Objects["child"]=new([],"\"child\"",true);await using var source=fixture.Source();await source.UploadAsync("child/file",new MemoryStream([1,2,3]),null);
        var probes=fixture.Server.Requests.Select(request=>Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath)[5..].TrimEnd('/')).Where(path=>path.Contains("hybridmount-probe-",StringComparison.Ordinal)).Distinct().ToArray();Assert.NotEmpty(probes);Assert.All(probes,path=>Assert.DoesNotContain('/',path));
        if(fallback)Assert.Contains(fixture.Server.Requests,request=>request.Method=="PUT"&&request.Target.Contains("/child/hybridmount-upload-",StringComparison.Ordinal));Assert.Equal(new byte[]{1,2,3},fixture.Objects["child/file"].Bytes);
    }
    [Theory]
    [InlineData("Planned")][InlineData("CleanupPending")][InlineData("Completed")][InlineData("Foreign")]
    public async Task DavListingsAndReportsHideOnlyRegisteredOwnedProbes(string stage)
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();var probe=(string)Call(source,"ProbePath","child")!;var foreign=probe+"-foreign";fixture.Dav.Put(probe,[17,31]);fixture.Dav.Put(foreign,[9]);
        if(stage=="Completed")Call(source,"CompleteTemporary",probe);else Call(source,"SaveTemporary",Assert.Single(source.PendingTemporaryItems) with{Stage=stage});
        var listed=await All(source.ListAsync(""));Assert.Equal(stage=="Foreign",listed.Any(entry=>entry.Path==probe));Assert.Contains(listed,entry=>entry.Path==foreign);
        fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:test:next",[SyncDavFixture.Resource("/dav/"+probe,404),SyncDavFixture.Resource("/dav/"+foreign,404)]);
        var deleted=await source.GetCollectionChangesAsync("","urn:test:old",default);Assert.Equal(stage=="Foreign",deleted.Deleted.Contains(probe));Assert.Contains(foreign,deleted.Deleted);Assert.Equal("urn:test:next",deleted.NextToken);
        fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:test:created",[fixture.Item(probe),fixture.Item(foreign)]);var created=await source.GetCollectionChangesAsync("","urn:test:next",default);Assert.Equal(stage=="Foreign",created.Entries.Any(entry=>entry.Path==probe));Assert.Contains(created.Entries,entry=>entry.Path==foreign);
        await using var other=fixture.Dav.Source();Assert.Contains(await All(other.ListAsync("")),entry=>entry.Path==probe);
    }
    [Theory]
    [InlineData(null)][InlineData("urn:test:old")]
    public async Task DavLateProbeDeletionCanAdvanceAnOtherwiseEmptyBatch(string? token)
    {
        await using var fixture=new SyncDavFixture();await using var source=fixture.Dav.Source();var probe=(string)Call(source,"ProbePath","child")!;Call(source,"CompleteTemporary",probe);fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:test:next",[SyncDavFixture.Resource("/dav/"+probe,404)]);
        if(token is null)await Assert.ThrowsAnyAsync<IOException>(()=>source.GetCollectionChangesAsync("",token,default));
        else{var batch=await source.GetCollectionChangesAsync("",token,default);Assert.Empty(batch.Entries);Assert.Empty(batch.Deleted);Assert.Equal("urn:test:next",batch.NextToken);}
    }
    [Theory]
    [InlineData("probe")][InlineData("upload")]
    public async Task DavRestoredProbeRegistrationSurvivesJournalCompletionWithoutHidingUploadTargets(string purpose)
    {
        await using var fixture=new DavFixture();await using var source=fixture.Source();var name="hybridmount-"+purpose+"-"+Guid.NewGuid().ToString("N");var repository=new MountRepository(Path.Combine(fixture.Area,"probe-journal"),@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N"));var mount=new MountDefinition(Guid.NewGuid(),"probe journal",fixture.Area,Source:fixture.Options);
        var scope=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fixture.Options.Endpoint+"\0user")));repository.SaveTemporary(new(mount.Id,scope,name,purpose,"Owned",[]));source.AttachTemporaryJournal(mount,repository);Call(source,"CompleteTemporary",name);fixture.Put(name,[9]);
        Assert.Equal(purpose!="probe",(await All(source.ListAsync(""))).Any(entry=>entry.Path==name));Assert.Empty(repository.TemporaryItems(mount.Id,scope));
    }
    /// <summary>等待DAV独立后台清理完成再检查零残留，不改变失败／冲突及内容断言。</summary>
    private static Task Drain(IFileSource source)=>source is WebDavFileSource dav?dav.DrainTemporaryCleanupAsync():Task.CompletedTask;
    private static object? Call(object source,string name,params object?[] args)=>source.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(source,args);
    private static string S3Key(S3Fixture fixture,HttpTestServer.Request request)=>Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath)[13..];
    private static async Task<List<SourceEntry>> All(IAsyncEnumerable<SourceEntry> entries){var list=new List<SourceEntry>();await foreach(var entry in entries)list.Add(entry);return list;}
    private static async Task<List<RemoteHintChange>> Hints(S3FileSource source,HintJournal journal){var list=new List<RemoteHintChange>();await foreach(var change in source.ReadHintChangesAsync(journal,Guid.Empty,"scope",default))list.Add(change);return list;}
    private sealed class HintJournal(IReadOnlyList<RemotePathHint> hints):IRemoteHintJournal{public IReadOnlyList<RemotePathHint> PendingRemoteHints(Guid mount,string scope)=>hints;}
}
