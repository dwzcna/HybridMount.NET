using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class WebDavSaveTransferTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task LargeWeakOrMissingTagUpdateUnderVerifiedLockHasNoTargetGets(bool missingTag)
    {
        await using var test=new SaveFixture(6*1024*1024){MissingTag=missingTag};var condition=await test.Baseline();var bytes=new byte[6*1024*1024];bytes[^1]=7;SourceContentBaseline? baseline=null;test.Source.ContentBaseline+=value=>{if(value.Path==SaveFixture.Path)baseline=value;};
        var result=await test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream(bytes),condition);
        Assert.Equal(bytes.Length,result.Size);Assert.Equal(bytes,test.Fixture.Dav.Objects[SaveFixture.Path].Bytes);Assert.Empty(test.Requests("GET"));Assert.Equal(CapabilitySupport.Supported,test.Source.WriteCapabilities.ExclusiveLock);
        Assert.StartsWith("(<opaquelocktoken:",Assert.Single(test.Requests("PUT")).Headers["If"]);Assert.Contains(test.Requests("LOCK"),r=>r.Headers.GetValueOrDefault("If-Match")=="*");Assert.Empty(test.Fixture.Locks);
        Assert.NotNull(baseline);Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)),baseline.Sha256);Assert.Equal(result.Version,baseline.Version);
    }
    [Fact]
    public async Task MetadataChangedBeforeLockFallsBackToHashAndDetectsConflict()
    {
        await using var test=new SaveFixture();var condition=await test.Baseline();test.Fixture.Dav.Put(SaveFixture.Path,[9,9,9]);
        await Assert.ThrowsAsync<SourceConflictException>(()=>test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([7]),condition));Assert.Single(test.Requests("GET"));Assert.Empty(test.Requests("PUT"));Assert.Equal(new byte[]{9,9,9},test.Fixture.Dav.Objects[SaveFixture.Path].Bytes);Assert.Empty(test.Fixture.Locks);
    }
    [Fact]
    public async Task ChangedTimeWithSameContentHashesBaselineBeforeUpdating()
    {
        await using var test=new SaveFixture();var condition=await test.Baseline();test.Modified=test.Modified.AddSeconds(1);
        await test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([7,8]),condition);Assert.Single(test.Requests("GET"));Assert.Single(test.Requests("PUT"));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task MissingOrUnenforcedLocksNeverAllowUserPut(bool unenforced)
    {
        await using var test=new SaveFixture();test.Fixture.IgnoreExclusive=unenforced;test.NoLockSupport=!unenforced;var condition=await test.Baseline();
        await Assert.ThrowsAsync<NotSupportedException>(()=>test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([7]),condition));Assert.Empty(test.Requests("PUT"));Assert.Equal(new byte[]{1,1,1},test.Fixture.Dav.Objects[SaveFixture.Path].Bytes);
    }
    [Fact]
    public async Task PostPutSizeMismatchRetainsOutcomeUnknownFailure()
    {
        await using var test=new SaveFixture(){WrongSizeAfterPut=true};var condition=await test.Baseline();await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([7,8]),condition));Assert.Single(test.Requests("PUT"));Assert.Empty(test.Requests("GET"));Assert.Empty(test.Fixture.Locks);
    }
    [Fact]
    public async Task ExposedStrongTagAfterPutAvoidsReadbackWhenTimeDoesNotChange()
    {
        await using var test=new SaveFixture(){ChangeTime=false,StrongAfterPut=true};var condition=await test.Baseline();var result=await test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([7,8]),condition);
        Assert.True(test.Source.HasReliableVersion(result));Assert.Empty(test.Requests("GET"));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UnchangedPostPutTimeKeepsFullReadbackAndDetectsCorruption(bool corrupt)
    {
        await using var test=new SaveFixture(){ChangeTime=false,CorruptAfterPut=corrupt};var condition=await test.Baseline();var upload=test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([7,8,9]),condition);
        if(corrupt)await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>upload);else Assert.Equal(3,(await upload).Size);
        Assert.Single(test.Requests("GET"));Assert.Single(test.Requests("PUT"));Assert.Empty(test.Fixture.Locks);
    }
    [Fact]
    public async Task MissingModificationMetadataKeepsBothFullHashes()
    {
        await using var test=new SaveFixture(){MissingModified=true};var condition=await test.Baseline();await test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([7,8]),condition);Assert.Equal(2,test.Requests("GET").Length);
    }
    [Theory]
    [InlineData(0)][InlineData(3)]
    public async Task MissingRecordedSizeIsNotTreatedAsMatchingMetadata(int size)
    {
        await using var test=new SaveFixture(size);var condition=await test.Baseline();var version=JsonNode.Parse(condition.ExpectedVersion!)!.AsObject();version.Remove("Size");condition=condition with{ExpectedVersion=version.ToJsonString()};
        await test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([7,8]),condition);Assert.Single(test.Requests("GET"));
    }
    [Fact]
    public async Task RecordedEmptyBaselineAndEmptyUpdateAvoidFullGets()
    {
        await using var test=new SaveFixture(0);var condition=await test.Baseline();var result=await test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([]),condition);
        Assert.Equal(0,result.Size);Assert.Empty(test.Requests("GET"));Assert.Single(test.Requests("PUT"));Assert.Empty(test.Fixture.Dav.Objects[SaveFixture.Path].Bytes);
    }
    [Fact]
    public async Task ChangedObservationRequiresBaselineHash()
    {
        await using var test=new SaveFixture();var condition=await test.Baseline();test.Source.WithObservation(await test.Source.GetMetadataAsync(SaveFixture.Path),1);
        await test.Source.UploadWithPreconditionAsync(SaveFixture.Path,new MemoryStream([7,8]),condition);Assert.Single(test.Requests("GET"));
    }
    [Fact]
    public async Task StrongConditionalUpdateRetainsExistingReadbackAndIfMatch()
    {
        await using var test=new SaveFixture(){StrongBefore=true};var before=await test.Source.GetMetadataAsync(SaveFixture.Path);await test.Source.UploadAsync(SaveFixture.Path,new MemoryStream([7,8]),before.Version);
        Assert.Single(test.Requests("GET"));Assert.Equal(test.InitialTag,Assert.Single(test.Requests("PUT")).Headers["If-Match"]);Assert.Empty(test.Requests("LOCK"));
    }
    [Fact]
    public async Task WeakTagCreateStillUsesExistingFullReadback()
    {
        await using var test=new SaveFixture();test.Fixture.Dav.Objects.TryRemove(SaveFixture.Path,out _);await test.Source.UploadAsync(SaveFixture.Path,new MemoryStream([7,8]),null);
        Assert.Single(test.Requests("GET"));Assert.Equal("*",Assert.Single(test.Requests("PUT")).Headers["If-None-Match"]);Assert.Empty(test.Requests("LOCK"));
    }
    private sealed class SaveFixture:IAsyncDisposable
    {
        internal const string Path="large.bin";
        internal DavLockFixture Fixture {get;}=new();
        internal WebDavFileSource Source {get;}
        internal string InitialTag {get;}
        internal DateTimeOffset Modified=new(2026,10,29,12,0,0,TimeSpan.Zero);
        internal bool MissingTag,MissingModified,WrongSizeAfterPut,NoLockSupport,StrongAfterPut,StrongBefore,CorruptAfterPut,ChangeTime=true;
        private bool _uploaded;
        internal SaveFixture(int size=3)
        {
            Fixture.Dav.Put(Path,Enumerable.Repeat((byte)1,size).ToArray());InitialTag=Fixture.Dav.Objects[Path].ETag;var handle=Fixture.Dav.Server.Handler;
            string? Tag(DavFixture.Item item)=>!ReferenceEquals(item,Fixture.Dav.Objects.GetValueOrDefault(Path))||StrongBefore||StrongAfterPut&&_uploaded?item.ETag:MissingTag?null:"W/"+item.ETag;
            Fixture.Dav.XmlTag=Tag;Fixture.Dav.HttpTag=Tag;
            Fixture.Dav.Server.Handler=async request=>
            {
                if(NoLockSupport&&request.Method=="LOCK")return new(405,[]);
                var response=await handle(request);if(!new Uri(Fixture.Dav.Server.Address,request.Target).AbsolutePath.EndsWith("/"+Path,StringComparison.Ordinal))return response;
                if(request.Method=="PUT"&&response.Status is >=200 and <300){_uploaded=true;if(CorruptAfterPut)Fixture.Dav.Put(Path,[9,9,9]);}
                if(request.Method=="PROPFIND"&&response.Status==207)
                {
                    var xml=XElement.Parse(Encoding.UTF8.GetString(response.Body));XNamespace d="DAV:";
                    foreach(var element in xml.Descendants(d+"getlastmodified").ToArray())if(MissingModified)element.Remove();else element.Value=(_uploaded&&ChangeTime?Modified.AddSeconds(2):Modified).ToString("R");
                    if(_uploaded&&WrongSizeAfterPut)foreach(var element in xml.Descendants(d+"getcontentlength"))element.Value=(long.Parse(element.Value)+1).ToString();
                    response=response with{Body=Encoding.UTF8.GetBytes(xml.ToString())};
                }
                return response;
            };
            Source=Fixture.Dav.Source();
        }
        internal async Task<UploadPrecondition> Baseline()=>new((await Source.GetMetadataAsync(Path)).Version,Convert.ToHexString(SHA256.HashData(Fixture.Dav.Objects[Path].Bytes)));
        internal HttpTestServer.Request[] Requests(string method)=>Fixture.Dav.Server.Requests.Where(request=>request.Method==method&&new Uri(Fixture.Dav.Server.Address,request.Target).AbsolutePath.EndsWith("/"+Path,StringComparison.Ordinal)).ToArray();
        public async ValueTask DisposeAsync(){await Source.DisposeAsync();await Fixture.DisposeAsync();}
    }
}
