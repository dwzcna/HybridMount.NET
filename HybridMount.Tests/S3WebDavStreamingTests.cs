using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class S3WebDavStreamingTests
{
    private const int Chunk=262144;
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task RemoteReconciliationHashesWithOneGet(bool s3)
    {
        await using var fixture=new Fixture(s3);var entry=await fixture.Metadata();fixture.Server.Requests.Clear();
        var method=typeof(MountManager).GetMethod("HashRemoteAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
        var digest=await (Task<string>)method.Invoke(null,[fixture.Source,entry,CancellationToken.None])!;
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixture.Bytes)),digest);Assert.Single(fixture.Server.Requests,r=>r.Method=="GET");
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task MultiMegabyteSpanUsesOneGetAndAlignedSinkBlocks(bool s3)
    {
        await using var fixture=new Fixture(s3);var entry=await fixture.Metadata();fixture.Server.Requests.Clear();using var output=new MemoryStream();var blocks=new List<int>();
        await fixture.Source.ReadRangeSequentialAsync(entry,4096,fixture.Bytes.Length-4096,async(bytes,token)=>{blocks.Add(bytes.Length);await output.WriteAsync(bytes,token);},Chunk);
        Assert.Equal(fixture.Bytes[4096..],output.ToArray());Assert.Single(fixture.Server.Requests,r=>r.Method=="GET");
        Assert.All(blocks.SkipLast(1),count=>Assert.Equal(Chunk,count));Assert.Equal((fixture.Bytes.Length-4096)%Chunk,blocks[^1]);
        Assert.Equal($"bytes=4096-{fixture.Bytes.Length-1}",fixture.Server.Requests.Single().Headers["Range"]);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task InterruptedBodyResumesAtNextUnreadByteWithoutDuplicatingSink(bool s3)
    {
        await using var fixture=new Fixture(s3);var entry=await fixture.Metadata();fixture.Server.Requests.Clear();var handle=fixture.Server.Handler;var attempts=0;
        fixture.Server.Handler=async request=>{var response=await handle(request);return ++attempts==1?Truncate(response,Chunk+17):response;};
        using var output=new MemoryStream();var blocks=new List<int>();
        await fixture.Source.ReadRangeSequentialAsync(entry,4096,entry.Size-4096,async(bytes,token)=>{blocks.Add(bytes.Length);await output.WriteAsync(bytes,token);},Chunk);
        Assert.Equal(fixture.Bytes[4096..],output.ToArray());Assert.Equal(2,attempts);Assert.All(blocks.SkipLast(1),count=>Assert.Equal(Chunk,count));
        var requests=fixture.Server.Requests.ToArray();Assert.Equal($"bytes={4096+Chunk+17}-{entry.Size-1}",requests[1].Headers["Range"]);
        Assert.Equal(requests[0].Headers["If-Match"],requests[1].Headers["If-Match"]);
        if(s3)Assert.Equal(new Uri(fixture.Server.Address,requests[0].Target).Query,new Uri(fixture.Server.Address,requests[1].Target).Query);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ChangedEtagOnResumeIsConflict(bool s3)
    {
        await using var fixture=new Fixture(s3);var entry=await fixture.Metadata();var handle=fixture.Server.Handler;var attempts=0;
        fixture.Server.Handler=async request=>
        {
            var response=await handle(request);if(++attempts==1)return Truncate(response,Chunk+17);response.Headers!["ETag"]="\"replacement\"";return response;
        };
        using var output=new MemoryStream();await Assert.ThrowsAsync<SourceConflictException>(()=>fixture.Source.ReadRangeSequentialAsync(entry,0,entry.Size,(bytes,token)=>output.WriteAsync(bytes,token),Chunk));
        Assert.Equal(2,attempts);Assert.Equal(Chunk,output.Length);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task PersistentlyShortBodyFailsWithinBoundedAttempts(bool s3)
    {
        await using var fixture=new Fixture(s3);var entry=await fixture.Metadata();var handle=fixture.Server.Handler;var attempts=0;
        fixture.Server.Handler=async request=>{attempts++;return Truncate(await handle(request),17);};
        var error=await Record.ExceptionAsync(()=>fixture.Source.ReadRangeSequentialAsync(entry,0,entry.Size,(_,_)=>ValueTask.CompletedTask,Chunk));
        Assert.IsAssignableFrom<IOException>(error);Assert.InRange(attempts,1,3);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task ConsumerFailureIsNeverRetried(bool s3)
    {
        await using var fixture=new Fixture(s3);var entry=await fixture.Metadata();fixture.Server.Requests.Clear();var expected=new IOException("local sink");var calls=0;
        var error=await Record.ExceptionAsync(()=>fixture.Source.ReadRangeSequentialAsync(entry,0,entry.Size,(_,_)=>{calls++;throw expected;},Chunk));
        Assert.Same(expected,error);Assert.Equal(1,calls);Assert.Single(fixture.Server.Requests,r=>r.Method=="GET");
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task CancellationBetweenBlocksStopsWithoutAnotherGet(bool s3)
    {
        await using var fixture=new Fixture(s3);var entry=await fixture.Metadata();fixture.Server.Requests.Clear();using var canceled=new CancellationTokenSource();var calls=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>fixture.Source.ReadRangeSequentialAsync(entry,0,entry.Size,(_,_)=>{calls++;canceled.Cancel();return ValueTask.CompletedTask;},Chunk,canceled.Token));
        Assert.Equal(1,calls);Assert.Single(fixture.Server.Requests,r=>r.Method=="GET");
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task DavIgnoredRangeUsesExistingWholeFileCache(bool afterResume)
    {
        await using var fixture=new Fixture(false);var entry=await fixture.Metadata();fixture.Server.Requests.Clear();var handle=fixture.Server.Handler;var attempts=0;
        fixture.Dav!.IgnoreRange=!afterResume;
        fixture.Server.Handler=async request=>
        {
            var response=await handle(request);if(afterResume&&++attempts==1){fixture.Dav.IgnoreRange=true;return Truncate(response,Chunk+17);}return response;
        };
        using var output=new MemoryStream();await fixture.Source.ReadRangeSequentialAsync(entry,4096,entry.Size-4096,(bytes,token)=>output.WriteAsync(bytes,token),Chunk);
        Assert.Equal(fixture.Bytes[4096..],output.ToArray());Assert.Equal(CapabilitySupport.Unsupported,fixture.Source.Capabilities.RangeRead);
        Assert.InRange(fixture.Server.Requests.Count(r=>r.Method=="GET"),1,3);var count=fixture.Server.Requests.Count;
        await fixture.Source.ReadRangeSequentialAsync(entry,0,17,(_,_)=>ValueTask.CompletedTask);Assert.Equal(count,fixture.Server.Requests.Count);
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task EmptyRangeSendsNothingAndInvalidRangesFail(bool s3)
    {
        await using var fixture=new Fixture(s3);var entry=await fixture.Metadata();fixture.Server.Requests.Clear();
        await fixture.Source.ReadRangeSequentialAsync(entry,entry.Size,0,(_,_)=>throw new InvalidOperationException());Assert.Empty(fixture.Server.Requests);
        await Assert.ThrowsAsync<ArgumentException>(()=>fixture.Source.ReadRangeSequentialAsync(entry,entry.Size,1,(_,_)=>ValueTask.CompletedTask));
    }
    [Fact]
    public async Task DefaultSourceAccumulatesShortReadsIntoCompleteBlocks()
    {
        IFileSource source=new Fake();var entry=await source.GetMetadataAsync("file");using var output=new MemoryStream();var sizes=new List<int>();
        await source.ReadRangeSequentialAsync(entry,3,28,(bytes,token)=>{sizes.Add(bytes.Length);return output.WriteAsync(bytes,token);},12);
        Assert.Equal(new[]{12,12,4},sizes);Assert.Equal(Enumerable.Range(3,28).Select(i=>(byte)i),output.ToArray());
        ((Fake)source).Short=true;await Assert.ThrowsAsync<EndOfStreamException>(()=>source.ReadRangeSequentialAsync(entry,0,20,(_,_)=>ValueTask.CompletedTask,12));
    }
    [Fact]
    public async Task StreamingSinkRejectsExtraBytesBeforeFinalBlock()
    {
        var transfer=new SequentialRangeTransfer(new("","file",false,4,DateTimeOffset.UnixEpoch,"v"),0,4,(_,_)=>throw new InvalidOperationException(),4);
        var error=await Assert.ThrowsAsync<SequentialRangeConsumerException>(()=>transfer.ReadAsync(new MemoryStream([1,2,3,4,5]),CancellationToken.None));Assert.IsType<IOException>(error.InnerException);
    }
    private static HttpTestServer.Response Truncate(HttpTestServer.Response response,int count)
    {
        var headers=new Dictionary<string,string>(response.Headers!){["Content-Length"]=response.Body.Length.ToString()};return response with{Headers=headers,Body=response.Body[..count]};
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly S3Fixture? _s3;
        public DavFixture? Dav {get;}
        public IFileSource Source {get;}
        public HttpTestServer Server=>_s3?.Server??Dav!.Server;
        public byte[] Bytes {get;}=Enumerable.Range(0,2*1024*1024+133).Select(i=>(byte)(i*19)).ToArray();
        public Fixture(bool s3)
        {
            if(s3){_s3=new();_s3.Put("remote/stream.bin",Bytes);Source=_s3.Source();}
            else{Dav=new();Dav.Put("stream.bin",Bytes);Source=Dav.Source();}
        }
        public Task<SourceEntry> Metadata()=>Source.GetMetadataAsync("stream.bin");
        public async ValueTask DisposeAsync(){await Source.DisposeAsync();if(_s3 is not null)await _s3.DisposeAsync();else await Dav!.DisposeAsync();}
    }
    private sealed class Fake:IFileSource
    {
        public bool Short;
        public FileSourceCapabilities Capabilities=>new(CapabilitySupport.Unknown,CapabilitySupport.Unknown,CapabilitySupport.Unknown,CapabilitySupport.Unknown,CapabilitySupport.Unknown,SourceCaseSensitivity.Unknown);
        public Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)=>Task.FromResult(new SourceEntry("",path,false,40,DateTimeOffset.UnixEpoch,"v"));
        public ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)
        {var count=Short?0:Math.Min(7,buffer.Length);for(var i=0;i<count;i++)buffer.Span[i]=(byte)(offset+i);return ValueTask.FromResult(count);}
        public IAsyncEnumerable<SourceEntry> ListAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> UploadAsync(string path,Stream content,string? expectedVersion,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> MoveAsync(string path,string destination,string expectedVersion,CancellationToken token=default)=>throw new NotSupportedException();
        public Task DeleteAsync(string path,string expectedVersion,CancellationToken token=default)=>throw new NotSupportedException();
    }
}
