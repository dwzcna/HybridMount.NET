using System.Text.Json;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class S3ListEfficiencyTests
{
    private static readonly XNamespace Ns="http://s3.amazonaws.com/doc/2006-03-01/";
    [Fact]
    public async Task ThousandObjectsUseOneListAndNoHeadsWithCompatibleVersions()
    {
        await using var fixture=new S3Fixture();fixture.Objects.Clear();for(var i=0;i<1000;i++)fixture.Put($"remote/file-{i:D4}",[1,2,3]);
        var handle=fixture.Server.Handler;fixture.Server.Handler=request=>request.Target.Contains("list-type=")?
            Task.FromResult(Page(fixture.Objects.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>Item(pair.Key,3,pair.Value.ETag)))):handle(request);
        await using var source=fixture.Source();var entries=await WebDavTests.All(source.ListAsync(""));Assert.Equal(1000,entries.Count);
        var request=Assert.Single(fixture.Server.Requests);Assert.Equal("GET",request.Method);Assert.Contains("list-type=2",request.Target);
        var listed=entries[0];Assert.Null(JsonDocument.Parse(listed.Version).RootElement.GetProperty("VersionId").GetString());
        var head=await source.GetMetadataAsync(listed.Path);Assert.True(source.IsSameVersion(listed.Version,head.Version));Assert.True(source.IsSameVersion(head.Version,listed.Version));
        var bytes=new byte[3];Assert.Equal(3,await source.ReadRangeAsync(listed,0,bytes));Assert.Equal(new byte[]{1,2,3},bytes);
    }
    [Fact]
    public async Task BadItemsAreIsolatedAndDiagnosticsContainOnlyReasonCodes()
    {
        await using var fixture=new S3Fixture();fixture.Server.Handler=_=>Task.FromResult(Page([
            Item("remote/Good",1),Item("remote/good",1),Item("remote/CON",1),Item("remote/bad:name",1),Item("remote/clash",1),
            new XElement(Ns+"CommonPrefixes",new XElement(Ns+"Prefix","remote/clash/")),Item("remote/nonempty/",1),
            Item("remote/deep/child",1),Item("foreign/outside",1),Item("remote/missing",1,null),Item("remote/last",1)]));
        await using var source=fixture.Source();var diagnostics=new List<SourceReadDiagnostic>();source.ReadDiagnostic+=diagnostics.Add;
        var entries=await WebDavTests.All(source.ListAsync(""));Assert.Equal(new[]{"Good","clash","last"},entries.Select(item=>item.Path));
        Assert.Equal(8,diagnostics.Count);Assert.All(diagnostics,item=>{Assert.Equal("list",item.Strategy);Assert.False(item.Completed);Assert.DoesNotContain("remote",item.FallbackReason!);});
        Assert.Contains(diagnostics,item=>item.FallbackReason=="case_collision");Assert.Contains(diagnostics,item=>item.FallbackReason=="item_collision");Assert.Single(fixture.Server.Requests);
    }
    [Fact]
    public async Task MarkersAndCommonPrefixesKeepDirectoryTypesWithoutHeads()
    {
        await using var fixture=new S3Fixture();fixture.Server.Handler=_=>Task.FromResult(Page([
            Item("remote/",0),Item("remote/marker/",0),new XElement(Ns+"CommonPrefixes",new XElement(Ns+"Prefix","remote/marker/")),
            new XElement(Ns+"CommonPrefixes",new XElement(Ns+"Prefix","remote/virtual/"))]));
        await using var source=fixture.Source();var entries=await WebDavTests.All(source.ListAsync(""));Assert.Equal(2,entries.Count);Assert.All(entries,item=>{Assert.True(item.IsDirectory);Assert.Equal(0,item.Size);});
        Assert.False(JsonDocument.Parse(entries.Single(item=>item.Path=="marker").Version).RootElement.GetProperty("Virtual").GetBoolean());
        Assert.True(JsonDocument.Parse(entries.Single(item=>item.Path=="virtual").Version).RootElement.GetProperty("Virtual").GetBoolean());Assert.Single(fixture.Server.Requests);
    }
    [Theory]
    [InlineData("missing")][InlineData("repeated")][InlineData("completion")]
    public async Task PaginationSafetyStillFailsWithoutPublishingEntries(string failure)
    {
        await using var fixture=new S3Fixture();var requests=0;fixture.Server.Handler=_=>
        {
            requests++;return Task.FromResult(Page([Item($"remote/file-{requests}",1)],failure!="completion",failure=="repeated"?"same":null,failure!="completion"));
        };
        await using var source=fixture.Source();var published=new List<SourceEntry>();
        await Assert.ThrowsAsync<IOException>(async()=>{await foreach(var entry in source.ListAsync(""))published.Add(entry);});Assert.Empty(published);Assert.Equal(failure=="repeated"?2:1,requests);
    }
    [Fact]
    public async Task ChangedObjectDoesNotBreakListingButRangeDetectsDrift()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var entries=await WebDavTests.All(source.ListAsync(""));var entry=entries.Single(item=>item.Path=="文件.bin");
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="HEAD");fixture.Put("remote/文件.bin",[4,5,6]);
        await Assert.ThrowsAsync<SourceConflictException>(async()=>await source.ReadRangeAsync(entry,0,new byte[3]));
    }
    [Fact]
    public async Task EmptyListingReturnsNoEntries()
    {
        await using var fixture=new S3Fixture();fixture.Server.Handler=_=>Task.FromResult(Page([]));await using var source=fixture.Source();Assert.Empty(await WebDavTests.All(source.ListAsync("")));Assert.Single(fixture.Server.Requests);
    }
    [Fact]
    public async Task VersionedListEntriesSupportUpdateMoveAndDeleteWithFreshCopyVersion()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/update.bin",[1]);fixture.Put("remote/move.bin",[2]);fixture.Put("remote/delete.bin",[3]);
        await using var source=fixture.Source();var entries=(await WebDavTests.All(source.ListAsync(""))).ToDictionary(item=>item.Path);var version=fixture.Objects["remote/move.bin"].VersionId;
        var updated=await source.UploadAsync("update.bin",new MemoryStream([4]),entries["update.bin"].Version);Assert.Equal(1,updated.Size);
        await source.MoveAsync("move.bin","moved.bin",entries["move.bin"].Version);await source.DeleteAsync("delete.bin",entries["delete.bin"].Version);
        Assert.Equal(new byte[]{4},fixture.Objects["remote/update.bin"].Bytes);Assert.Equal(new byte[]{2},fixture.Objects["remote/moved.bin"].Bytes);Assert.False(fixture.Objects.ContainsKey("remote/move.bin"));Assert.False(fixture.Objects.ContainsKey("remote/delete.bin"));
        Assert.Contains(fixture.Server.Requests,r=>r.Headers.TryGetValue("x-amz-copy-source",out var value)&&value.Contains("move.bin",StringComparison.Ordinal)&&value.Contains("versionId="+version,StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE"&&r.Target.Contains("versionId=",StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("update")][InlineData("move")][InlineData("delete")]
    public async Task ListEntryEtagMismatchStillBlocksMutation(string operation)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var entry=(await WebDavTests.All(source.ListAsync(""))).Single(item=>item.Path=="文件.bin");fixture.Put("remote/文件.bin",[9]);
        await Assert.ThrowsAsync<SourceConflictException>(async()=>
        {
            if(operation=="update")await source.UploadAsync(entry.Path,new MemoryStream([1]),entry.Version);
            else if(operation=="move")await source.MoveAsync(entry.Path,"moved.bin",entry.Version);else await source.DeleteAsync(entry.Path,entry.Version);
        });Assert.Equal(new byte[]{9},fixture.Objects["remote/文件.bin"].Bytes);Assert.False(fixture.Objects.ContainsKey("remote/moved.bin"));
    }
    [Theory]
    [InlineData(null,"v1",false,false,"\"tag\"",true)]
    [InlineData("v1","v1",false,false,"\"tag\"",true)]
    [InlineData("v1","v2",false,false,"\"tag\"",false)]
    [InlineData(null,null,false,true,"\"tag\"",false)]
    [InlineData(null,null,false,false,"\"other\"",false)]
    [InlineData(null,null,false,false,"W/\"tag\"",false)]
    public async Task VersionComparisonUsesStrongEtagAndAvailableVersionIds(string? leftId,string? rightId,bool leftVirtual,bool rightVirtual,string rightTag,bool expected)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        var left=JsonSerializer.Serialize(new{ETag="\"tag\"",VersionId=leftId,Virtual=leftVirtual});var right=JsonSerializer.Serialize(new{ETag=rightTag,VersionId=rightId,Virtual=rightVirtual});Assert.Equal(expected,source.IsSameVersion(left,right));
    }
    private static XElement Item(string key,long size,string? tag="\"tag\"")=>new(Ns+"Contents",new XElement(Ns+"Key",key),new XElement(Ns+"Size",size),
        new XElement(Ns+"LastModified","2026-10-29T12:00:00Z"),tag is null?null:new XElement(Ns+"ETag",tag));
    private static HttpTestServer.Response Page(IEnumerable<XElement> rows,bool truncated=false,string? next=null,bool completion=true)
        =>HttpTestServer.Response.Text(200,new XElement(Ns+"ListBucketResult",new XElement(Ns+"Name","test-bucket"),completion?new XElement(Ns+"IsTruncated",truncated.ToString().ToLowerInvariant()):null,
            next is null?null:new XElement(Ns+"NextContinuationToken",next),rows).ToString(SaveOptions.DisableFormatting));
}
