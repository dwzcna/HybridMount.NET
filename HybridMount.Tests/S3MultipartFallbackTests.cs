using System.Diagnostics;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class S3MultipartFallbackTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task KnownUnsupportedMultipartUsesOneConditionalPutForTwentyMegabytes(bool update)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var initial=await source.UploadAsync("large.bin",new MemoryStream([1]),null);
        Unsupported(source);fixture.Server.Requests.Clear();var bytes=new byte[20*1024*1024];bytes[^1]=37;
        if(!update)fixture.Objects.TryRemove("remote/large.bin",out _);
        var result=await source.UploadAsync("large.bin",new MemoryStream(bytes),update?initial.Version:null);Assert.Equal(bytes.Length,result.Size);Assert.Equal(bytes,fixture.Objects["remote/large.bin"].Bytes);
        var put=Assert.Single(fixture.Server.Requests,r=>r.Method=="PUT");Assert.Equal(update?fixture.Versions.Values.Single(item=>item.Bytes.Length==1&&item.Bytes[0]==1).ETag:"*",put.Headers[update?"If-Match":"If-None-Match"]);
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Target.Contains("uploads",StringComparison.Ordinal));Assert.Contains(fixture.Server.Requests,r=>r.Method=="HEAD"&&r.Target.EndsWith("large.bin",StringComparison.Ordinal));
    }
    [Theory]
    [InlineData("ignored")][InlineData("notimplemented")]
    public async Task UnsupportedProbeIsCachedAndNeverInitiatesUserMultipart(string failure)
    {
        await using var fixture=new S3Fixture();var handle=fixture.Server.Handler;var probes=0;
        fixture.Server.Handler=request=>
        {
            if(request.Method=="POST"&&request.Target.Contains("uploadId=",StringComparison.Ordinal))
            {
                probes++;if(failure=="notimplemented")return Task.FromResult(HttpTestServer.Response.Text(501,"<Error><Code>NotImplemented</Code></Error>"));
                request=new(request.Method,request.Target,new(request.Headers,StringComparer.OrdinalIgnoreCase),request.Body);request.Headers.Remove("If-Match");request.Headers.Remove("If-None-Match");
            }
            return handle(request);
        };
        await using var source=fixture.Source();await source.UploadAsync("large.bin",new MemoryStream(new byte[20*1024*1024]),null);
        Assert.Equal(CapabilitySupport.Unsupported,source.WriteCapabilities.MultipartComplete);Assert.Equal(1,probes);Assert.Empty(fixture.Uploads);
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="POST"&&r.Target.StartsWith("/test-bucket/remote/large.bin",StringComparison.Ordinal));
        fixture.Server.Requests.Clear();await source.UploadAsync("second.bin",new MemoryStream(new byte[6*1024*1024]),null);
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="POST");Assert.Equal(1,probes);
    }
    [Fact]
    public async Task UnsupportedMultipartRejectsMoreThanFiveGiBBeforeReadingContent()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();await source.UploadAsync("warm.bin",new MemoryStream([1]),null);Unsupported(source);fixture.Server.Requests.Clear();
        var error=await Assert.ThrowsAsync<NotSupportedException>(()=>source.UploadAsync("huge.bin",new LengthOnlyStream(),null));Assert.Contains("5 GiB",error.Message);
        Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method is "PUT" or "POST");
    }
    [Fact]
    public async Task SupportedMultipartStillSendsPartsInsteadOfUserPut()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();await source.UploadAsync("large.bin",new MemoryStream(new byte[9*1024*1024]),null);
        Assert.Equal(CapabilitySupport.Supported,source.WriteCapabilities.MultipartComplete);Assert.Contains(fixture.Server.Requests,r=>r.Target.Contains("large.bin?uploads",StringComparison.Ordinal));
        Assert.Contains(fixture.Server.Requests,r=>r.Target.Contains("partNumber=2",StringComparison.Ordinal));Assert.DoesNotContain(fixture.Server.Requests,r=>r.Method=="PUT"&&r.Target=="/test-bucket/remote/large.bin");
    }
    [Fact]
    public async Task StalledUploadBodySurfacesOutcomeUnknown()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();await source.UploadAsync("warm.bin",new MemoryStream([1]),null);source.InactivityTimeout=TimeSpan.FromMilliseconds(100);
        using var stalled=new SlowStream(1,Timeout.InfiniteTimeSpan);await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.UploadAsync("stalled.bin",stalled,null));Assert.False(fixture.Objects.ContainsKey("remote/stalled.bin"));
    }
    [Fact]
    public async Task SlowContinuousUploadProgressOutlivesInactivityWindow()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();await source.UploadAsync("warm.bin",new MemoryStream([1]),null);source.InactivityTimeout=TimeSpan.FromMilliseconds(500);
        using var slow=new SlowStream(6,TimeSpan.FromMilliseconds(120));var watch=Stopwatch.StartNew();var entry=await source.UploadAsync("slow.bin",slow,null);
        Assert.Equal(6,entry.Size);Assert.True(watch.Elapsed>source.InactivityTimeout);Assert.Equal(new byte[]{7,7,7,7,7,7},fixture.Objects["remote/slow.bin"].Bytes);
    }
    [Fact]
    public async Task StalledResponseHeadersSurfaceMutationOutcomeUnknown()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();await source.UploadAsync("warm.bin",new MemoryStream([1]),null);source.InactivityTimeout=TimeSpan.FromMilliseconds(100);var handle=fixture.Server.Handler;var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.Handler=async request=>{if(request.Method=="PUT"&&request.Target.EndsWith("stalled.bin",StringComparison.Ordinal))await release.Task;return await handle(request);};
        try{await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.UploadAsync("stalled.bin",new MemoryStream([1]),null));}finally{release.TrySetResult();}
    }
    [Fact]
    public async Task StalledResponseReadHasInactivityLimitAndCallerCancellationIsPreserved()
    {
        using var inner=new SlowStream(1,Timeout.InfiniteTimeSpan);using var idle=new S3FileSource.S3IdleStream(inner,()=>TimeSpan.FromMilliseconds(50),CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async()=>Assert.Equal(1,await idle.ReadAsync(new byte[1])));
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>Assert.Equal(1,await idle.ReadAsync(new byte[1],canceled.Token)));
    }
    private static void Unsupported(S3FileSource source)=>typeof(S3FileSource).GetProperty(nameof(S3FileSource.WriteCapabilities))!.SetValue(source,source.WriteCapabilities with{MultipartComplete=CapabilitySupport.Unsupported});
    private sealed class LengthOnlyStream:MemoryStream
    {
        public override long Length=>5L*1024*1024*1024+1;
        public override ValueTask<int> ReadAsync(Memory<byte> bytes,CancellationToken token=default)=>throw new InvalidOperationException("Must not read.");
    }
    private sealed class SlowStream(int length,TimeSpan delay):Stream
    {
        private long _position;
        public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;public override long Length=>length;public override long Position{get=>_position;set=>_position=value;}
        public override async ValueTask<int> ReadAsync(Memory<byte> bytes,CancellationToken token=default)
        {if(_position>=Length)return 0;await Task.Delay(delay,token);bytes.Span[0]=7;_position++;return 1;}
        public override int Read(byte[] bytes,int offset,int count)=>ReadAsync(bytes.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] bytes,int offset,int count,CancellationToken token)=>ReadAsync(bytes.AsMemory(offset,count),token).AsTask();
        public override long Seek(long offset,SeekOrigin origin){_position=origin switch{SeekOrigin.Begin=>offset,SeekOrigin.Current=>_position+offset,_=>Length+offset};return _position;}
        public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] bytes,int offset,int count)=>throw new NotSupportedException();
    }
}
