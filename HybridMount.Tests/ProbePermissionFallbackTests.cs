using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class ProbePermissionFallbackTests
{
    [Theory]
    [InlineData(401,"Unauthorized",false)]
    [InlineData(403,"AccessDenied",true)]
    [InlineData(400,"AccessDenied",false)]
    public async Task S3PermissionFallbackIsRegisteredBeforeWriteAndFilteredThroughCleanup(int status,string code,bool failCleanup)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/child/",[]);await using var source=fixture.Source();var original=fixture.Server.Handler;
        var ready=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var registered=false;
        fixture.Server.Handler=async request=>
        {
            var key=S3Key(fixture,request);
            if(request.Method=="PUT"&&IsRootS3Probe(key))return S3Error(status,code);
            if(failCleanup&&request.Method=="DELETE"&&IsProbe(key))return S3Error(503,"ServiceUnavailable");
            if(request.Method=="PUT"&&IsProbe(key)&&!ready.Task.IsCompleted)
            {
                registered=S3Tracked(source,key);var response=await original(request);ready.TrySetResult(key);await release.Task;return response;
            }
            return await original(request);
        };
        var upload=source.UploadAsync("child/file",new MemoryStream([1]),null);var probe=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));var relative=probe[7..];fixture.Put(probe+"-foreign",[9]);
        try
        {
            Assert.True(registered);Assert.StartsWith("remote/child/.hybridmount-probe-",probe);
            Assert.DoesNotContain(await All(source.ListAsync("child")),entry=>entry.Path==relative);
            Assert.DoesNotContain(await source.ReadSnapshotAsync([],default),entry=>entry.Path==relative);
            Assert.Contains(await All(source.ListAsync("child")),entry=>entry.Path==relative+"-foreign");
        }
        finally{release.TrySetResult();}
        await upload;await Drain(source);Assert.Equal(failCleanup,fixture.Objects.ContainsKey(probe));Assert.DoesNotContain(await All(source.ListAsync("child")),entry=>entry.Path==relative);
        var count=fixture.Server.Requests.Count;var hints=await Hints(source,relative);Assert.Equal(2,hints.Count);Assert.All(hints,hint=>{Assert.Empty(hint.Entries);Assert.Empty(hint.Deleted);});Assert.Equal(count,fixture.Server.Requests.Count);
        Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT"&&IsRootS3Probe(S3Key(fixture,request)));
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&IsRootS3Probe(S3Key(fixture,request)));
    }
    [Theory]
    [InlineData(401,false)]
    [InlineData(403,true)]
    public async Task DavPermissionFallbackIsRegisteredBeforeWriteAndFilteredFromListingsAndReports(int status,bool failCleanup)
    {
        await using var fixture=new SyncDavFixture();fixture.Dav.Objects["child"]=new([],"\"child\"",true);await using var source=fixture.Dav.Source();var original=fixture.Dav.Server.Handler;
        var ready=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var registered=false;
        fixture.Dav.Server.Handler=async request=>
        {
            var path=DavPath(fixture.Dav,request);
            if(request.Method=="PUT"&&IsRootDavProbe(path))return new(status,[]);
            if(failCleanup&&request.Method=="DELETE"&&IsProbe(path))return new(503,[]);
            if(request.Method=="PUT"&&IsProbe(path)&&!ready.Task.IsCompleted)
            {
                registered=(bool)Call(source,"IsInternalTemporary",path)!;var response=await original(request);ready.TrySetResult(path);await release.Task;return response;
            }
            return await original(request);
        };
        var upload=source.UploadAsync("child/file",new MemoryStream([1]),null);var probe=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));var foreign=probe+"-foreign";fixture.Dav.Put(foreign,[9]);
        try
        {
            Assert.True(registered);Assert.StartsWith("child/hybridmount-probe-",probe);Assert.DoesNotContain(await All(source.ListAsync("child")),entry=>entry.Path==probe);
            fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:test:created",[fixture.Item(probe),fixture.Item(foreign)]);
            var created=await source.GetCollectionChangesAsync("child","urn:test:old",default);Assert.DoesNotContain(created.Entries,entry=>entry.Path==probe);Assert.Contains(created.Entries,entry=>entry.Path==foreign);
        }
        finally{release.TrySetResult();}
        await upload;await Drain(source);Assert.Equal(failCleanup,fixture.Dav.Objects.ContainsKey(probe));Assert.DoesNotContain(await All(source.ListAsync("child")),entry=>entry.Path==probe);
        fixture.Report=(_,_)=>SyncDavFixture.Multi("urn:test:deleted",[SyncDavFixture.Resource("/dav/"+probe,404),SyncDavFixture.Resource("/dav/"+foreign,404)]);
        var deleted=await source.GetCollectionChangesAsync("child","urn:test:created",default);Assert.DoesNotContain(probe,deleted.Deleted);Assert.Contains(foreign,deleted.Deleted);Assert.Equal("urn:test:deleted",deleted.NextToken);
        Assert.Single(fixture.Dav.Server.Requests,request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture.Dav,request)));
        Assert.DoesNotContain(fixture.Dav.Server.Requests,request=>request.Method=="DELETE"&&IsRootDavProbe(DavPath(fixture.Dav,request)));
    }
    [Theory]
    [InlineData(500,"InternalError")][InlineData(503,"ServiceUnavailable")][InlineData(429,"SlowDown")][InlineData(400,"InvalidRequest")][InlineData(412,"PreconditionFailed")]
    public async Task S3OtherRootRejectionsDoNotFallbackOrCache(int status,string code)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/child/",[]);await using var source=fixture.Source();var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="PUT"&&IsRootS3Probe(S3Key(fixture,request))?Task.FromResult(S3Error(status,code)):original(request);
        var error=await Record.ExceptionAsync(()=>source.UploadAsync("child/file",Stream.Null,null));Assert.NotNull(error);
        if(status>=500)Assert.IsType<SourceOutcomeUnknownException>(error);else if(status==412)Assert.IsType<SourceConflictException>(error);else Assert.Equal(status,Assert.IsType<SourceConnectionException>(error).Diagnostic.HttpStatus);
        Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT");
        fixture.Server.Handler=original;await source.UploadAsync("child/retry",Stream.Null,null);Assert.Contains(fixture.Server.Requests,request=>request.Method=="PUT"&&IsRootS3Probe(S3Key(fixture,request)));
        Assert.DoesNotContain(fixture.Server.Requests,request=>IsProbe(S3Key(fixture,request))&&!IsRootS3Probe(S3Key(fixture,request)));
    }
    [Theory]
    [InlineData(500)][InlineData(503)][InlineData(429)][InlineData(400)][InlineData(412)][InlineData(404)][InlineData(307)]
    public async Task DavOtherRootRejectionsDoNotFallbackOrCache(int status)
    {
        await using var fixture=new DavFixture();fixture.Objects["child"]=new([],"\"child\"",true);await using var source=fixture.Source();var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture,request))?Task.FromResult(new HttpTestServer.Response(status,[])):original(request);
        var error=await Record.ExceptionAsync(()=>source.UploadAsync("child/file",Stream.Null,null));Assert.IsAssignableFrom<IOException>(error);
        if(status>=500)Assert.IsType<RemoteSyncException>(error);else if(status is 400 or 429)Assert.Equal(status,Assert.IsType<SourceRequestRejectedException>(error).HttpStatus);else if(status==412)Assert.IsType<SourceConflictException>(error);
        Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT");
        fixture.Server.Handler=original;await source.UploadAsync("child/retry",Stream.Null,null);
        Assert.DoesNotContain(fixture.Server.Requests,request=>IsProbe(DavPath(fixture,request))&&!IsRootDavProbe(DavPath(fixture,request)));
    }
    [Theory]
    [InlineData(0)][InlineData(1)]
    public async Task WritableS3RootNeverUsesTargetParent(int length)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/child/",[]);await using var source=fixture.Source();await source.UploadAsync("child/file",new MemoryStream(new byte[length]),null);
        var probes=fixture.Server.Requests.Where(request=>IsProbe(S3Key(fixture,request))).ToArray();Assert.NotEmpty(probes);Assert.All(probes,request=>Assert.True(IsRootS3Probe(S3Key(fixture,request))));
    }
    [Theory]
    [InlineData(0)][InlineData(1)]
    public async Task WritableDavRootNeverUsesTargetParent(int length)
    {
        await using var fixture=new DavFixture();fixture.Objects["child"]=new([],"\"child\"",true);await using var source=fixture.Source();await source.UploadAsync("child/file",new MemoryStream(new byte[length]),null);
        var probes=fixture.Server.Requests.Where(request=>IsProbe(DavPath(fixture,request))).ToArray();Assert.NotEmpty(probes);Assert.All(probes,request=>Assert.True(IsRootDavProbe(DavPath(fixture,request))));
    }
    [Fact]
    public async Task S3CacheUsesEachTargetsParentForDeletePutMultipartAndCopyButResetsWithNewSource()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/child/",[]);fixture.Put("remote/other/",[]);fixture.Put("remote/child/delete",[8]);var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="PUT"&&IsRootS3Probe(S3Key(fixture,request))?Task.FromResult(S3Error(403,"AccessDenied")):original(request);
        await using var source=fixture.Source();var before=await source.GetMetadataAsync("child/delete");await source.DeleteAsync(before.Path,before.Version);
        var entry=await source.UploadAsync("child/file",new MemoryStream(new byte[9*1024*1024]),null);await source.MoveAsync(entry.Path,"other/moved",entry.Version);await Drain(source);
        var puts=fixture.Server.Requests.Where(request=>request.Method=="PUT"&&IsProbe(S3Key(fixture,request))).Select(request=>S3Key(fixture,request)).ToArray();Assert.Single(puts,IsRootS3Probe);
        Assert.Contains(puts,key=>key.StartsWith("remote/child/.hybridmount-probe-multipart-",StringComparison.Ordinal));Assert.Contains(puts,key=>key.StartsWith("remote/other/.hybridmount-probe-",StringComparison.Ordinal)&&key.EndsWith("-destination",StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Objects.Keys,key=>IsProbe(key));
        await using var next=fixture.Source();await next.UploadAsync("child/next",Stream.Null,null);Assert.Equal(2,fixture.Server.Requests.Count(request=>request.Method=="PUT"&&IsRootS3Probe(S3Key(fixture,request))));
    }
    [Fact]
    public async Task DavCacheUsesEachTargetsParentForDeletePutAndMoveButResetsWithNewSource()
    {
        await using var fixture=new DavFixture();fixture.Objects["child"]=new([],"\"child\"",true);fixture.Objects["other"]=new([],"\"other\"",true);fixture.Put("child/delete",[8]);var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture,request))?Task.FromResult(new HttpTestServer.Response(403,[])):original(request);
        await using var source=fixture.Source();var before=await source.GetMetadataAsync("child/delete");await source.DeleteAsync(before.Path,before.Version);
        var entry=await source.UploadAsync("child/file",new MemoryStream([1]),null);await Drain(source);await source.MoveAsync(entry.Path,"other/moved",entry.Version);await Drain(source);
        var puts=fixture.Server.Requests.Where(request=>request.Method=="PUT"&&IsProbe(DavPath(fixture,request))).Select(request=>DavPath(fixture,request)).ToArray();Assert.Single(puts,IsRootDavProbe);Assert.Contains(puts,path=>path.StartsWith("other/hybridmount-probe-",StringComparison.Ordinal));Assert.DoesNotContain(fixture.Objects.Keys,IsProbe);
        await using var next=fixture.Source();await next.UploadAsync("child/next",Stream.Null,null);Assert.Equal(2,fixture.Server.Requests.Count(request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture,request))));
    }
    [Fact]
    public async Task DavCreateMoveProbesUseCachedWritableParent()
    {
        await using var fixture=new DavFixture{IgnoreConditions=true};fixture.Objects["child"]=new([],"\"child\"",true);var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture,request))?Task.FromResult(new HttpTestServer.Response(403,[])):original(request);
        await using var source=fixture.Source();await source.UploadAsync("child/file",new MemoryStream([1]),null);await Drain(source);Assert.Equal(new byte[]{1},fixture.Objects["child/file"].Bytes);
        Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture,request)));Assert.Contains(fixture.Server.Requests,request=>request.Method=="MOVE"&&DavPath(fixture,request).StartsWith("child/hybridmount-probe-",StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Objects.Keys,IsProbe);
    }
    [Fact]
    public async Task DavLockProbesUseCachedWritableParent()
    {
        await using var fixture=new DavLockFixture{RawTags=true};fixture.Dav.Objects["child"]=new([],"\"child\"",true);fixture.Dav.Put("child/file",[1]);var original=fixture.Dav.Server.Handler;
        fixture.Dav.Server.Handler=request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture.Dav,request))?Task.FromResult(new HttpTestServer.Response(403,[])):original(request);
        await using var source=fixture.Dav.Source();var before=await source.GetMetadataAsync("child/file");await source.UploadWithPreconditionAsync(before.Path,new MemoryStream([2]),new(before.Version,Convert.ToHexString(SHA256.HashData(new byte[]{1}))));
        Assert.Equal(new byte[]{2},fixture.Dav.Objects["child/file"].Bytes);Assert.Equal(CapabilitySupport.Supported,source.WriteCapabilities.ExclusiveLock);Assert.Empty(fixture.Locks);
        Assert.Single(fixture.Dav.Server.Requests,request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture.Dav,request)));Assert.All(fixture.Dav.Server.Requests.Where(request=>request.Method=="LOCK"&&IsProbe(DavPath(fixture.Dav,request))),request=>Assert.StartsWith("child/",DavPath(fixture.Dav,request)));
    }
    [Fact]
    public async Task S3FallbackCollisionIsNotClaimedOrDeleted()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/child/",[]);var original=fixture.Server.Handler;string? collision=null;
        fixture.Server.Handler=request=>{var key=S3Key(fixture,request);if(request.Method=="PUT"&&IsRootS3Probe(key))return Task.FromResult(S3Error(403,"AccessDenied"));if(request.Method=="PUT"&&IsProbe(key)&&collision is null){collision=key;fixture.Put(key,[9]);}return original(request);};
        await using var source=fixture.Source();await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("child/file",Stream.Null,null));Assert.NotNull(collision);Assert.False(S3Tracked(source,collision));Assert.Equal(new byte[]{9},fixture.Objects[collision].Bytes);
        Assert.Contains(await All(source.ListAsync("child")),entry=>entry.Path==collision[7..]);Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&S3Key(fixture,request)==collision);
    }
    [Fact]
    public async Task DavFallbackCollisionIsNotClaimedOrDeleted()
    {
        await using var fixture=new DavFixture();fixture.Objects["child"]=new([],"\"child\"",true);var original=fixture.Server.Handler;string? collision=null;
        fixture.Server.Handler=request=>{var path=DavPath(fixture,request);if(request.Method=="PUT"&&IsRootDavProbe(path))return Task.FromResult(new HttpTestServer.Response(403,[]));if(request.Method=="PUT"&&IsProbe(path)&&collision is null){collision=path;fixture.Put(path,[9]);}return original(request);};
        await using var source=fixture.Source();await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("child/file",Stream.Null,null));Assert.NotNull(collision);Assert.Equal(new byte[]{9},fixture.Objects[collision].Bytes);
        Assert.Contains(await All(source.ListAsync("child")),entry=>entry.Path==collision);Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&DavPath(fixture,request)==collision);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task DavReadPermissionFailureDoesNotTriggerWriteFallback(bool denyPreflight)
    {
        await using var fixture=new DavFixture();fixture.Objects["child"]=new([],"\"child\"",true);await using var source=fixture.Source();var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="PROPFIND"&&IsRootDavProbe(DavPath(fixture,request))&&denyPreflight?Task.FromResult(new HttpTestServer.Response(403,[])):original(request);
        if(denyPreflight){await Assert.ThrowsAsync<SourceRequestRejectedException>(()=>source.UploadAsync("child/file",Stream.Null,null));Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="PUT");}
        fixture.Server.Handler=original;await source.UploadAsync("child/retry",Stream.Null,null);Assert.DoesNotContain(fixture.Server.Requests,request=>IsProbe(DavPath(fixture,request))&&!IsRootDavProbe(DavPath(fixture,request)));
    }
    [Fact]
    public async Task S3RootTargetCannotRetrySameDeniedRootButLaterNestedTargetUsesCache()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/child/",[]);var original=fixture.Server.Handler;fixture.Server.Handler=request=>request.Method=="PUT"&&IsRootS3Probe(S3Key(fixture,request))?Task.FromResult(S3Error(403,"AccessDenied")):original(request);
        await using var source=fixture.Source();await Assert.ThrowsAsync<SourceConnectionException>(()=>source.UploadAsync("file",Stream.Null,null));await Assert.ThrowsAsync<SourceConnectionException>(()=>source.UploadAsync("retry",Stream.Null,null));
        await source.UploadAsync("child/file",Stream.Null,null);Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT"&&IsRootS3Probe(S3Key(fixture,request)));
    }
    [Fact]
    public async Task DavRootTargetCannotRetrySameDeniedRootButLaterNestedTargetUsesCache()
    {
        await using var fixture=new DavFixture();fixture.Objects["child"]=new([],"\"child\"",true);var original=fixture.Server.Handler;fixture.Server.Handler=request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture,request))?Task.FromResult(new HttpTestServer.Response(403,[])):original(request);
        await using var source=fixture.Source();await Assert.ThrowsAsync<SourceRequestRejectedException>(()=>source.UploadAsync("file",Stream.Null,null));await Assert.ThrowsAsync<SourceRequestRejectedException>(()=>source.UploadAsync("retry",Stream.Null,null));
        await source.UploadAsync("child/file",Stream.Null,null);Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture,request)));
    }
    [Fact]
    public async Task S3MultipartRootTransientKeepsExistingNoFallbackOrDeleteBehavior()
    {
        await using var fixture=new S3Fixture();var original=fixture.Server.Handler;fixture.Server.Handler=request=>request.Method=="PUT"&&IsRootS3Probe(S3Key(fixture,request))?Task.FromResult(S3Error(503,"ServiceUnavailable")):original(request);
        await using var source=fixture.Source();await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>(Task)Call(source,"VerifyMultipartAsync","remote/child/",CancellationToken.None)!);
        var request=Assert.Single(fixture.Server.Requests);Assert.Equal("PUT",request.Method);Assert.StartsWith("remote/.hybridmount-probe-multipart-",S3Key(fixture,request));
    }
    [Theory]
    [InlineData(403)][InlineData(503)]
    public async Task S3FallbackFailureDoesNotTryAnotherLocationOrForgetRootRejection(int fallbackStatus)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/child/",[]);var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="PUT"&&IsProbe(S3Key(fixture,request))?Task.FromResult(S3Error(IsRootS3Probe(S3Key(fixture,request))?403:fallbackStatus,fallbackStatus==503&&!IsRootS3Probe(S3Key(fixture,request))?"ServiceUnavailable":"AccessDenied")):original(request);
        await using var source=fixture.Source();var error=await Record.ExceptionAsync(()=>source.UploadAsync("child/file",Stream.Null,null));if(fallbackStatus==503)Assert.IsType<SourceOutcomeUnknownException>(error);else Assert.IsType<SourceConnectionException>(error);
        Assert.Equal(2,fixture.Server.Requests.Count(request=>request.Method=="PUT"));Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE");
        fixture.Server.Handler=original;await source.UploadAsync("child/retry",Stream.Null,null);Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT"&&IsRootS3Probe(S3Key(fixture,request)));
    }
    [Theory]
    [InlineData(403)][InlineData(503)]
    public async Task DavFallbackFailureDoesNotTryAnotherLocationOrForgetRootRejection(int fallbackStatus)
    {
        await using var fixture=new DavFixture();fixture.Objects["child"]=new([],"\"child\"",true);var original=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="PUT"&&IsProbe(DavPath(fixture,request))?Task.FromResult(new HttpTestServer.Response(IsRootDavProbe(DavPath(fixture,request))?403:fallbackStatus,[])):original(request);
        await using var source=fixture.Source();var error=await Record.ExceptionAsync(()=>source.UploadAsync("child/file",Stream.Null,null));if(fallbackStatus==503)Assert.IsType<RemoteSyncException>(error);else Assert.IsType<SourceRequestRejectedException>(error);
        Assert.Equal(2,fixture.Server.Requests.Count(request=>request.Method=="PUT"));Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE");
        fixture.Server.Handler=original;await source.UploadAsync("child/retry",Stream.Null,null);Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT"&&IsRootDavProbe(DavPath(fixture,request)));
    }
    private static bool IsProbe(string path)=>path.Contains("hybridmount-probe-",StringComparison.Ordinal);
    private static bool IsRootS3Probe(string key)=>key.StartsWith("remote/.hybridmount-probe-",StringComparison.Ordinal);
    private static bool IsRootDavProbe(string path)=>path.StartsWith("hybridmount-probe-",StringComparison.Ordinal);
    private static string S3Key(S3Fixture fixture,HttpTestServer.Request request)=>Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath)[13..];
    private static string DavPath(DavFixture fixture,HttpTestServer.Request request)=>Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath)[5..].TrimEnd('/');
    private static HttpTestServer.Response S3Error(int status,string code)=>HttpTestServer.Response.Text(status,"<Error><Code>"+code+"</Code></Error>");
    /// <summary>等待DAV独立后台清理完成再检查零残留，不改变失败／冲突及内容断言。</summary>
    private static Task Drain(IFileSource source)=>source is WebDavFileSource dav?dav.DrainTemporaryCleanupAsync():Task.CompletedTask;
    private static object? Call(object source,string name,params object?[] args)=>source.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(source,args);
    private static bool S3Tracked(S3FileSource source,string key)=>((ConcurrentDictionary<string,byte>)typeof(S3FileSource).GetField("_sessionProbeKeys",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(source)!).ContainsKey(key);
    private static async Task<List<SourceEntry>> All(IAsyncEnumerable<SourceEntry> entries){var result=new List<SourceEntry>();await foreach(var entry in entries)result.Add(entry);return result;}
    private static async Task<List<RemoteHintChange>> Hints(S3FileSource source,string path){var result=new List<RemoteHintChange>();await foreach(var hint in source.ReadHintChangesAsync(new HintJournal([new("created",path),new("deleted",path)]),Guid.Empty,"scope",default))result.Add(hint);return result;}
    private sealed class HintJournal(IReadOnlyList<RemotePathHint> hints):IRemoteHintJournal{public IReadOnlyList<RemotePathHint> PendingRemoteHints(Guid mount,string scope)=>hints;}
}
