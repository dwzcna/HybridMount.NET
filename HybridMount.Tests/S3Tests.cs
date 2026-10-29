using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>真实 AWS SDK HTTP 请求验证，不使用接口模拟代替签名/协议/分页证据。</summary>
public sealed class S3Tests
{
    [Fact]
    public async Task SigV4PathStylePaginationPrefixAndVersionPinnedRange()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();await source.ProbeAsync();
        var entries=await WebDavTests.All(source.ListAsync(""));Assert.Equal(3,entries.Count);Assert.Contains(entries,item=>item.Path=="empty"&&item.IsDirectory);
        Assert.Contains(fixture.Server.Requests,request=>request.Target.Contains("continuation-token="));
        var file=await source.GetMetadataAsync("文件.bin");var original=fixture.Objects["remote/文件.bin"].Bytes;
        fixture.Put("remote/文件.bin",[1,2,3]);var bytes=new byte[73];Assert.Equal(73,await source.ReadRangeAsync(file,4093,bytes));Assert.Equal(original[4093..4166],bytes);
        Assert.All(fixture.Server.Requests,request=>Assert.StartsWith("/test-bucket",request.Target));
        Assert.All(fixture.Server.Requests,S3Fixture.VerifySignature);
        Assert.Equal(CapabilitySupport.Supported,source.Capabilities.RangeRead);Assert.Equal(CapabilitySupport.Unsupported,source.Capabilities.StableIds);
        Assert.All(entries,entry=>Assert.Equal("",entry.StableId));
    }
    [Fact]
    public async Task RejectRepeatedPaginationCollisionAndBadRange()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        fixture.RepeatToken=true;await Assert.ThrowsAsync<IOException>(()=>WebDavTests.All(source.ListAsync("")));fixture.RepeatToken=false;
        fixture.Put("remote/conflict",[1]);fixture.Put("remote/conflict/child",[2]);await Assert.ThrowsAsync<IOException>(()=>WebDavTests.All(source.ListAsync("")));
        fixture.Objects.TryRemove("remote/conflict",out _);fixture.Objects.TryRemove("remote/conflict/child",out _);
        var file=await source.GetMetadataAsync("文件.bin");fixture.BadRange=true;
        await Assert.ThrowsAsync<SourceConflictException>(async()=>await source.ReadRangeAsync(file,4,new byte[3]));
    }
    [Fact]
    public async Task ConditionalWritesMarkersCopyMoveAndDeleteKeepVersions()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        var file=await source.UploadAsync("new.bin",new MemoryStream([1,2,3]),null);
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("new.bin",Stream.Null,null));
        var updated=await source.UploadAsync("new.bin",new MemoryStream([4,5]),file.Version);
        await source.CreateDirectoryAsync("target");var moved=await source.MoveAsync("new.bin","target/moved.bin",updated.Version);
        Assert.Equal(2,moved.Size);Assert.False(fixture.Objects.ContainsKey("remote/new.bin"));
        var versions=fixture.Versions.Count;await source.DeleteAsync(moved.Path,moved.Version);Assert.Equal(versions,fixture.Versions.Count);
        Assert.DoesNotContain(fixture.Server.Requests,request=>request.Method=="DELETE"&&request.Target.Contains("versionId="));
        Assert.DoesNotContain(fixture.Objects.Keys,key=>key.Contains(".hybridmount-probe-"));
        Assert.Contains(fixture.Server.Requests,request=>request.Headers.ContainsKey("x-amz-copy-source")&&!request.Target.Contains("probe"));
    }
    [Fact]
    public async Task UnsupportedCopyConditionsUseSafeDownloadAndConditionalPut()
    {
        await using var fixture=new S3Fixture{IgnoreCopyConditions=true};await using var source=fixture.Source();
        var file=await source.GetMetadataAsync("文件.bin");await source.MoveAsync(file.Path,"moved.bin",file.Version);
        Assert.Contains(fixture.Server.Requests,request=>request.Method=="GET"&&request.Target.Contains("%E6%96%87%E4%BB%B6.bin",StringComparison.OrdinalIgnoreCase));
        Assert.Equal(file.Size,fixture.Objects["remote/moved.bin"].Bytes.Length);Assert.False(fixture.Objects.ContainsKey("remote/文件.bin"));
    }
    [Fact]
    public async Task MultipartUploadAndCancellationCleanOnlyOwnedUploads()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        var bytes=Enumerable.Range(0,9*1024*1024+17).Select(index=>(byte)(index*31)).ToArray();
        var entry=await source.UploadAsync("large.bin",new MemoryStream(bytes),null);Assert.Equal(bytes.Length,entry.Size);Assert.Equal(bytes,fixture.Objects["remote/large.bin"].Bytes);
        Assert.Empty(fixture.Uploads);Assert.True(fixture.Aborts>=2);Assert.Contains(fixture.Server.Requests,request=>request.Target.Contains("partNumber=2"));
        using var cancel=new CancellationTokenSource();fixture.OnPart=key=>{if(key=="remote/cancel.bin")cancel.Cancel();};
        var error=await Record.ExceptionAsync(()=>source.UploadAsync("cancel.bin",new MemoryStream(bytes),null,cancel.Token));
        Assert.True(error is OperationCanceledException or SourceOutcomeUnknownException,error?.ToString());Assert.Empty(fixture.Uploads);Assert.False(fixture.Objects.ContainsKey("remote/cancel.bin"));
    }
    [Fact]
    public async Task DirectoryMoveReportsPartialResultAndPreservesFailedSource()
    {
        await using var fixture=new S3Fixture();fixture.Put("remote/tree/a.bin",[1]);fixture.Put("remote/tree/b.bin",[2]);fixture.DeleteFailKey="remote/tree/b.bin";
        await using var source=fixture.Source();var directory=await source.GetMetadataAsync("tree");
        var partial=await Assert.ThrowsAsync<SourcePartialMoveException>(()=>source.MoveAsync("tree","destination",directory.Version));
        Assert.Contains(partial.Results,item=>item.Path=="tree/a.bin"&&item.Status=="Completed");
        Assert.Contains(partial.Results,item=>item.Path=="tree/b.bin"&&item.Status=="Copied");
        Assert.False(fixture.Objects.ContainsKey("remote/tree/a.bin"));Assert.True(fixture.Objects.ContainsKey("remote/tree/b.bin"));Assert.True(fixture.Objects.ContainsKey("remote/destination/b.bin"));
    }
    [Fact]
    public async Task UncertainWriteIsNotRetriedAndDirectoryTargetsAreProtected()
    {
        await using var fixture=new S3Fixture();await using var source=fixture.Source();
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("dir",new MemoryStream([99]),null));
        fixture.FailPutKey="remote/uncertain.bin";
        await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.UploadAsync("uncertain.bin",new MemoryStream([1]),null));
        Assert.Equal(1,fixture.Server.Requests.Count(request=>request.Method=="PUT"&&request.Target.EndsWith("uncertain.bin")));
        Assert.True(fixture.Objects.ContainsKey("remote/dir/child.bin"));
    }
    [Fact]
    public async Task IgnoredConditionsRejectUserWrite()
    {
        await using var fixture=new S3Fixture{IgnorePutConditions=true};await using var source=fixture.Source();
        await Assert.ThrowsAsync<NotSupportedException>(()=>source.UploadAsync("user.bin",Stream.Null,null));
        Assert.False(fixture.Objects.ContainsKey("remote/user.bin"));Assert.Equal(CapabilitySupport.Unsupported,source.Capabilities.ConditionalWrite);
    }
    [Fact]
    public async Task InterruptedMultipartRecoveryAbortsOnlyJournaledUploads()
    {
        await using var fixture=new S3Fixture();using var cancel=new CancellationTokenSource();
        await using(var source=fixture.Source())
        {
            fixture.AbortFailKey="remote/interrupted.bin";fixture.OnPart=key=>{if(key==fixture.AbortFailKey)cancel.Cancel();};
            var error=await Record.ExceptionAsync(()=>source.UploadAsync("interrupted.bin",new MemoryStream(new byte[6*1024*1024]),null,cancel.Token));Assert.NotNull(error);
            Assert.Single(fixture.Uploads);Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Area,"work"),"*.multipart.json"));
        }
        fixture.Uploads["foreign-upload"]=new("remote/foreign.bin",new());fixture.AbortFailKey=null;fixture.OnPart=null;
        await using(var resumed=fixture.Source())await resumed.ProbeAsync();
        Assert.Single(fixture.Uploads);Assert.True(fixture.Uploads.ContainsKey("foreign-upload"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Area,"work"),"*.multipart.json"));Assert.False(fixture.Objects.ContainsKey("remote/interrupted.bin"));
    }
    [Fact(Timeout=120000)]
    public async Task RealCfApiS3HydrationAndRemovalKeepRemoteObjects()
    {
        await using var fixture=new S3Fixture();var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(fixture.Area,"data"),registry);await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));
        await using var manager=new MountManager(repository,logger);var count=fixture.Objects.Count;
        var mount=await manager.AddAsync("S3 CFAPI 测试",local,true,source:fixture.Options,credentials:S3Fixture.Credentials);
        try
        {
            var expected=fixture.Objects["remote/文件.bin"].Bytes;Assert.Equal(expected,await Task.Run(()=>File.ReadAllBytes(Path.Combine(local,"文件.bin"))).WaitAsync(TimeSpan.FromSeconds(20)));
            var release=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"freeSpace"));Assert.Equal("Completed",release.State);
            var removed=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"remove"));Assert.True(removed.State=="Completed",removed.Error);
            Assert.Equal(expected,await File.ReadAllBytesAsync(Path.Combine(local,"文件.bin")));Assert.Equal(count,fixture.Objects.Count);Assert.Null(MountPathGuard.ReadRoot(local));
        }
        finally
        {
            if(manager.Mounts.Count>0){var result=await WebDavTests.Finished(manager,manager.Queue(mount.Id,"clearAndRemove"));Assert.True(result.State=="Completed",result.Error);}
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }
}

internal sealed class S3Fixture : IAsyncDisposable
{
    internal sealed record Item(byte[] Bytes,string ETag,string VersionId);
    internal sealed record Upload(string Key,SortedDictionary<int,byte[]> Parts);
    public static SourceCredentials Credentials { get; }=new(AccessKey:"TESTACCESSKEY",SecretKey:"test-secret-key");
    public string Area { get; }=Path.Combine(Path.GetTempPath(),"HybridMount.S3Tests",Guid.NewGuid().ToString("N"));
    public HttpTestServer Server { get; }=new();
    public ConcurrentDictionary<string,Item> Objects { get; }=new(StringComparer.Ordinal);
    public ConcurrentDictionary<(string,string),Item> Versions { get; }=new();
    public ConcurrentDictionary<string,Upload> Uploads { get; }=new();
    public bool RepeatToken,BadRange,IgnoreCopyConditions,IgnorePutConditions;
    public string? DeleteFailKey,FailPutKey,AbortFailKey;
    public Action<string>? OnPart;
    public int Aborts;
    private int _serial;
    private static readonly XNamespace Ns="http://s3.amazonaws.com/doc/2006-03-01/";
    public FileSourceConfiguration Options=>new(FileSourceKind.S3,Server.Address.AbsoluteUri,"test-bucket","remote","us-east-1",true,true);
    public S3Fixture()
    {
        Directory.CreateDirectory(Area);Put("remote/文件.bin",Enumerable.Range(0,16397).Select(index=>(byte)(index*19)).ToArray());Put("remote/dir/child.bin",[4,5,6]);Put("remote/empty/",[]);Server.Handler=Handle;
    }
    public S3FileSource Source()=>new(Options,Credentials,Path.Combine(Area,"work"),5L*1024*1024);
    public void Put(string key,byte[] bytes)
    {
        var serial=Interlocked.Increment(ref _serial);var item=new Item(bytes,"\"etag-is-not-md5-"+serial+"\"","version-"+serial);Objects[key]=item;Versions[(key,item.VersionId)]=item;
    }
    private static Dictionary<string,string> Query(Uri uri)=>uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries).Select(part=>part.Split('=',2)).ToDictionary(parts=>Uri.UnescapeDataString(parts[0]),parts=>parts.Length>1?Uri.UnescapeDataString(parts[1]):"",StringComparer.Ordinal);
    private static HttpTestServer.Response Xml(int status,XElement element,Dictionary<string,string>? headers=null)=>HttpTestServer.Response.Text(status,element.ToString(SaveOptions.DisableFormatting),headers);
    private static HttpTestServer.Response Error(int status,string code)=>Xml(status,new XElement("Error",new XElement("Code",code),new XElement("Message","test service"),new XElement("RequestId","test")));
    private static Dictionary<string,string> Headers(Item item)=>new(){["ETag"]=item.ETag,["x-amz-version-id"]=item.VersionId,["Last-Modified"]="Thu, 29 Oct 2026 12:00:00 GMT",["Content-Length"]=item.Bytes.Length.ToString()};
    private Task<HttpTestServer.Response> Handle(HttpTestServer.Request request)
    {
        var uri=new Uri(Server.Address,request.Target);var query=Query(uri);var path=Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if(path!="test-bucket"&&!path.StartsWith("test-bucket/",StringComparison.Ordinal))return Task.FromResult(Error(404,"NoSuchBucket"));
        var key=path.Length>12?path[12..]:"";Objects.TryGetValue(key,out var item);
        HttpTestServer.Response response;
        bool Conflict()=>request.Headers.GetValueOrDefault("If-None-Match")=="*"&&item is not null||request.Headers.TryGetValue("If-Match",out var tag)&&tag!=item?.ETag;
        if(request.Method=="GET"&&query.ContainsKey("list-type"))
        {
            var prefix=query.GetValueOrDefault("prefix","");var delimiter=query.GetValueOrDefault("delimiter","");
            var rows=new SortedDictionary<string,bool>(StringComparer.Ordinal);
            foreach(var name in Objects.Keys.Where(name=>name.StartsWith(prefix,StringComparison.Ordinal)))
            {var tail=name[prefix.Length..];var slash=delimiter=="/"?tail.IndexOf('/'):-1;if(slash>=0)rows[prefix+tail[..(slash+1)]]=true;else rows[name]=false;}
            var offset=query.TryGetValue("continuation-token",out var next)?int.Parse(next.Replace("page-","")):0;
            var take=Math.Min(2,int.Parse(query.GetValueOrDefault("max-keys","1000")));var page=rows.Skip(offset).Take(take).ToArray();var truncated=offset+page.Length<rows.Count;
            var xml=new XElement(Ns+"ListBucketResult",new XElement(Ns+"Name","test-bucket"),new XElement(Ns+"Prefix",prefix),new XElement(Ns+"KeyCount",page.Length),new XElement(Ns+"MaxKeys",take),new XElement(Ns+"IsTruncated",truncated.ToString().ToLowerInvariant()),
                truncated?new XElement(Ns+"NextContinuationToken",RepeatToken?"page-0":"page-"+(offset+page.Length)):null,
                page.Select(pair=>pair.Value?new XElement(Ns+"CommonPrefixes",new XElement(Ns+"Prefix",pair.Key)):new XElement(Ns+"Contents",new XElement(Ns+"Key",pair.Key),new XElement(Ns+"LastModified","2026-10-29T12:00:00.000Z"),new XElement(Ns+"ETag",Objects[pair.Key].ETag),new XElement(Ns+"Size",Objects[pair.Key].Bytes.Length),new XElement(Ns+"StorageClass","STANDARD"))));
            response=Xml(200,xml);
        }
        else if(request.Method=="HEAD"||request.Method=="GET")
        {
            if(query.TryGetValue("versionId",out var version))Versions.TryGetValue((key,version),out item);
            if(item is null)return Task.FromResult(Error(404,"NoSuchKey"));
            if(request.Headers.TryGetValue("If-Match",out var match)&&match!=item.ETag)return Task.FromResult(Error(412,"PreconditionFailed"));
            var headers=Headers(item);
            if(request.Method=="GET"&&request.Headers.TryGetValue("Range",out var range))
            {var limits=range[6..].Split('-').Select(int.Parse).ToArray();var content=item.Bytes[limits[0]..(limits[1]+1)];headers["Content-Length"]=content.Length.ToString();headers["Content-Range"]=$"bytes {limits[0]+(BadRange?1:0)}-{limits[1]}/{item.Bytes.Length}";response=new(206,content,headers);}
            else response=new(200,request.Method=="HEAD"?[]:item.Bytes,headers);
        }
        else if(query.ContainsKey("uploads")&&request.Method=="POST")
        {var id=Guid.NewGuid().ToString("N");Uploads[id]=new(key,new());response=Xml(200,new XElement(Ns+"InitiateMultipartUploadResult",new XElement(Ns+"Bucket","test-bucket"),new XElement(Ns+"Key",key),new XElement(Ns+"UploadId",id)));}
        else if(query.TryGetValue("uploadId",out var uploadId))
        {
            if(request.Method=="DELETE"){if(key==AbortFailKey)return Task.FromResult(Error(503,"ServiceUnavailable"));Uploads.TryRemove(uploadId,out _);Interlocked.Increment(ref Aborts);response=new(204,[]);}
            else if(!Uploads.TryGetValue(uploadId,out var upload))return Task.FromResult(Error(404,"NoSuchUpload"));
            else if(request.Method=="PUT"){var part=int.Parse(query["partNumber"]);upload.Parts[part]=request.Body;OnPart?.Invoke(key);response=new(200,[],new(){["ETag"]="\"part-"+part+"\""});}
            else if(Conflict())return Task.FromResult(Error(412,"PreconditionFailed"));
            else
            {Put(key,upload.Parts.Values.SelectMany(bytes=>bytes).ToArray());Uploads.TryRemove(uploadId,out _);var final=Objects[key];response=Xml(200,new XElement(Ns+"CompleteMultipartUploadResult",new XElement(Ns+"Location",uri.AbsoluteUri),new XElement(Ns+"Bucket","test-bucket"),new XElement(Ns+"Key",key),new XElement(Ns+"ETag",final.ETag)),Headers(final));response.Headers!["Content-Length"]=response.Body.Length.ToString();}
        }
        else if(request.Method=="PUT"&&request.Headers.TryGetValue("x-amz-copy-source",out var copy))
        {
            if(!IgnoreCopyConditions&&Conflict())return Task.FromResult(Error(412,"PreconditionFailed"));
            var sourceUri=new Uri(Server.Address,copy.TrimStart('/'));var sourcePath=Uri.UnescapeDataString(sourceUri.AbsolutePath).TrimStart('/')[12..];var sourceQuery=Query(sourceUri);
            Objects.TryGetValue(sourcePath,out var original);if(sourceQuery.TryGetValue("versionId",out var version))Versions.TryGetValue((sourcePath,version),out original);
            if(original is null)return Task.FromResult(Error(404,"NoSuchKey"));
            if(request.Headers.TryGetValue("x-amz-copy-source-if-match",out var expected)&&expected!=original.ETag)return Task.FromResult(Error(412,"PreconditionFailed"));
            Put(key,original.Bytes);var final=Objects[key];response=Xml(200,new XElement(Ns+"CopyObjectResult",new XElement(Ns+"ETag",final.ETag),new XElement(Ns+"LastModified","2026-10-29T12:00:00.000Z")),new(){["x-amz-version-id"]=final.VersionId});
        }
        else if(request.Method=="PUT")
        {if(key==FailPutKey)return Task.FromResult(Error(503,"ServiceUnavailable"));if(!IgnorePutConditions&&Conflict())return Task.FromResult(Error(412,"PreconditionFailed"));Put(key,request.Body);response=new(200,[],new(){["ETag"]=Objects[key].ETag,["x-amz-version-id"]=Objects[key].VersionId});}
        else if(request.Method=="DELETE")
        {if(key==DeleteFailKey)return Task.FromResult(Error(403,"AccessDenied"));if(Conflict())return Task.FromResult(Error(412,"PreconditionFailed"));Objects.TryRemove(key,out _);response=new(204,[],new(){["x-amz-delete-marker"]="true"});}
        else response=Error(400,"InvalidRequest");
        return Task.FromResult(response);
    }
    public static void VerifySignature(HttpTestServer.Request request)
    {
        var auth=request.Headers["Authorization"];Assert.StartsWith("AWS4-HMAC-SHA256 ",auth);
        var fields=auth[17..].Split(',',StringSplitOptions.TrimEntries).Select(part=>part.Split('=',2)).ToDictionary(parts=>parts[0],parts=>parts[1]);
        var credential=fields["Credential"].Split('/');Assert.Equal("TESTACCESSKEY",credential[0]);Assert.Equal("us-east-1",credential[2]);Assert.Equal("s3",credential[3]);
        var uri=new Uri("http://localhost"+request.Target);var query=uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries).Select(part=>part.Split('=',2)).Select(parts=>(Key:Uri.EscapeDataString(Uri.UnescapeDataString(parts[0])),Value:parts.Length==2?Uri.EscapeDataString(Uri.UnescapeDataString(parts[1])):""));
        var canonicalQuery=string.Join('&',query.OrderBy(item=>item.Key,StringComparer.Ordinal).ThenBy(item=>item.Value,StringComparer.Ordinal).Select(item=>item.Key+"="+item.Value));
        var signed=fields["SignedHeaders"];var canonicalHeaders=string.Join("",signed.Split(';').Select(name=>name+":"+string.Join(' ',request.Headers[name].Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries))+"\n"));
        var payload=request.Headers.GetValueOrDefault("x-amz-content-sha256",Convert.ToHexStringLower(SHA256.HashData(request.Body)));
        if(payload.Length==64)Assert.Equal(payload,Convert.ToHexStringLower(SHA256.HashData(request.Body)));
        var canonical=request.Method+"\n"+uri.AbsolutePath+"\n"+canonicalQuery+"\n"+canonicalHeaders+"\n"+signed+"\n"+payload;
        var scope=string.Join('/',credential[1..]);var toSign="AWS4-HMAC-SHA256\n"+request.Headers["x-amz-date"]+"\n"+scope+"\n"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        static byte[] Hmac(byte[] key,string text)=>HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(text));
        var key=Hmac(Encoding.UTF8.GetBytes("AWS4"+Credentials.SecretKey),credential[1]);key=Hmac(key,credential[2]);key=Hmac(key,"s3");key=Hmac(key,"aws4_request");
        Assert.Equal(fields["Signature"],Convert.ToHexStringLower(Hmac(key,toSign)));
    }
    public async ValueTask DisposeAsync(){await Server.DisposeAsync();if(Directory.Exists(Area)&&!MountPathGuard.ContainsCloudPlaceholders(Area))Directory.Delete(Area,true);}
}
