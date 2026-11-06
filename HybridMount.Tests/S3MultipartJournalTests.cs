using System.Text.Json;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class S3MultipartJournalTests
{
    private static readonly XNamespace Ns="http://s3.amazonaws.com/doc/2006-03-01/";
    [Fact]
    public async Task PendingJournalIsPresentBeforeEveryInitiateAndUpdatedBeforeParts()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var handle=fixture.Server.Handler;var pending=0;var initiated=0;var parts=0;
        source.MultipartJournalPending=path=>
        {
            using var json=JsonDocument.Parse(File.ReadAllText(path));var root=json.RootElement;Assert.Equal(JsonValueKind.Null,root.GetProperty("UploadId").ValueKind);
            Assert.True(Guid.TryParseExact(root.GetProperty("ClientToken").GetString(),"N",out _));Assert.True(root.GetProperty("StartedUtc").GetDateTimeOffset()<=DateTimeOffset.UtcNow);pending++;
        };
        fixture.Server.Handler=async request=>
        {
            var uri=new Uri(fixture.Server.Address,request.Target);var key=Uri.UnescapeDataString(uri.AbsolutePath)[13..];
            if(request.Method=="POST"&&uri.Query.Contains("uploads",StringComparison.Ordinal))
            {
                var records=Directory.GetFiles(Work(fixture),"*.multipart.json").Select(path=>{using var json=JsonDocument.Parse(File.ReadAllText(path));return json.RootElement.Clone();});
                Assert.Contains(records,record=>record.GetProperty("Key").GetString()==key&&record.GetProperty("UploadId").ValueKind==JsonValueKind.Null);initiated++;
            }
            if(request.Method=="PUT"&&uri.Query.Contains("partNumber=",StringComparison.Ordinal))
            {
                Assert.Contains(Directory.GetFiles(Work(fixture),"*.multipart.json"),path=>
                {using var json=JsonDocument.Parse(File.ReadAllText(path));return json.RootElement.GetProperty("Key").GetString()==key&&json.RootElement.GetProperty("UploadId").ValueKind==JsonValueKind.String;});parts++;
            }
            return await handle(request);
        };
        await source.UploadAsync("large.bin",new MemoryStream(new byte[9*1024*1024]),null);Assert.Equal(3,pending);Assert.Equal(pending,initiated);Assert.Equal(4,parts);
        Assert.Empty(Directory.GetFiles(Work(fixture),"*.multipart.json"));Assert.Empty(Directory.GetFiles(Work(fixture),"*.tmp"));
    }
    [Fact]
    public async Task CrashAfterPendingWriteRecoversExactKeyUpload()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();string? journal=null;DateTimeOffset start=default;
        source.MultipartJournalPending=path=>
        {
            using var json=JsonDocument.Parse(File.ReadAllText(path));if(json.RootElement.GetProperty("Key").GetString()!="remote/large.bin")return;
            journal=path;start=json.RootElement.GetProperty("StartedUtc").GetDateTimeOffset();throw new SimulatedCrash();
        };
        await Assert.ThrowsAsync<SimulatedCrash>(()=>source.UploadAsync("large.bin",new MemoryStream(new byte[6*1024*1024]),null));Assert.NotNull(journal);Assert.True(File.Exists(journal));
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="POST"&&r.Target.StartsWith("/test-bucket/remote/large.bin",StringComparison.Ordinal));
        fixture.Uploads["matching"]=new("remote/large.bin",new());List(fixture,[Upload("remote/large.bin","matching",start.AddSeconds(1))]);await source.RecoverKnownUploadsAsync();
        Assert.False(File.Exists(journal));Assert.False(fixture.Uploads.ContainsKey("matching"));Assert.Contains(fixture.Server.Requests,r=>r.Method=="GET"&&r.Target.Contains("prefix=remote%2Flarge.bin",StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public async Task OldFormatAndMalformedJournalsAreProcessedIndependently()
    {
        await using var fixture=new S3Fixture();var broken=await Journal(fixture,"000-broken","invalid-json");var valid=await Journal(fixture,"999-valid",new{Bucket="test-bucket",Key="remote/known.bin",UploadId="known"});fixture.Uploads["known"]=new("remote/known.bin",new());
        await using var source=fixture.Source();var warnings=new List<string>();source.WriteWarning+=warnings.Add;await source.RecoverKnownUploadsAsync();
        Assert.False(File.Exists(broken));Assert.True(File.Exists(broken+".bad"));Assert.False(File.Exists(valid));Assert.Empty(fixture.Uploads);Assert.Single(warnings);
    }
    [Fact]
    public async Task UnreadableJournalCanBeQuarantinedWithoutBlockingLaterRecords()
    {
        await using var fixture=new S3Fixture();var broken=await Journal(fixture,"000-unreadable","unreadable");var valid=await Journal(fixture,"999-valid",new{Bucket="test-bucket",Key="remote/known.bin",UploadId="known"});fixture.Uploads["known"]=new("remote/known.bin",new());
        using var locked=new FileStream(broken,FileMode.Open,FileAccess.Write,FileShare.Delete);await using var source=fixture.Source();await source.RecoverKnownUploadsAsync();
        Assert.True(File.Exists(broken+".bad"));Assert.False(File.Exists(valid));Assert.Empty(fixture.Uploads);
    }
    [Theory]
    [InlineData(403)][InlineData(501)][InlineData(503)]
    public async Task UnavailablePendingListKeepsJournalAndBoundsWarnings(int status)
    {
        await using var fixture=new S3Fixture();var path=await Pending(fixture,"pending",Start());var handle=fixture.Server.Handler;
        fixture.Server.Handler=request=>IsList(request)?Task.FromResult(HttpTestServer.Response.Text(status,$"<Error><Code>{(status==403?"AccessDenied":"NotImplemented")}</Code><Message>SECRET</Message></Error>")):handle(request);
        await using var source=fixture.Source();var warnings=new List<string>();source.WriteWarning+=warnings.Add;await source.RecoverKnownUploadsAsync();await source.RecoverKnownUploadsAsync();
        Assert.True(File.Exists(path));Assert.Single(warnings);Assert.DoesNotContain("SECRET",warnings[0]);Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE");
    }
    [Fact]
    public async Task RecoveryOnlyAbortsExactKeysInsideInclusiveInitiationWindow()
    {
        await using var fixture=new S3Fixture();var start=Start();var path=await Pending(fixture,"pending",start);var rows=new[]{
            Upload("remote/large.bin","early",start.AddSeconds(-31)),Upload("remote/large.bin","lower",start.AddSeconds(-30)),
            Upload("remote/large.bin","upper",start.AddMinutes(5)),Upload("remote/large.bin","late",start.AddMinutes(5).AddSeconds(1)),Upload("remote/large.bin.other","other",start)};
        foreach(var row in rows)fixture.Uploads[row.Element(Ns+"UploadId")!.Value]=new(row.Element(Ns+"Key")!.Value,new());List(fixture,rows);
        await using var source=fixture.Source();await source.RecoverKnownUploadsAsync();Assert.Equal(new[]{"early","late","other"},fixture.Uploads.Keys.Order(StringComparer.Ordinal));Assert.False(File.Exists(path));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CompletedEmptyOrExpiredListRetiresPendingJournal(bool expired)
    {
        await using var fixture=new S3Fixture();var start=Start().AddMinutes(expired?-10:0);var path=await Pending(fixture,"pending",start);
        List(fixture,expired?[Upload("remote/large.bin","recent",Start())]:[]);await using var source=fixture.Source();await source.RecoverKnownUploadsAsync();Assert.False(File.Exists(path));Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE");
    }
    [Fact]
    public async Task TransientAbortFailureLeavesOnlyFailedJournalForNextRun()
    {
        await using var fixture=new S3Fixture();var first=await Journal(fixture,"000-first",new{Bucket="test-bucket",Key="remote/first.bin",UploadId="first"});var second=await Journal(fixture,"999-second",new{Bucket="test-bucket",Key="remote/second.bin",UploadId="second"});
        fixture.Uploads["first"]=new("remote/first.bin",new());fixture.Uploads["second"]=new("remote/second.bin",new());fixture.AbortFailKey="remote/first.bin";
        await using var source=fixture.Source();await source.RecoverKnownUploadsAsync();Assert.True(File.Exists(first));Assert.False(File.Exists(second));Assert.True(fixture.Uploads.ContainsKey("first"));Assert.False(fixture.Uploads.ContainsKey("second"));
        fixture.AbortFailKey=null;await source.RecoverKnownUploadsAsync();Assert.False(File.Exists(first));Assert.Empty(fixture.Uploads);
    }
    [Fact]
    public async Task IncompletePaginationNeverAbortsPartiallyListedCandidates()
    {
        await using var fixture=new S3Fixture();var path=await Pending(fixture,"pending",Start());fixture.Uploads["candidate"]=new("remote/large.bin",new());
        List(fixture,[Upload("remote/large.bin","candidate",Start())],true);await using var source=fixture.Source();await source.RecoverKnownUploadsAsync();Assert.True(File.Exists(path));Assert.True(fixture.Uploads.ContainsKey("candidate"));Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="DELETE");
    }
    private static DateTimeOffset Start()=>DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    [Fact]
    public async Task PendingRecoveryCollectsEveryPageBeforeAborting()
    {
        await using var fixture=new S3Fixture();var start=Start();var path=await Pending(fixture,"pending",start);fixture.Uploads["first"]=new("remote/large.bin",new());fixture.Uploads["second"]=new("remote/large.bin",new());var handle=fixture.Server.Handler;
        fixture.Server.Handler=request=>
        {
            if(!IsList(request))return handle(request);var second=request.Target.Contains("key-marker=",StringComparison.Ordinal);
            if(second)Assert.Contains("upload-id-marker=first",request.Target);
            var page=new XElement(Ns+"ListMultipartUploadsResult",new XElement(Ns+"IsTruncated",second?"false":"true"),Upload("remote/large.bin",second?"second":"first",start));
            if(!second)page.Add(new XElement(Ns+"NextKeyMarker","remote/large.bin"),new XElement(Ns+"NextUploadIdMarker","first"));
            return Task.FromResult(HttpTestServer.Response.Text(200,page.ToString(SaveOptions.DisableFormatting)));
        };
        await using var source=fixture.Source();await source.RecoverKnownUploadsAsync();Assert.False(File.Exists(path));Assert.Empty(fixture.Uploads);
        var requests=fixture.Server.Requests.ToArray();Assert.Equal(2,requests.Count(IsList));Assert.True(Array.FindLastIndex(requests,IsList)<Array.FindIndex(requests,request=>request.Method=="DELETE"));
    }
    [Theory]
    [InlineData("{}")] [InlineData("null")]
    [InlineData("{\"Bucket\":\"test-bucket\",\"Key\":\"remote/large.bin\"}")]
    [InlineData("{\"Bucket\":\"another-bucket\",\"Key\":\"remote/large.bin\",\"UploadId\":\"known\"}")]
    public async Task InvalidJournalSchemaOrScopeIsQuarantinedWithoutRemoteRequests(string contents)
    {
        await using var fixture=new S3Fixture();var path=await Journal(fixture,"broken",contents);await using var source=fixture.Source();await source.RecoverKnownUploadsAsync();Assert.True(File.Exists(path+".bad"));Assert.Empty(fixture.Server.Requests);
    }
    [Fact]
    public async Task PendingWindowUsesUtcEvenForExtremeLocalOffset()
    {
        await using var fixture=new S3Fixture();var path=await Pending(fixture,"pending",new DateTimeOffset(9999,12,31,23,59,0,TimeSpan.FromHours(14)));List(fixture,[]);
        await using var source=fixture.Source();await source.RecoverKnownUploadsAsync();Assert.False(File.Exists(path));Assert.False(File.Exists(path+".bad"));
    }
    private static string Work(S3Fixture fixture)=>Path.Combine(fixture.Area,"work");
    private static async Task<string> Journal(S3Fixture fixture,string name,object value)
    {Directory.CreateDirectory(Work(fixture));var path=Path.Combine(Work(fixture),name+".multipart.json");await File.WriteAllTextAsync(path,value is string text?text:JsonSerializer.Serialize(value));return path;}
    private static Task<string> Pending(S3Fixture fixture,string name,DateTimeOffset start)=>Journal(fixture,name,new{Bucket="test-bucket",Key="remote/large.bin",UploadId=(string?)null,ClientToken=Guid.NewGuid().ToString("N"),StartedUtc=start});
    private static XElement Upload(string key,string id,DateTimeOffset start)=>new(Ns+"Upload",new XElement(Ns+"Key",key),new XElement(Ns+"UploadId",id),new XElement(Ns+"Initiated",start.UtcDateTime.ToString("O")));
    private static bool IsList(HttpTestServer.Request request)=>request.Method=="GET"&&request.Target.Contains("uploads",StringComparison.Ordinal);
    private static void List(S3Fixture fixture,IEnumerable<XElement> rows,bool truncated=false)
    {
        var handle=fixture.Server.Handler;fixture.Server.Handler=request=>IsList(request)?Task.FromResult(HttpTestServer.Response.Text(200,new XElement(Ns+"ListMultipartUploadsResult",new XElement(Ns+"Bucket","test-bucket"),new XElement(Ns+"IsTruncated",truncated.ToString().ToLowerInvariant()),rows).ToString(SaveOptions.DisableFormatting))):handle(request);
    }
    private sealed class SimulatedCrash:Exception;
}
