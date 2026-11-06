using System.Text.Json;
using HybridMount.Common;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class S3MutationVersionTests
{
    private static string Version(string? etag="\"tag\"",string? id="version",bool virtualDirectory=false)=>JsonSerializer.Serialize(new{ETag=etag,VersionId=id,Virtual=virtualDirectory});
    [Theory]
    [InlineData(null,"version",true)][InlineData("version",null,true)]
    [InlineData(null,null,true)][InlineData("version","version",true)]
    [InlineData("first","second",false)][InlineData("Version","version",false)]
    [InlineData("null","version",true)][InlineData("","version",true)]
    public async Task OptionalVersionIdsUseStrongEtagUnlessBothAreAvailable(string? left,string? right,bool same)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();IFileSource contract=source;
        Assert.Equal(same,contract.IsSameVersion(Version(id:left),Version(id:right)));Assert.Equal(same,contract.IsSameVersion(Version(id:right),Version(id:left)));
        Assert.False(contract.IsSameVersion(Version(id:left),Version(etag:"\"different\"",id:right)));
    }
    [Theory]
    [InlineData("")][InlineData("not-json")][InlineData("null")][InlineData("{}")]
    [InlineData("{\"ETag\":null}")][InlineData("{\"ETag\":\"W/\\\"tag\\\"\"}")]
    [InlineData("{\"ETag\":\"*\"}")][InlineData("{\"ETag\":\"unquoted\"}")]
    public async Task InvalidOrMissingValidatorsNeverCompareAsObjectVersions(string value)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();Assert.False(source.IsSameVersion(value,value));Assert.False(source.IsSameVersion(value,Version()));Assert.False(source.IsSameVersion(Version(),value));
    }
    [Fact]
    public async Task JsonFormattingIsNotVersionIdentityAndVirtualDirectoriesStayDistinct()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        Assert.True(source.IsSameVersion(Version(),"{ \"Virtual\": false, \"VersionId\": \"version\", \"ETag\": \"\\\"tag\\\"\" }"));
        Assert.True(source.IsSameVersion(Version(null,null,true),"{\"Virtual\":true}"));Assert.False(source.IsSameVersion(Version(),Version(null,null,true)));
    }
    [Theory]
    [InlineData(false,false,true)][InlineData(false,true,false)][InlineData(false,false,false)]
    [InlineData(true,false,true)][InlineData(true,true,false)][InlineData(true,false,false)]
    public async Task MoveAndDeleteAllowHeadVersionIdToAppearOrDisappear(bool move,bool originalHasId,bool currentHasId)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/source.bin",[1,2,3]);var handler=fixture.Server.Handler;var heads=0;
        fixture.Server.Handler=async request=>
        {
            var response=await handler(request);
            if(IsSource(fixture,request)&&request.Method=="HEAD")
            {
                var number=++heads;var expose=number==1?originalHasId:number==2?currentHasId:!currentHasId;if(!expose)response.Headers?.Remove("x-amz-version-id");
            }
            return response;
        };
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("source.bin");var tag=fixture.Objects["remote/source.bin"].ETag;
        if(move){var result=await source.MoveAsync(entry.Path,"moved.bin",entry.Version);Assert.Equal(3,result.Size);Assert.Equal(new byte[]{1,2,3},fixture.Objects["remote/moved.bin"].Bytes);}
        else await source.DeleteAsync(entry.Path,entry.Version);
        Assert.False(fixture.Objects.ContainsKey("remote/source.bin"));Assert.Equal(move?3:2,heads);
        var deletion=Assert.Single(fixture.Server.Requests,request=>IsSource(fixture,request)&&request.Method=="DELETE");Assert.Equal(tag,deletion.Headers.GetValueOrDefault("If-Match"));Assert.DoesNotContain("versionId=",deletion.Target,StringComparison.Ordinal);
        if(move){var copy=Assert.Single(fixture.Server.Requests,request=>request.Method=="PUT"&&new Uri(fixture.Server.Address,request.Target).AbsolutePath=="/test-bucket/remote/moved.bin");Assert.Equal(tag,copy.Headers.GetValueOrDefault("x-amz-copy-source-if-match"));Assert.Equal("*",copy.Headers.GetValueOrDefault("If-None-Match"));}
    }
    [Theory]
    [InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task ChangedEtagOrTwoDifferentVersionIdsRejectBeforeUserMutation(bool move,bool changeEtag)
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/source.bin",[1,2,3]);await using var source=fixture.Source();var entry=await source.GetMetadataAsync("source.bin");
        if(changeEtag)fixture.Put("remote/source.bin",[9,8,7]);
        else{var original=fixture.Objects["remote/source.bin"];var replacement=original with{VersionId="concurrent-version"};fixture.Objects["remote/source.bin"]=replacement;fixture.Versions[("remote/source.bin",replacement.VersionId)]=replacement;}
        if(move)await Assert.ThrowsAsync<SourceConflictException>(()=>source.MoveAsync(entry.Path,"moved.bin",entry.Version));else await Assert.ThrowsAsync<SourceConflictException>(()=>source.DeleteAsync(entry.Path,entry.Version));
        Assert.True(fixture.Objects.ContainsKey("remote/source.bin"));Assert.False(fixture.Objects.ContainsKey("remote/moved.bin"));Assert.DoesNotContain(fixture.Server.Requests,request=>IsSource(fixture,request)&&request.Method=="DELETE");
    }
    [Fact]
    public async Task SourceChangedAfterCopyStillCannotBeDeleted()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/source.bin",[1,2,3]);var handler=fixture.Server.Handler;
        fixture.Server.Handler=async request=>{var response=await handler(request);if(request.Method=="PUT"&&new Uri(fixture.Server.Address,request.Target).AbsolutePath=="/test-bucket/remote/moved.bin")fixture.Put("remote/source.bin",[9,8,7]);return response;};
        await using var source=fixture.Source();var entry=await source.GetMetadataAsync("source.bin");await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync(entry.Path,"moved.bin",entry.Version));
        Assert.Equal(new byte[]{9,8,7},fixture.Objects["remote/source.bin"].Bytes);Assert.Equal(new byte[]{1,2,3},fixture.Objects["remote/moved.bin"].Bytes);Assert.DoesNotContain(fixture.Server.Requests,request=>IsSource(fixture,request)&&request.Method=="DELETE");
    }
    private static bool IsSource(S3Fixture fixture,HttpTestServer.Request request)=>new Uri(fixture.Server.Address,request.Target).AbsolutePath=="/test-bucket/remote/source.bin";
}
