using System.Net;
using System.Net.Sockets;
using Amazon.Runtime;
using Amazon.S3;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class S3ReadRetryTests
{
    [Theory]
    [InlineData(408)][InlineData(429)][InlineData(500)][InlineData(502)][InlineData(503)][InlineData(504)]
    public async Task HeadTransientThenSuccess(int status)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var handle=fixture.Server.Handler;var attempts=0;
        fixture.Server.Handler=request=>Interlocked.Increment(ref attempts)==1?Task.FromResult(Failure(status)):handle(request);
        var entry=await source.GetMetadataAsync("文件.bin");Assert.Equal(16397,entry.Size);Assert.Equal(2,attempts);Assert.All(fixture.Server.Requests,r=>Assert.Equal("HEAD",r.Method));
    }
    [Theory]
    [InlineData("SlowDown")][InlineData("RequestTimeout")][InlineData("InternalError")]
    public async Task ListTransientServiceCodeThenSuccess(string code)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var handle=fixture.Server.Handler;var attempts=0;
        fixture.Server.Handler=request=>Interlocked.Increment(ref attempts)==1?Task.FromResult(Failure(400,code)):handle(request);
        await source.ProbeAsync();Assert.Equal(2,attempts);Assert.All(fixture.Server.Requests,r=>Assert.Contains("list-type=2",r.Target));
    }
    [Fact]
    public async Task ThreeTransientResponsesKeepSanitizedConnectionFailure()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();fixture.Server.Handler=_=>Task.FromResult(Failure(503));
        var error=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.GetMetadataAsync("文件.bin"));
        Assert.Equal(3,fixture.Server.Requests.Count);Assert.Equal(503,error.Diagnostic.HttpStatus);Assert.Equal(3,error.Diagnostic.Attempts);Assert.True(error.Diagnostic.Transient);
        Assert.DoesNotContain("SECRET",error.ToString());Assert.DoesNotContain(fixture.Server.Address.AbsoluteUri,error.ToString());
    }
    [Theory]
    [InlineData("reset")][InlineData("timeout")][InlineData("canceled")]
    public async Task RangeNetworkFailureRetriesWholePinnedRequest(string failure)
    {
        await using var fixture=new S3Fixture();var attempts=0;var requests=new List<string>();
        await using var source=Source(fixture,request=>
        {
            if(request.Headers.Range is null)return null;
            requests.Add(request.RequestUri!.Query+"|"+request.Headers.Range+"|"+string.Join(",",request.Headers.IfMatch));
            return ++attempts!=1?null:failure switch
            {
                "timeout"=>new TimeoutException("SECRET"),"canceled"=>new TaskCanceledException("SECRET"),
                _=>new HttpRequestException(HttpRequestError.ConnectionError,"SECRET",new IOException("SECRET",new SocketException((int)SocketError.ConnectionReset)))
            };
        });
        var entry=await source.GetMetadataAsync("文件.bin");var bytes=new byte[7];Assert.Equal(7,await source.ReadRangeAsync(entry,5,bytes));
        Assert.Equal(fixture.Objects["remote/文件.bin"].Bytes[5..12],bytes);Assert.Equal(2,attempts);Assert.Equal(requests[0],requests[1]);Assert.Contains("versionId=",requests[1]);
    }
    [Fact]
    public async Task InterruptedRangeBodyRestartsFromOriginalOffset()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");
        var handle=fixture.Server.Handler;var attempts=0;var diagnostics=new List<SourceReadDiagnostic>();source.ReadDiagnostic+=diagnostics.Add;
        fixture.Server.Handler=async request=>
        {
            var response=await handle(request);return ++attempts==1?response with{Body=response.Body[..2]}:response;
        };
        var bytes=new byte[7];Assert.Equal(7,await source.ReadRangeAsync(entry,5,bytes));Assert.Equal(2,attempts);Assert.Equal(fixture.Objects["remote/文件.bin"].Bytes[5..12],bytes);
        var ranges=fixture.Server.Requests.Where(r=>r.Method=="GET").ToArray();Assert.Equal(2,ranges.Length);Assert.All(ranges,r=>Assert.Equal("bytes=5-11",r.Headers["Range"]));
        Assert.Equal(2,diagnostics.Count);Assert.False(diagnostics[0].Completed);Assert.True(diagnostics[1].Completed);
    }
    [Theory]
    [InlineData("status")][InlineData("range")][InlineData("etag")][InlineData("version")][InlineData("length")]
    public async Task RetriedRangeStillValidatesEveryResponseField(string invalid)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");var handle=fixture.Server.Handler;var attempts=0;
        fixture.Server.Handler=async request=>
        {
            if(++attempts==1)return Failure(503);var response=await handle(request);
            if(invalid=="status")return response with{Status=200};
            response.Headers![invalid switch{"range"=>"Content-Range","etag"=>"ETag","version"=>"x-amz-version-id",_=>"Content-Length"}]=invalid=="length"?"8":"invalid";return response;
        };
        await Assert.ThrowsAsync<SourceConflictException>(async()=>await source.ReadRangeAsync(entry,5,new byte[7]));Assert.Equal(2,attempts);
    }
    [Theory]
    [InlineData("PUT")][InlineData("DELETE")]
    public async Task MutationsNeverRetryTransientResponse(string method)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var entry=await source.UploadAsync("write.bin",new MemoryStream([1,2,3]),null);
        var handle=fixture.Server.Handler;var attempts=0;
        fixture.Server.Handler=request=>
        {
            if(request.Method!=method||!request.Target.StartsWith("/test-bucket/remote/write.bin",StringComparison.Ordinal))return handle(request);
            Interlocked.Increment(ref attempts);return Task.FromResult(Failure(503));
        };
        await Assert.ThrowsAsync<SourceOutcomeUnknownException>(async()=>
        {
            if(method=="PUT")await source.UploadAsync(entry.Path,new MemoryStream([4]),entry.Version);else await source.DeleteAsync(entry.Path,entry.Version);
        });
        Assert.Equal(1,attempts);Assert.Equal(new byte[]{1,2,3},fixture.Objects["remote/write.bin"].Bytes);
    }
    [Fact]
    public async Task CancellationDuringBackoffStopsWithoutAnotherRequest()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();using var canceled=new CancellationTokenSource();source.ReadRetryWaiting=canceled.Cancel;
        fixture.Server.Handler=_=>Task.FromResult(Failure(503));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>source.GetMetadataAsync("文件.bin",canceled.Token));Assert.Single(fixture.Server.Requests);
    }
    [Fact]
    public async Task AlreadyCanceledTokenSendsNothing()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>source.ProbeAsync(canceled.Token));Assert.Empty(fixture.Server.Requests);
    }
    [Theory]
    [InlineData(403)][InlineData(404)]
    public async Task PermanentResponsesAreNotRetried(int status)
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();var entry=await source.GetMetadataAsync("文件.bin");fixture.Server.Requests.Clear();fixture.Server.Handler=_=>Task.FromResult(Failure(status));
        var error=await Record.ExceptionAsync(async()=>await source.ReadRangeAsync(entry,5,new byte[7]));
        if(status==404)Assert.IsType<FileNotFoundException>(error);else Assert.IsType<SourceConnectionException>(error);Assert.Single(fixture.Server.Requests);
    }
    [Fact]
    public async Task MissingMetadataKeepsExistingFallbackWithoutRetrying404()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();Assert.Null(await source.TryGetMetadataAsync("missing"));
        Assert.Equal(2,fixture.Server.Requests.Count(r=>r.Method=="HEAD"));Assert.Single(fixture.Server.Requests,r=>r.Method=="GET");
    }
    private static HttpTestServer.Response Failure(int status,string code="SlowDown")=>HttpTestServer.Response.Text(status,$"<Error><Code>{code}</Code><Message>SECRET-URL-AND-RESPONSE</Message><RequestId>test</RequestId></Error>");
    private static S3FileSource Source(S3Fixture fixture,Func<HttpRequestMessage,Exception?> failure)
    {
        var config=new AmazonS3Config{ServiceURL=fixture.Server.Address.AbsoluteUri,AuthenticationRegion="us-east-1",ForcePathStyle=true,UseHttp=true,MaxErrorRetry=0,
            HttpClientFactory=new FaultFactory(failure),CacheHttpClient=false,DisableLogging=true,RequestChecksumCalculation=RequestChecksumCalculation.WHEN_REQUIRED,ResponseChecksumValidation=ResponseChecksumValidation.WHEN_REQUIRED};
        var client=new AmazonS3Client(new BasicAWSCredentials(S3Fixture.Credentials.AccessKey,S3Fixture.Credentials.SecretKey),config);
        return new(fixture.Options,S3Fixture.Credentials,Path.Combine(fixture.Area,"work"),client:client);
    }
    private sealed class FaultFactory(Func<HttpRequestMessage,Exception?> failure):HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig config)=>new(new FaultHandler(failure)){Timeout=TimeSpan.FromSeconds(15)};
    }
    private sealed class FaultHandler(Func<HttpRequestMessage,Exception?> failure):DelegatingHandler(new SocketsHttpHandler{AllowAutoRedirect=false})
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            =>failure(request) is {} error?Task.FromException<HttpResponseMessage>(error):base.SendAsync(request,token);
    }
}
