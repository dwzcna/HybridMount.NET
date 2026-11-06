using System.Text.Json;
using System.Xml.Linq;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class S3MultipartCopyTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task LargeCopyUsesConditionalServerRangesPreservesMetadataAndTrackedLifecycle(bool versioned)
    {
        await using var fixture=new CopyFixture(){IncludeVersionId=versioned};await using var source=fixture.Inner.Source();var entry=await source.GetMetadataAsync("source.bin");var result=await source.MoveAsync(entry.Path,"copied.bin",entry.Version);
        Assert.Equal(fixture.Length,result.Size);Assert.Equal(1,fixture.Initiates);Assert.Equal(11,fixture.Parts);Assert.Equal(1,fixture.Completes);Assert.Equal(0,fixture.Aborts);Assert.False(fixture.Inner.Objects.ContainsKey(CopyFixture.SourceKey));Assert.Empty(fixture.Journals());
        Assert.Equal("application/x-hybridmount-test",fixture.InitiateHeaders!["Content-Type"]);Assert.Equal("owner-value",fixture.InitiateHeaders["x-amz-meta-owner"]);Assert.Equal("",fixture.InitiateHeaders["x-amz-meta-empty"]);
        long next=0;var number=1;
        foreach(var request in fixture.Inner.Server.Requests.Where(request=>CopyFixture.Key(fixture.Inner,request)==CopyFixture.TargetKey&&request.Method=="PUT"))
        {
            Assert.Empty(request.Body);Assert.Equal(fixture.Original.ETag,request.Headers["x-amz-copy-source-if-match"]);
            if(versioned)Assert.Contains("versionId="+fixture.Original.VersionId,Uri.UnescapeDataString(request.Headers["x-amz-copy-source"]),StringComparison.Ordinal);else Assert.DoesNotContain("versionId=",request.Headers["x-amz-copy-source"],StringComparison.Ordinal);
            var range=request.Headers["x-amz-copy-source-range"][6..].Split('-').Select(long.Parse).ToArray();Assert.Equal(next,range[0]);Assert.InRange(range[1]-range[0]+1,1,5L*1024*1024*1024);next=range[1]+1;
            Assert.Equal(number++.ToString(),CopyFixture.Query(fixture.Inner,request)["partNumber"]);
        }
        Assert.Equal(fixture.Length,next);Assert.DoesNotContain(fixture.Inner.Server.Requests,request=>request.Method=="GET"&&CopyFixture.Key(fixture.Inner,request)==CopyFixture.SourceKey);
        Assert.Empty(Directory.GetFiles(fixture.Work,"*.move.tmp"));Assert.All(fixture.Inner.Server.Requests,S3Fixture.VerifySignature);
        var deletion=Assert.Single(fixture.Inner.Server.Requests,request=>request.Method=="DELETE"&&CopyFixture.Key(fixture.Inner,request)==CopyFixture.SourceKey);Assert.Equal(fixture.Original.ETag,deletion.Headers["If-Match"]);Assert.DoesNotContain("versionId=",deletion.Target,StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(false,0L)][InlineData(true,0L)][InlineData(false,3L)][InlineData(true,3L)][InlineData(false,5L*1024*1024*1024)]
    public async Task SmallCopyKeepsExistingCopyOrDownloadFallback(bool fallback,long length)
    {
        await using var fixture=new CopyFixture(fallback){Length=length};if(length==0)fixture.Inner.Put(CopyFixture.SourceKey,[]);await using var source=fixture.Inner.Source();var entry=await source.GetMetadataAsync("source.bin");await source.MoveAsync(entry.Path,"copied.bin",entry.Version);
        Assert.Equal(0,fixture.Initiates);Assert.Equal(fallback&&length>0,fixture.Inner.Server.Requests.Any(request=>request.Method=="GET"&&CopyFixture.Key(fixture.Inner,request)==CopyFixture.SourceKey));
        var write=Assert.Single(fixture.Inner.Server.Requests,request=>request.Method=="PUT"&&CopyFixture.Key(fixture.Inner,request)==CopyFixture.TargetKey);if(!fallback)Assert.Equal("COPY",write.Headers["x-amz-metadata-directive"]);Assert.Equal("*",write.Headers["If-None-Match"]);
    }
    [Fact]
    public async Task LargeCopyDoesNotRequireOrdinaryConditionalCopySupportOrOptionalMetadata()
    {
        await using var fixture=new CopyFixture(true){IncludeMetadata=false};await using var source=fixture.Inner.Source();var entry=await source.GetMetadataAsync("source.bin");await source.MoveAsync(entry.Path,"copied.bin",entry.Version);Assert.Equal(1,fixture.Completes);Assert.DoesNotContain(fixture.InitiateHeaders!.Keys,key=>key.StartsWith("x-amz-meta-",StringComparison.OrdinalIgnoreCase));
    }
    [Theory]
    [InlineData("PartFailure")][InlineData("PartConflict")][InlineData("MissingPartTag")][InlineData("WrongPartVersion")]
    [InlineData("DestinationAppeared")][InlineData("CompleteLost")][InlineData("BadSize")][InlineData("BadVersion")][InlineData("SourceChanged")]
    public async Task CopyOrVerificationFailuresNeverDeleteSource(string failure)
    {
        await using var fixture=new CopyFixture(){Failure=failure};await using var source=fixture.Inner.Source();await ExpectPartial(source,fixture);
        Assert.True(fixture.Inner.Objects.ContainsKey(CopyFixture.SourceKey));Assert.DoesNotContain(fixture.Inner.Server.Requests,request=>request.Method=="DELETE"&&CopyFixture.Key(fixture.Inner,request)==CopyFixture.SourceKey);Assert.Empty(fixture.Journals());
        if(failure=="DestinationAppeared")Assert.Equal(new byte[]{9},fixture.Inner.Objects[CopyFixture.TargetKey].Bytes);
        if(failure=="SourceChanged")Assert.Equal(new byte[]{9},fixture.Inner.Objects[CopyFixture.SourceKey].Bytes);
        if(failure is "PartFailure" or "PartConflict" or "MissingPartTag" or "WrongPartVersion"){Assert.Equal(1,fixture.Parts);Assert.Equal(0,fixture.Completes);Assert.Equal(1,fixture.Aborts);}
        if(failure=="CompleteLost")Assert.Equal(1,fixture.Completes);
    }
    [Theory]
    [InlineData("MetadataChanged")][InlineData("NoCompleteSupport")]
    public async Task UnverifiedSourceOrConditionalCompletionNeverStartsUserMultipart(string failure)
    {
        await using var fixture=new CopyFixture(){Failure=failure};await using var source=fixture.Inner.Source();await ExpectPartial(source,fixture);Assert.Equal(0,fixture.Initiates);Assert.Equal(0,fixture.Parts);Assert.True(fixture.Inner.Objects.ContainsKey(CopyFixture.SourceKey));Assert.Empty(fixture.Journals());
    }
    [Fact]
    public async Task CancellationAbortsWithIndependentTokenAndPreservesSource()
    {
        await using var fixture=new CopyFixture();using var cancel=new CancellationTokenSource();fixture.OnPart=()=>cancel.Cancel();await using var source=fixture.Inner.Source();var entry=await source.GetMetadataAsync("source.bin");await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync(entry.Path,"copied.bin",entry.Version,cancel.Token));Assert.Equal(1,fixture.Aborts);Assert.Empty(fixture.Journals());Assert.True(fixture.Inner.Objects.ContainsKey(CopyFixture.SourceKey));
    }
    [Fact]
    public async Task FailedAbortRetainsExistingJournalForRecovery()
    {
        await using var fixture=new CopyFixture(){Failure="PartFailure",FailAbort=true};await using var source=fixture.Inner.Source();await ExpectPartial(source,fixture);using(var json=JsonDocument.Parse(File.ReadAllText(Assert.Single(fixture.Journals()))))Assert.Equal("copy-upload",json.RootElement.GetProperty("UploadId").GetString());
        fixture.FailAbort=false;await source.RecoverKnownUploadsAsync();Assert.Empty(fixture.Journals());Assert.Equal(2,fixture.Aborts);Assert.True(fixture.Inner.Objects.ContainsKey(CopyFixture.SourceKey));
    }
    [Fact]
    public async Task MissingInitiationReceiptKeepsUnidentifiedJournalWithoutGuessingOwnership()
    {
        await using var fixture=new CopyFixture(){Failure="MissingUploadId"};await using var source=fixture.Inner.Source();await ExpectPartial(source,fixture);using(var json=JsonDocument.Parse(File.ReadAllText(Assert.Single(fixture.Journals()))))Assert.Equal(JsonValueKind.Null,json.RootElement.GetProperty("UploadId").ValueKind);
        Assert.Equal(0,fixture.Parts);await source.RecoverKnownUploadsAsync();Assert.Single(fixture.Journals());Assert.Equal(0,fixture.Aborts);
        Assert.DoesNotContain(fixture.Inner.Server.Requests,request=>request.Method=="GET"&&CopyFixture.Query(fixture.Inner,request).ContainsKey("uploads"));
    }
    [Fact]
    public async Task CrashBeforeInitiationLeavesPendingRecordAndNoRemoteUpload()
    {
        await using var fixture=new CopyFixture();await using var source=fixture.Inner.Source();source.MultipartJournalPending=path=>{using var json=JsonDocument.Parse(File.ReadAllText(path));if(json.RootElement.GetProperty("Key").GetString()==CopyFixture.TargetKey)throw new IOException("simulated crash");};
        await ExpectPartial(source,fixture);Assert.Equal(0,fixture.Initiates);Assert.Single(fixture.Journals());await source.RecoverKnownUploadsAsync();Assert.Single(fixture.Journals());Assert.Equal(0,fixture.Aborts);
    }
    [Fact]
    public async Task OversizedCopyFailsBeforeInitiatingMultipart()
    {
        await using var fixture=new CopyFixture(){Length=long.MaxValue};await using var source=fixture.Inner.Source();await ExpectPartial(source,fixture);Assert.Equal(0,fixture.Initiates);Assert.True(fixture.Inner.Objects.ContainsKey(CopyFixture.SourceKey));
    }
    /// <summary>能力结果已缓存为不支持时，第二次移动也不得越过条件完成验证初始化用户目标。</summary>
    [Fact]
    public async Task UnsupportedConditionalCompleteNeverStartsTargetOnRepeatedMove()
    {
        await using var fixture=new CopyFixture(){Failure="NoCompleteSupport"};await using var source=fixture.Inner.Source();
        await ExpectPartial(source,fixture);await ExpectPartial(source,fixture);
        Assert.Equal(CapabilitySupport.Unsupported,source.WriteCapabilities.MultipartComplete);Assert.Equal(0,fixture.Initiates);Assert.Equal(0,fixture.Parts);Assert.Equal(0,fixture.Completes);
        Assert.True(fixture.Inner.Objects.ContainsKey(CopyFixture.SourceKey));Assert.False(fixture.Inner.Objects.ContainsKey(CopyFixture.TargetKey));Assert.Empty(fixture.Journals());
        Assert.DoesNotContain(fixture.Inner.Server.Requests,request=>CopyFixture.Key(fixture.Inner,request)==CopyFixture.TargetKey&&request.Method is "POST" or "PUT");
    }
    private static async Task ExpectPartial(S3FileSource source,CopyFixture fixture)
    {
        var entry=await source.GetMetadataAsync("source.bin");var error=await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync(entry.Path,"copied.bin",entry.Version));Assert.Contains(error.Results,item=>item.Path==entry.Path);
    }
    private sealed class CopyFixture:IAsyncDisposable
    {
        internal const string SourceKey="remote/source.bin",TargetKey="remote/copied.bin";
        private static readonly XNamespace Ns="http://s3.amazonaws.com/doc/2006-03-01/";
        internal readonly S3Fixture Inner;
        internal readonly S3Fixture.Item Original;
        private readonly Func<HttpTestServer.Request,Task<HttpTestServer.Response>> _handle;
        internal long Length=5L*1024*1024*1024+17;
        internal bool IncludeMetadata=true,IncludeVersionId=true,FailAbort;
        internal string Failure="";
        internal int Initiates,Parts,Completes,Aborts;
        internal Action? OnPart;
        internal Dictionary<string,string>? InitiateHeaders;
        private bool _active;
        internal string Work=>Path.Combine(Inner.Area,"work");
        internal string[] Journals()=>Directory.Exists(Work)?Directory.GetFiles(Work,"*.multipart.json"):[];
        internal CopyFixture(bool fallback=false)
        {
            Inner=new(){IgnoreCopyConditions=fallback};Inner.Put(SourceKey,[1,2,3]);Original=Inner.Objects[SourceKey];_handle=Inner.Server.Handler;Inner.Server.Handler=Handle;
        }
        internal static string Key(S3Fixture fixture,HttpTestServer.Request request)
        {
            var path=Uri.UnescapeDataString(new Uri(fixture.Server.Address,request.Target).AbsolutePath);return path.StartsWith("/test-bucket/",StringComparison.Ordinal)?path[13..]:"";
        }
        internal static Dictionary<string,string> Query(S3Fixture fixture,HttpTestServer.Request request)=>new Uri(fixture.Server.Address,request.Target).Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries).Select(part=>part.Split('=',2)).ToDictionary(parts=>Uri.UnescapeDataString(parts[0]),parts=>parts.Length>1?Uri.UnescapeDataString(parts[1]):"");
        private static HttpTestServer.Response Xml(XElement xml,Dictionary<string,string>? headers=null)=>HttpTestServer.Response.Text(200,xml.ToString(SaveOptions.DisableFormatting),headers);
        private static HttpTestServer.Response Error(int status,string code)=>HttpTestServer.Response.Text(status,$"<Error><Code>{code}</Code><RequestId>copy-test</RequestId></Error>");
        private async Task<HttpTestServer.Response> Handle(HttpTestServer.Request request)
        {
            var key=Key(Inner,request);var query=Query(Inner,request);
            if(request.Method=="GET"&&query.ContainsKey("uploads"))return Xml(new XElement(Ns+"ListMultipartUploadsResult",new XElement(Ns+"Bucket","test-bucket"),new XElement(Ns+"IsTruncated","false"),_active?new XElement(Ns+"Upload",new XElement(Ns+"Key",TargetKey),new XElement(Ns+"UploadId","copy-upload"),new XElement(Ns+"Initiated",DateTime.UtcNow.ToString("O"))):null));
            if(key==TargetKey&&query.ContainsKey("uploads")&&request.Method=="POST")
            {
                Assert.Contains(Journals(),path=>{using var json=JsonDocument.Parse(File.ReadAllText(path));return json.RootElement.GetProperty("Key").GetString()==TargetKey&&json.RootElement.GetProperty("UploadId").ValueKind==JsonValueKind.Null;});
                Initiates++;_active=true;InitiateHeaders=new(request.Headers,StringComparer.OrdinalIgnoreCase);
                return Xml(new XElement(Ns+"InitiateMultipartUploadResult",new XElement(Ns+"Bucket","test-bucket"),new XElement(Ns+"Key",key),new XElement(Ns+"UploadId",Failure=="MissingUploadId"?"":"copy-upload")));
            }
            if(key==TargetKey&&query.ContainsKey("uploadId"))
            {
                if(request.Method=="DELETE"){Aborts++;if(FailAbort)return Error(503,"ServiceUnavailable");var active=_active;_active=false;return active?new(204,[]):Error(404,"NoSuchUpload");}
                if(request.Method=="PUT")
                {
                    Parts++;Assert.Contains(Journals(),path=>{using var json=JsonDocument.Parse(File.ReadAllText(path));return json.RootElement.GetProperty("UploadId").GetString()=="copy-upload";});OnPart?.Invoke();
                    if(Failure=="PartFailure")return Error(503,"ServiceUnavailable");if(Failure=="PartConflict")return Error(412,"PreconditionFailed");
                    if(Failure=="SourceChanged"&&Parts==1)Inner.Put(SourceKey,[9]);if(Failure=="DestinationAppeared"&&Parts==1)Inner.Put(TargetKey,[9]);
                    return Xml(new XElement(Ns+"CopyPartResult",new XElement(Ns+"LastModified","2026-10-29T12:00:00.000Z"),Failure=="MissingPartTag"?null:new XElement(Ns+"ETag","\"part-"+Parts+"\"")),new(){["x-amz-copy-source-version-id"]=Failure=="WrongPartVersion"?"wrong-version":Original.VersionId});
                }
                Completes++;Assert.Equal("*",request.Headers.GetValueOrDefault("If-None-Match"));var body=XElement.Parse(System.Text.Encoding.UTF8.GetString(request.Body));Assert.Equal(Parts,body.Elements().Count());
                if(Failure=="DestinationAppeared")return Error(412,"PreconditionFailed");
                _active=false;Inner.Put(TargetKey,Original.Bytes);var receipt=Inner.Objects[TargetKey];
                if(Failure=="BadVersion"){var changed=receipt with{VersionId="changed-version"};Inner.Objects[TargetKey]=changed;Inner.Versions[(TargetKey,changed.VersionId)]=changed;}
                if(Failure=="CompleteLost")return Error(503,"ServiceUnavailable");
                return Xml(new XElement(Ns+"CompleteMultipartUploadResult",new XElement(Ns+"Bucket","test-bucket"),new XElement(Ns+"Key",key),new XElement(Ns+"ETag",receipt.ETag)),new(){["x-amz-version-id"]=receipt.VersionId});
            }
            if(Failure=="NoCompleteSupport"&&request.Method=="POST"&&query.ContainsKey("uploadId"))
            {
                var headers=new Dictionary<string,string>(request.Headers,StringComparer.OrdinalIgnoreCase);headers.Remove("If-None-Match");headers.Remove("If-Match");return await _handle(request with{Headers=headers});
            }
            var response=await _handle(request);
            if(request.Method=="HEAD"&&response.Status==200&&key is SourceKey or TargetKey)
            {
                response.Headers!["Content-Length"]=(Failure=="BadSize"&&key==TargetKey?Length-1:Length).ToString();
                if(!IncludeVersionId)response.Headers.Remove("x-amz-version-id");
                if(key==SourceKey&&IncludeMetadata){response.Headers["Content-Type"]="application/x-hybridmount-test";response.Headers["x-amz-meta-owner"]="owner-value";response.Headers["x-amz-meta-empty"]="";}
                if(Failure=="MetadataChanged"&&key==SourceKey&&request.Headers.ContainsKey("If-Match"))response.Headers["ETag"]="\"changed\"";
            }
            return response;
        }
        public ValueTask DisposeAsync()=>Inner.DisposeAsync();
    }
}
