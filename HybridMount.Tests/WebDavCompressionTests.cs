using System.IO.Compression;
using System.Text;
using HybridMount.Models.Mounting.WebDAV;

namespace HybridMount.Tests;

/// <summary>本机真实HTTP和独立流验证gzip边界，保留原XML及正文安全策略。</summary>
public sealed class WebDavCompressionTests
{
    private static byte[] Zip(byte[] bytes)
    {using var output=new MemoryStream();using(var gzip=new GZipStream(output,CompressionLevel.Fastest,true))gzip.Write(bytes);return output.ToArray();}
    private static HttpTestServer.Response Compress(HttpTestServer.Response response,bool truncate=false)
    {
        var headers=new Dictionary<string,string>(response.Headers??[]){["Content-Encoding"]="gzip"};headers.Remove("Content-Length");
        var body=Zip(response.Body);if(truncate)body=body[..^4];return response with{Body=body,Headers=headers};
    }
    [Fact]
    public async Task Metadata_is_negotiated_and_decoded_while_file_bytes_remain_identity()
    {
        await using var fixture=new DavFixture();var next=fixture.Server.Handler;
        fixture.Server.Handler=async request=>
        {
            var response=await next(request);return request.Method is "PROPFIND" or "REPORT"?Compress(response):response;
        };
        await using var source=fixture.Source();await source.ProbeAsync();var entries=await WebDavTests.All(source.ListAsync(""));Assert.Equal(2,entries.Count);
        var file=await source.GetMetadataAsync("文件.bin");var bytes=new byte[7];await source.ReadRangeAsync(file,0,bytes);Assert.Equal(fixture.Bytes[..7],bytes);
        using var upload=new MemoryStream([1,2,3]);await source.UploadAsync("new.bin",upload,null);Assert.Equal(new byte[]{1,2,3},fixture.Objects["new.bin"].Bytes);
        Assert.All(fixture.Server.Requests.Where(request=>request.Method is "PROPFIND" or "REPORT"),request=>Assert.Equal("gzip",request.Headers["Accept-Encoding"]));
        Assert.All(fixture.Server.Requests.Where(request=>request.Method is "GET" or "HEAD"),request=>Assert.Equal("identity",request.Headers["Accept-Encoding"]));
        Assert.All(fixture.Server.Requests.Where(request=>request.Method=="PUT"),request=>Assert.False(request.Headers.ContainsKey("Content-Encoding")));
        Assert.Equal(ProtocolCapabilityStatus.Supported,ProtocolCapabilitySummary.Read(source.ProtocolCapabilities,"dav.gzip").Status);
    }
    [Theory]
    [InlineData("truncated")][InlineData("crc")][InlineData("length")][InlineData("header")][InlineData("extra")]
    public async Task Corrupt_or_truncated_gzip_is_rejected_even_if_xml_is_complete(string corruption)
    {
        var bytes=Zip("<root/>"u8.ToArray());
        if(corruption=="truncated")bytes=bytes[..^4];
        if(corruption=="crc")bytes[^8]^=1;
        if(corruption=="length")bytes[^4]^=1;
        if(corruption=="header")bytes[0]=0;
        if(corruption=="extra")bytes=[..bytes,0,0,0];
        using var content=new ByteArrayContent(bytes);content.Headers.ContentEncoding.Add("gzip");using var decoded=new WebDavMetadataContent(content);
        await using var stream=await decoded.ReadAsStreamAsync();await Assert.ThrowsAnyAsync<IOException>(()=>stream.CopyToAsync(Stream.Null));
    }
    [Fact]
    public async Task Decoded_stream_enforces_real_64MiB_limit_without_buffering_the_expansion()
    {
        using var compressed=new MemoryStream();
        using(var gzip=new GZipStream(compressed,CompressionLevel.Fastest,true))
        {
            var block=new byte[65536];for(var index=0;index<1024;index++)gzip.Write(block);gzip.WriteByte(0);
        }
        using var content=new ByteArrayContent(compressed.ToArray());content.Headers.ContentEncoding.Add("gzip");using var decoded=new WebDavMetadataContent(content);
        await using var stream=await decoded.ReadAsStreamAsync();await Assert.ThrowsAnyAsync<IOException>(()=>stream.CopyToAsync(Stream.Null));
    }
    [Theory]
    [InlineData("br")][InlineData("gzip, br")]
    public async Task Unknown_metadata_encoding_fails_before_returning_a_directory(string encoding)
    {
        await using var fixture=new DavFixture();var next=fixture.Server.Handler;
        fixture.Server.Handler=async request=>request.Method=="PROPFIND"?Compress(await next(request)) with{Headers=new(){["Content-Encoding"]=encoding}}:await next(request);
        await using var source=fixture.Source();await Assert.ThrowsAnyAsync<IOException>(()=>source.GetMetadataAsync(""));
    }
    [Fact]
    public async Task Xml_limits_and_dtd_protection_still_apply_after_decompression()
    {
        await using var fixture=new DavFixture();var next=fixture.Server.Handler;
        foreach(var xml in new[]{"<!DOCTYPE root [<!ENTITY x 'private'>]><root>&x;</root>","<d:multistatus xmlns:d='DAV:'/>"+new string(' ',17*1024*1024)})
        {
            fixture.Server.Handler=request=>request.Method=="PROPFIND"?Task.FromResult(Compress(HttpTestServer.Response.Text(207,xml))):next(request);
            await using var source=fixture.Source();await Assert.ThrowsAnyAsync<IOException>(()=>source.GetMetadataAsync(""));
        }
    }
    [Fact]
    public async Task Corrupt_report_does_not_advance_collection_checkpoints()
    {
        await using var fixture=new SyncDavFixture();var next=fixture.Dav.Server.Handler;var corrupt=false;
        fixture.Dav.Server.Handler=async request=>
        {
            var response=await next(request);return request.Method is "PROPFIND" or "REPORT"?Compress(response,corrupt&&request.Method=="REPORT"):response;
        };
        await using var source=fixture.Dav.Source();var repository=fixture.Repository();var mount=Guid.NewGuid();
        var engine=new RemoteSyncEngine(mount,"gzip",source,source,repository);await engine.PollAsync(false,TimeSpan.Zero,default);
        var original=repository.RemoteCollections(mount,"gzip").ToArray();fixture.Sequence++;corrupt=true;
        await Assert.ThrowsAnyAsync<IOException>(()=>engine.PollAsync(false,TimeSpan.Zero,default));Assert.Equal(original,repository.RemoteCollections(mount,"gzip"));
    }
    [Fact(Timeout=15000)]
    public async Task Decode_failure_returns_single_global_network_slot()
    {
        await using var fixture=new DavFixture();await using var queue=new HybridMount.Services.TasksQueueService(1);
        var next=fixture.Server.Handler;var corrupt=true;
        fixture.Server.Handler=async request=>request.Method=="PROPFIND"?Compress(await next(request),corrupt):await next(request);
        await using var source=fixture.Source();((IMountNetworkSource)source).BindQueue(queue.StartMount(Guid.NewGuid()));
        await Assert.ThrowsAnyAsync<IOException>(()=>source.GetMetadataAsync(""));Assert.Empty(queue.QueueSnapshots);
        corrupt=false;await source.ProbeAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert.Empty(queue.QueueSnapshots);
    }
    [Fact(Timeout=10000)]
    public async Task Compressed_body_wait_obeys_metadata_deadline()
    {
        await using var fixture=new DavFixture();var next=fixture.Server.Handler;
        fixture.Server.Handler=async request=>request.Method=="PROPFIND"?Compress(await next(request)) with{BeforeBody=new TaskCompletionSource().Task}:await next(request);
        await using var source=fixture.Source();source.MetadataDeadline=TimeSpan.FromMilliseconds(80);
        await Assert.ThrowsAnyAsync<IOException>(()=>source.ProbeAsync());
    }
}
