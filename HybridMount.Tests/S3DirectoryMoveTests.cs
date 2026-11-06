using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class S3DirectoryMoveTests
{
    [Theory]
    [InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task ExistingDestinationDirectoriesAreIdempotentAndKeepUnrelatedContents(bool markedSource,bool markedTarget)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/sub/file",[1,2,3]);fixture.Put("remote/tree/empty/",[]);fixture.Put("remote/destination/sub/unrelated",[9]);
        if(markedSource){fixture.Put("remote/tree/",[]);fixture.Put("remote/tree/sub/",[]);}
        if(markedTarget){fixture.Put("remote/destination/",[]);fixture.Put("remote/destination/sub/",[]);}
        await using var source=fixture.Source();var warnings=new List<string>();source.WriteWarning+=warnings.Add;var entry=await source.GetMetadataAsync("tree");var target=await source.MoveAsync("tree","destination",entry.Version);
        Assert.True(target.IsDirectory);Assert.Equal("destination",target.Path);Assert.Equal(new byte[]{1,2,3},fixture.Objects["remote/destination/sub/file"].Bytes);Assert.Equal(new byte[]{9},fixture.Objects["remote/destination/sub/unrelated"].Bytes);Assert.True(fixture.Objects.ContainsKey("remote/destination/empty/"));Assert.DoesNotContain(fixture.Objects.Keys,key=>key.StartsWith("remote/tree/",StringComparison.Ordinal));Assert.Empty(warnings);
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="PUT"&&Key(fixture,request) is "remote/destination/" or "remote/destination/sub/");
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task VirtualDirectoriesDoNotCreateDestinationMarkers(bool fallback)
    {
        await using var fixture=new S3Fixture{IgnoreCopyConditions=fallback};fixture.Put("remote/tree/sub/file",[1]);await using var source=fixture.Source();var entry=await source.GetMetadataAsync("tree");await source.MoveAsync("tree","destination",entry.Version);
        Assert.Equal(new byte[]{1},fixture.Objects["remote/destination/sub/file"].Bytes);Assert.False(fixture.Objects.ContainsKey("remote/destination/"));Assert.False(fixture.Objects.ContainsKey("remote/destination/sub/"));Assert.True((await source.GetMetadataAsync("destination")).IsDirectory);
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="PUT"&&Key(fixture,request) is "remote/destination/" or "remote/destination/sub/");
        var write=Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT"&&Key(fixture,request)=="remote/destination/sub/file");Assert.Equal("*",write.Headers.GetValueOrDefault("If-None-Match"));Assert.Equal(!fallback,write.Headers.ContainsKey("x-amz-copy-source"));
    }
    [Fact]
    public async Task DisappearingVirtualContentsCannotReturnANonexistentTarget()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/file",[1]);await using var source=fixture.Source();var entry=await source.GetMetadataAsync("tree");var handler=fixture.Server.Handler;
        fixture.Server.Handler=request=>{if(request.Method=="GET"&&request.Target.Contains("delimiter=",StringComparison.Ordinal)&&Uri.UnescapeDataString(request.Target).Contains("remote/tree/",StringComparison.Ordinal))fixture.Objects.TryRemove("remote/tree/file",out _);return handler(request);};
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.MoveAsync("tree","destination",entry.Version));Assert.DoesNotContain(fixture.Objects.Keys,key=>key.StartsWith("remote/destination",StringComparison.Ordinal));
    }
    [Fact]
    public async Task EmptyMarkedDirectoryStillCreatesItsMarkerAndDeletesSourceConditionally()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/",[]);var tag=fixture.Objects["remote/tree/"].ETag;await using var source=fixture.Source();var entry=await source.GetMetadataAsync("tree");var target=await source.MoveAsync("tree","destination",entry.Version);
        Assert.True(target.IsDirectory);Assert.True(fixture.Objects.ContainsKey("remote/destination/"));Assert.False(fixture.Objects.ContainsKey("remote/tree/"));var deletion=Assert.Single(fixture.Server.Requests,request=>request.Method=="DELETE"&&Key(fixture,request)=="remote/tree/");Assert.Equal(tag,deletion.Headers.GetValueOrDefault("If-Match"));Assert.DoesNotContain("versionId=",deletion.Target,StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ExistingFileNeverCountsAsDestinationDirectory(bool child)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/sub/file",[1]);fixture.Put(child?"remote/destination/sub":"remote/destination",[9]);await using var source=fixture.Source();var entry=await source.GetMetadataAsync("tree");
        var error=await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync("tree","destination",entry.Version));Assert.Contains(error.Results,item=>item.Status=="Failed");Assert.True(fixture.Objects.ContainsKey("remote/tree/sub/file"));Assert.Equal(new byte[]{9},fixture.Objects[child?"remote/destination/sub":"remote/destination"].Bytes);Assert.False(fixture.Objects.ContainsKey("remote/destination/sub/file"));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ConcurrentDirectoryCreationIsAcceptedButFileRaceIsNot(bool file)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/",[]);fixture.Put("remote/tree/file",[1]);var handler=fixture.Server.Handler;var raced=false;
        fixture.Server.Handler=request=>{if(request.Method=="PUT"&&Key(fixture,request)=="remote/destination/"){raced=true;fixture.Put(file?"remote/destination":"remote/destination/",file?[9]:[]);}return handler(request);};
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("tree");
        if(file){await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync("tree","destination",entry.Version));Assert.True(fixture.Objects.ContainsKey("remote/tree/file"));Assert.Equal(new byte[]{9},fixture.Objects["remote/destination"].Bytes);}
        else{var target=await source.MoveAsync("tree","destination",entry.Version);Assert.True(target.IsDirectory);Assert.Equal(new byte[]{1},fixture.Objects["remote/destination/file"].Bytes);Assert.False(fixture.Objects.ContainsKey("remote/tree/file"));}
        Assert.True(raced);Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT"&&Key(fixture,request)=="remote/destination/");
    }
    [Theory]
    [InlineData("Denied")][InlineData("NonEmpty")][InlineData("ChangedMarker")][InlineData("ChangedType")][InlineData("VirtualNonEmpty")][InlineData("Canceled")]
    public async Task CompletedChildMovesReturnTargetAndWarnSeparatelyAboutSourceCleanup(string failure)
    {
        await using var fixture=new S3Fixture();if(failure!="VirtualNonEmpty")fixture.Put("remote/tree/",[]);fixture.Put("remote/tree/file",[1,2,3]);var handler=fixture.Server.Handler;using var cancel=new CancellationTokenSource();var changed=false;
        if(failure=="Denied")fixture.DeleteFailKey="remote/tree/";
        fixture.Server.Handler=async request=>
        {
            if(failure=="Canceled"&&changed&&request.Method=="HEAD"&&Key(fixture,request)=="remote/tree")cancel.Cancel();
            var response=await handler(request);
            if(request.Method=="DELETE"&&Key(fixture,request)=="remote/tree/file"&&response.Status==204)
            {
                changed=true;
                if(failure is "NonEmpty" or "VirtualNonEmpty")fixture.Put("remote/tree/late",[7]);
                if(failure=="ChangedMarker")fixture.Put("remote/tree/",[]);
                if(failure=="ChangedType"){fixture.Objects.TryRemove("remote/tree/",out _);fixture.Put("remote/tree",[8]);}
            }
            return response;
        };
        await using var source=fixture.Source();var warnings=new List<string>();source.WriteWarning+=warnings.Add;var entry=await source.GetMetadataAsync("tree");var target=await source.MoveAsync("tree","destination",entry.Version,cancel.Token);
        Assert.True(changed);Assert.True(target.IsDirectory);Assert.Equal(new byte[]{1,2,3},fixture.Objects["remote/destination/file"].Bytes);Assert.False(fixture.Objects.ContainsKey("remote/tree/file"));Assert.Contains(warnings,message=>message.Contains("tree",StringComparison.Ordinal)&&message.Contains("destination",StringComparison.Ordinal));
        if(failure is "NonEmpty" or "VirtualNonEmpty")Assert.Equal(new byte[]{7},fixture.Objects["remote/tree/late"].Bytes);
        else if(failure=="ChangedType")Assert.Equal(new byte[]{8},fixture.Objects["remote/tree"].Bytes);
        else Assert.True(fixture.Objects.ContainsKey("remote/tree/"));
        if(failure is "ChangedMarker" or "ChangedType" or "NonEmpty" or "VirtualNonEmpty")Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&Key(fixture,request)=="remote/tree/");
    }
    [Fact]
    public async Task CleanupFailureDoesNotPreventOtherEmptySourceMarkersFromBeingRemoved()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/",[]);fixture.Put("remote/tree/one/",[]);fixture.Put("remote/tree/two/",[]);fixture.DeleteFailKey="remote/tree/one/";await using var source=fixture.Source();var warnings=new List<string>();source.WriteWarning+=warnings.Add;var entry=await source.GetMetadataAsync("tree");await source.MoveAsync("tree","destination",entry.Version);
        Assert.True(fixture.Objects.ContainsKey("remote/tree/one/"));Assert.False(fixture.Objects.ContainsKey("remote/tree/two/"));Assert.True(fixture.Objects.ContainsKey("remote/tree/"));Assert.Contains(warnings,message=>message.Contains("tree/one",StringComparison.Ordinal));Assert.True(fixture.Objects.ContainsKey("remote/destination/one/"));Assert.True(fixture.Objects.ContainsKey("remote/destination/two/"));
    }
    [Fact]
    public async Task FailedFileMoveStillThrowsPartialResultsAndKeepsSource()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/file",[1]);fixture.DeleteFailKey="remote/tree/file";await using var source=fixture.Source();var warnings=new List<string>();source.WriteWarning+=warnings.Add;var entry=await source.GetMetadataAsync("tree");var error=await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync("tree","destination",entry.Version));
        Assert.Contains(error.Results,item=>item.Path=="tree/file"&&item.Status=="Copied");Assert.Contains(error.Results,item=>item.Status=="Failed");Assert.True(fixture.Objects.ContainsKey("remote/tree/file"));Assert.True(fixture.Objects.ContainsKey("remote/destination/file"));Assert.Empty(warnings);
    }
    [Fact]
    public async Task WarningObserverCannotTurnCompletedMoveIntoFailure()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/",[]);fixture.DeleteFailKey="remote/tree/";await using var source=fixture.Source();source.WriteWarning+=_=>throw new InvalidOperationException("observer");var entry=await source.GetMetadataAsync("tree");var target=await source.MoveAsync("tree","destination",entry.Version);Assert.True(target.IsDirectory);Assert.True(fixture.Objects.ContainsKey("remote/tree/"));Assert.True(fixture.Objects.ContainsKey("remote/destination/"));
    }
    [Theory]
    [InlineData("destination/file",true)][InlineData("destination/deep/file",true)]
    [InlineData("destination-other/file",false)][InlineData("Destination/file",false)]
    [InlineData("elsewhere/file",false)][InlineData("outside/destination/file",false)]
    public async Task MissingParentAllowanceUsesOrdinalDestinationBoundary(string path,bool allowed)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        var method=typeof(S3FileSource).GetMethod("CheckTargetAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        Task Check()=>(Task)method.Invoke(source,[path,null,CancellationToken.None,"destination"])!;
        if(allowed)await Check();else await Assert.ThrowsAsync<FileNotFoundException>(Check);
    }
    [Theory]
    [InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task DirectoryMoveRejectsOutsideMissingOrFileParentWithoutCreatingTarget(bool fallback,bool fileParent)
    {
        await using var fixture=new S3Fixture{IgnoreCopyConditions=fallback};fixture.Put("remote/tree/file",[1]);if(fileParent)fixture.Put("remote/outside",[9]);await using var source=fixture.Source();var entry=await source.GetMetadataAsync("tree");
        await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync("tree","outside/destination",entry.Version));Assert.True(fixture.Objects.ContainsKey("remote/tree/file"));Assert.DoesNotContain(fixture.Objects.Keys,key=>key.StartsWith("remote/outside/destination",StringComparison.Ordinal));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task NormalFileMovesAndUploadsStillRejectMissingParents(bool move)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/source",[1]);await using var source=fixture.Source();var entry=await source.GetMetadataAsync("source");
        if(move)await Assert.ThrowsAsync<FileNotFoundException>(()=>source.MoveAsync("source","missing/file",entry.Version));else await Assert.ThrowsAsync<FileNotFoundException>(()=>source.UploadAsync("missing/file",new MemoryStream([1]),null));
        Assert.True(fixture.Objects.ContainsKey("remote/source"));Assert.False(fixture.Objects.ContainsKey("remote/missing/file"));
    }
    [Fact]
    public async Task FallbackRechecksFileParentAfterDownloadingSource()
    {
        await using var fixture=new S3Fixture{IgnoreCopyConditions=true};fixture.Put("remote/tree/file",[1]);var handler=fixture.Server.Handler;
        fixture.Server.Handler=async request=>{var response=await handler(request);if(request.Method=="GET"&&Key(fixture,request)=="remote/tree/file")fixture.Put("remote/destination",[9]);return response;};
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("tree");await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync("tree","destination",entry.Version));
        Assert.Equal(new byte[]{9},fixture.Objects["remote/destination"].Bytes);Assert.True(fixture.Objects.ContainsKey("remote/tree/file"));Assert.False(fixture.Objects.ContainsKey("remote/destination/file"));
    }
    /// <summary>列表不逐目录HEAD，只有移动阶段核实目录标记；嵌套空标记必须保留而虚拟父目录不额外创建标记。</summary>
    [Fact]
    public async Task HeadFreeListingAndMovePreserveNestedExplicitEmptyMarkers()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/empty/",[]);fixture.Put("remote/tree/nested/deep/",[]);
        await using var source=fixture.Source();var children=new List<SourceEntry>();await foreach(var child in source.ListAsync("tree"))children.Add(child);
        Assert.Equal(2,children.Count);Assert.All(children,child=>Assert.True(child.IsDirectory));Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="HEAD");
        var entry=await source.GetMetadataAsync("tree");await source.MoveAsync("tree","destination",entry.Version);
        Assert.True(fixture.Objects.ContainsKey("remote/destination/empty/"));Assert.True(fixture.Objects.ContainsKey("remote/destination/nested/deep/"));
        Assert.False(fixture.Objects.ContainsKey("remote/destination/"));Assert.False(fixture.Objects.ContainsKey("remote/destination/nested/"));
        Assert.DoesNotContain(fixture.Objects.Keys,key=>key.StartsWith("remote/tree/",StringComparison.Ordinal));
        Assert.Contains(fixture.Server.Requests,request=>request.Method=="HEAD"&&Key(fixture,request)=="remote/tree/nested/deep/");
    }
    private static string Key(S3Fixture fixture,HttpTestServer.Request request)=>Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath)["/test-bucket/".Length..];
}
