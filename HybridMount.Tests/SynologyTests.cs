using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting.Synology;

namespace HybridMount.Tests;

/// <summary>隔离 File Station 响应验证；不连接 NAS、不创建正式根，快照和探测均限于测试目录。</summary>
public sealed class SynologyTests : IDisposable
{
    private readonly string _area=Path.Combine(Path.GetTempPath(),"HybridMount.SynologyTests",Guid.NewGuid().ToString("N"));
    internal static readonly FileSourceConfiguration Options=new(FileSourceKind.Synology,"https://nas.example.test:5001/",Prefix:"/share");
    private SynologyFileSource Source(SynologyFakeDsm server,string? otp=null)=>new(Options,new("user","password"),new WholeFileCache(_area),server,otp);
    public void Dispose(){if(Directory.Exists(_area))Directory.Delete(_area,true);}

    [Theory]
    [InlineData(3)][InlineData(6)][InlineData(17)]
    public async Task DiscoveryUsesOnlyImplementedVersionsAndAuthenticationFields(int maximum)
    {
        var server=new SynologyFakeDsm{AuthMax=maximum};await using var source=Source(server,"123456");
        await source.ProbeAsync();var login=Assert.Single(server.Logins);
        Assert.Equal(maximum>=6?"6":"3",login["version"]);Assert.Equal("FileStation",login["session"]);Assert.Equal("123456",login["otp_code"]);
        Assert.Equal(maximum>=6,login.ContainsKey("enable_device_token"));Assert.Equal(maximum>=6,login.ContainsKey("enable_syno_token"));
        Assert.All(server.Requests.Where(x=>x["api"]=="SYNO.FileStation.List"),x=>Assert.Equal("2",x["version"]));
        Assert.All(server.Requests.Where(x=>x.ContainsKey("_sid")),x=>Assert.Equal("csrf",x["SynoToken"]));
    }
    [Fact]
    public async Task ExplicitOtpReplacesStaleDeviceTokenRatherThanSendingBoth()
    {
        var server=new SynologyFakeDsm();await using var source=new SynologyFileSource(Options,new("user","password",SessionToken:"stale-device"),new WholeFileCache(_area),server,"123456");
        await source.ProbeAsync();var login=Assert.Single(server.Logins);Assert.Equal("123456",login["otp_code"]);Assert.False(login.ContainsKey("device_id"));
    }
    [Fact]
    public async Task HashTaskStartUsesBoundedSessionRecovery()
    {
        var remaining=1;var server=new SynologyFakeDsm();server.Intercept=(request,fields,token)=>
        {
            if(fields["api"]=="SYNO.FileStation.MD5"&&fields["method"]=="start"&&Interlocked.Exchange(ref remaining,0)==1)
                return Task.FromResult<HttpResponseMessage?>(SynologyFakeDsm.Json(new{success=false,error=new{code=106}}));
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await using var source=Source(server);Assert.Contains(";md5:",(await source.GetMetadataAsync("file.bin")).Version);Assert.Equal(2,server.Logins.Count);
    }
    [Fact]
    public async Task ExplicitMissingDiscoveryEndpointFallsBackButAuthenticationFailureDoesNot()
    {
        var server=new SynologyFakeDsm{LegacyMissing=true};await using var source=Source(server);await source.ProbeAsync();Assert.Equal(2,server.Discoveries);
        var failed=new SynologyFakeDsm{DiscoveryStatus=403};await using var other=Source(failed);
        await Assert.ThrowsAsync<SourceRequestRejectedException>(()=>other.ProbeAsync());Assert.Equal(1,failed.Discoveries);Assert.Empty(failed.Logins);
    }
    [Fact]
    public async Task OptionalAndUnrelatedApisDoNotBlockReadOnlyConnection()
    {
        var server=new SynologyFakeDsm{OmitWrites=true};await using var source=Source(server);await source.ProbeAsync();
        var entry=await source.GetMetadataAsync("file.bin");Assert.Equal(3,await source.ReadRangeAsync(entry,2,new byte[3]));
        using var input=new MemoryStream([1]);await Assert.ThrowsAsync<NotSupportedException>(()=>source.UploadAsync("new.bin",input,null));
        Assert.Empty(server.Uploads);
    }
    [Theory]
    [InlineData(400)][InlineData(403)][InlineData(404)][InlineData(406)][InlineData(407)]
    public async Task AuthenticationFailuresLatchAcrossConcurrentAndLaterTasks(int code)
    {
        var server=new SynologyFakeDsm{LoginError=code};await using var source=Source(server);
        var errors=await Task.WhenAll(Enumerable.Range(0,12).Select(async _=>await Record.ExceptionAsync(()=>source.ProbeAsync())));
        Assert.All(errors,error=>Assert.Equal(ConnectionFailureKind.Authentication,Assert.IsType<SourceConnectionException>(error).Diagnostic.Kind));
        await Assert.ThrowsAsync<SourceConnectionException>(()=>source.GetMetadataAsync("file.bin"));Assert.Single(server.Logins);
        Assert.All(errors,error=>Assert.True(UploadRules.IsPermanentConnectionFailure(error!)));
    }
    [Theory]
    [InlineData(106)][InlineData(107)][InlineData(119)]
    public async Task ExpiredReadSessionRecoversOnlyOnce(int code)
    {
        var server=new SynologyFakeDsm{Expire=1,SessionError=code};await using var source=Source(server,"123456");await source.ProbeAsync();
        Assert.Equal(2,server.Logins.Count);Assert.False(server.Logins.Last().ContainsKey("otp_code"));
        var repeated=new SynologyFakeDsm{Expire=2};await using var other=Source(repeated);
        await Assert.ThrowsAsync<SourceConnectionException>(()=>other.ProbeAsync());Assert.Equal(2,repeated.Logins.Count);
        await Assert.ThrowsAsync<SourceConnectionException>(()=>other.ProbeAsync());Assert.Equal(2,repeated.Logins.Count);
    }
    [Fact]
    public async Task PreviewReturnsUnsupportedWithoutAnyAuthenticationOrSourceRequest()
    {
        var server=new SynologyFakeDsm();await using var source=Source(server);
        var result=await source.TryReadPreviewRangeAsync(new("","file.bin",false,8,DateTimeOffset.UtcNow,"unverified"),0,new byte[4]);
        Assert.False(result.Supported);Assert.Equal(0,result.Bytes);Assert.Empty(server.Requests);Assert.Empty(server.Logins);
    }
    [Fact]
    public async Task CompleteSnapshotIsSharedAcrossBlocksAndBaselineIsPublishedOnlyAfterValidation()
    {
        var server=new SynologyFakeDsm();await using var source=Source(server);var baselines=new List<SourceContentBaseline>();source.ContentBaseline+=baselines.Add;
        var entry=await source.GetMetadataAsync("file.bin");var a=new byte[3];var b=new byte[3];
        Assert.Equal(3,await source.ReadRangeAsync(entry,2,a));Assert.Equal(3,await source.ReadRangeAsync(entry,5,b));
        Assert.Equal("cde",Encoding.UTF8.GetString(a));Assert.Equal("fgh",Encoding.UTF8.GetString(b));Assert.Equal(1,server.Downloads);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(server.Files["/share/file.bin"])),Assert.Single(baselines).Sha256);
        Assert.DoesNotContain(server.Requests,x=>x.ContainsKey("range"));
    }
    [Fact]
    public async Task SameSecondSameLengthChangeInvalidatesVersionAndCachedContent()
    {
        var server=new SynologyFakeDsm();await using var source=Source(server);var old=await source.GetMetadataAsync("file.bin");var bytes=new byte[8];
        await source.ReadRangeAsync(old,0,bytes);server.Files["/share/file.bin"]=Encoding.UTF8.GetBytes("ABCDEFGH");
        var current=await source.GetMetadataAsync("file.bin");Assert.False(source.IsSameVersion(old.Version,current.Version));
        await source.ReadRangeAsync(current,0,bytes);Assert.Equal("ABCDEFGH",Encoding.UTF8.GetString(bytes));Assert.Equal(2,server.Downloads);
    }
    [Fact]
    public async Task ChangedSnapshotCannotPublishOrServeAnyBytes()
    {
        var server=new SynologyFakeDsm{AfterDownload=()=>Encoding.UTF8.GetBytes("CHANGED!")};await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");
        var published=0;source.ContentBaseline+=_=>published++;var output=new byte[8];
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.ReadRangeAsync(entry,0,output).AsTask());
        Assert.All(output,x=>Assert.Equal((byte)0,x));Assert.Equal(0,published);
        Assert.Empty(Directory.Exists(_area)?Directory.GetFiles(_area,"*.cache"):[]);
    }
    [Fact]
    public async Task WeakOrStaleVersionCannotStartSnapshotDownload()
    {
        var server=new SynologyFakeDsm();await using var source=Source(server);
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.ReadRangeAsync(new("","file.bin",false,8,DateTimeOffset.UtcNow,"s:8;m:10;c:1"),0,new byte[1]).AsTask());
        var entry=await source.GetMetadataAsync("file.bin");server.Files["/share/file.bin"]=Encoding.UTF8.GetBytes("CHANGED!");
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.ReadRangeAsync(entry,0,new byte[1]).AsTask());Assert.Equal(0,server.Downloads);
    }
    [Fact]
    public async Task CanceledHashStopsRemoteTaskAndCannotPublish()
    {
        var server=new SynologyFakeDsm{HoldMd5=true};await using var source=Source(server);using var cancel=new CancellationTokenSource();
        var work=source.GetMetadataAsync("file.bin",cancel.Token);await server.HashStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>work);
        await source.DisposeAsync();Assert.Contains(server.Requests,x=>x["api"]=="SYNO.FileStation.MD5"&&x["method"]=="stop");Assert.Equal(0,server.Downloads);
    }
    [Fact]
    public async Task ListingKeepsForeignHmFilesAndRejectsCaseCollisionsWithoutCheckpoint()
    {
        var server=new SynologyFakeDsm();server.Files["/share/.hm-user.tmp"]=[1];await using var source=Source(server);var entries=new List<SourceEntry>();
        await foreach(var entry in source.ListAsync(""))entries.Add(entry);Assert.Contains(entries,x=>x.Path==".hm-user.tmp");
        Assert.All(entries,x=>Assert.Contains(";md5:",x.Version));
        server.Files["/share/FILE.bin"]=[2];await Assert.ThrowsAsync<SourceConnectionException>(async()=>{await foreach(var _ in source.ListAsync("")){};});
    }
    [Fact]
    public async Task OnlyExplicitNotFoundReturnsNullAndQuotaRemainsVolumeLabeled()
    {
        var server=new SynologyFakeDsm();await using var source=Source(server);Assert.Null(await source.TryGetMetadataAsync("missing.bin"));
        server.MetadataError=407;var error=await Assert.ThrowsAsync<SourceConnectionException>(()=>source.TryGetMetadataAsync("file.bin"));Assert.Equal(ConnectionFailureKind.Permission,error.Diagnostic.Kind);
        var capacity=await source.GetRemoteCapacityAsync();Assert.Equal(12,capacity.AvailableBytes);Assert.Equal(32,capacity.TotalBytes);Assert.Contains("非账号配额",capacity.Scope);
    }
    [Fact]
    public async Task ReadOnlyProbeDoesNotCreateOrDeleteRemoteObjects()
    {
        var server=new SynologyFakeDsm();await using var source=Source(server);await source.ProbeAsync();await source.ListSharedFoldersAsync();
        Assert.Empty(server.Uploads);Assert.DoesNotContain(server.Requests,x=>x["api"] is "SYNO.FileStation.Delete" or "SYNO.FileStation.CreateFolder");
    }
    [Fact]
    public async Task SafeCreateVerifiesCollisionAndReceiptWithoutOverwriteOrImplicitParents()
    {
        var server=new SynologyFakeDsm();await using var source=Source(server);using var content=new MemoryStream(Encoding.UTF8.GetBytes("new content"));
        var result=await source.UploadAsync("new.bin",content,null);
        Assert.Equal("new.bin",result.Path);Assert.Equal("new content",Encoding.UTF8.GetString(server.Files["/share/new.bin"]));Assert.True(content.CanRead);
        Assert.All(server.Uploads,upload=>{Assert.False(upload.ContainsKey("overwrite"));Assert.Equal("false",upload["create_parents"]);Assert.InRange(long.Parse(upload["mtime"]),DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds(),DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds());});
        Assert.Equal(CapabilitySupport.Supported,source.WriteCapabilities.CreateOnly);
        Assert.DoesNotContain(server.Files.Keys,x=>x.Contains(".hybridmount-probe-",StringComparison.Ordinal));
        Assert.All(server.Requests.Where(x=>x["api"]=="SYNO.FileStation.Delete"&&x["method"]=="start"),x=>Assert.Contains(".hybridmount-probe-",x["path"]));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task UnreliableCreateOrSkippedReceiptNeverUploadsUserContent(bool skip)
    {
        var server=new SynologyFakeDsm{IgnoreCreateOnly=!skip,SkipExisting=skip};await using var source=Source(server);using var content=new MemoryStream([1,2,3]);
        await Assert.ThrowsAnyAsync<Exception>(()=>source.UploadAsync("new.bin",content,null));Assert.False(server.Files.ContainsKey("/share/new.bin"));
        var before=server.Uploads.Count;await Assert.ThrowsAsync<NotSupportedException>(()=>source.UploadAsync("new.bin",content,null));Assert.Equal(before,server.Uploads.Count);
    }
    [Fact]
    public async Task LostUserUploadReplyIsUnknownAndIsNeverReplayed()
    {
        var server=new SynologyFakeDsm{DropUpload="/share/new.bin"};await using var source=Source(server);using var content=new MemoryStream([1,2,3]);
        await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.UploadAsync("new.bin",content,null));
        Assert.Single(server.Uploads,x=>x["file"]=="new.bin");Assert.Equal(new byte[]{1,2,3},server.Files["/share/new.bin"]);
    }
    [Fact]
    public async Task CreateRacePreservesCompetingTargetAndDoesNotUseDeleteOrRename()
    {
        var server=new SynologyFakeDsm();server.Intercept=(request,fields,token)=>
        {if(fields.GetValueOrDefault("file")=="new.bin")server.Files["/share/new.bin"]=Encoding.UTF8.GetBytes("other writer");return Task.FromResult<HttpResponseMessage?>(null);};
        await using var source=Source(server);using var input=new MemoryStream([1,2,3]);
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("new.bin",input,null));
        Assert.Equal("other writer",Encoding.UTF8.GetString(server.Files["/share/new.bin"]));
        Assert.DoesNotContain(server.Requests,x=>x["api"]=="SYNO.FileStation.Rename"||x["api"]=="SYNO.FileStation.Delete"&&x.GetValueOrDefault("path","").Contains("new.bin",StringComparison.Ordinal));
    }
    [Fact]
    public async Task DestructiveOperationsAreRejectedBeforeAnyNetworkRequest()
    {
        var server=new SynologyFakeDsm();await using var source=Source(server);using var input=new MemoryStream([1]);
        await Assert.ThrowsAsync<NotSupportedException>(()=>source.UploadAsync("file.bin",input,"old"));
        await Assert.ThrowsAsync<NotSupportedException>(()=>source.DeleteAsync("file.bin","old"));
        await Assert.ThrowsAsync<NotSupportedException>(()=>source.MoveAsync("file.bin","new.bin","old"));Assert.Empty(server.Requests);
    }
    [Fact]
    public void CertificatePinIsScopedAndRejectsUnrelatedCertificates()
    {
        using var key=RSA.Create(2048);var request=new CertificateRequest("CN=test",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint=Convert.ToHexString(SHA256.HashData(certificate.RawData));
        Assert.False(SynologyFileSource.AcceptCertificate(certificate,SslPolicyErrors.RemoteCertificateChainErrors,null));
        Assert.True(SynologyFileSource.AcceptCertificate(certificate,SslPolicyErrors.RemoteCertificateChainErrors,fingerprint));
        Assert.False(SynologyFileSource.AcceptCertificate(certificate,SslPolicyErrors.RemoteCertificateChainErrors,new string('0',64)));
    }
}

/// <summary>可控 DSM 响应器；仅记录协议字段，故障注入不连接外部服务。</summary>
internal sealed class SynologyFakeDsm:HttpMessageHandler
{
    internal readonly ConcurrentQueue<Dictionary<string,string>> Requests=[],Logins=[],Uploads=[];
    internal readonly ConcurrentDictionary<string,byte[]> Files=new(StringComparer.Ordinal){["/share/file.bin"]=Encoding.UTF8.GetBytes("abcdefgh")};
    private readonly ConcurrentDictionary<string,string> _md5Tasks=new(StringComparer.Ordinal);
    internal int AuthMax=17,Expire,SessionError=106,LoginError,MetadataError,Discoveries,Downloads;
    internal bool LegacyMissing,OmitWrites,IgnoreCreateOnly,SkipExisting,HoldMd5;
    internal int DiscoveryStatus;
    internal string? DropUpload;
    internal Func<byte[]>? AfterDownload;
    internal Func<HttpRequestMessage,Dictionary<string,string>,CancellationToken,Task<HttpResponseMessage?>>? Intercept;
    internal bool JsonFormat,RequireOtp;
    internal readonly ConcurrentQueue<Dictionary<string,string>> RawRequests=[];
    internal readonly TaskCompletionSource HashStarted=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Dictionary<string,string> Fields(string text)=>text.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries)
        .Select(x=>x.Split('=',2)).ToDictionary(x=>Uri.UnescapeDataString(x[0]),x=>Uri.UnescapeDataString(x[1].Replace("+"," ")));
    internal static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private static HttpResponseMessage Success(object data)=>Json(new{success=true,data});
    private static HttpResponseMessage Failure(int code)=>Json(new{success=false,error=new{code}});
    private object File(string path)=>new{path,isdir=path=="/share",additional=new{size=Files.TryGetValue(path,out var bytes)?bytes.Length:0,time=new{mtime=10,crtime=1},type="bin",real_path=path}};
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        if(request.Content is MultipartFormDataContent form)
        {
            var upload=new Dictionary<string,string>();byte[] bytes=[];
            foreach(var part in form)
            {
                var name=part.Headers.ContentDisposition!.Name!.Trim('"');
                if(name=="file"){upload[name]=part.Headers.ContentDisposition.FileName!.Trim('"');bytes=await part.ReadAsByteArrayAsync(token);}
                else upload[name]=await part.ReadAsStringAsync(token);
            }
            Requests.Enqueue(upload);Uploads.Enqueue(upload);
            if(Intercept is {} onUpload&&await onUpload(request,upload,token) is {} uploadReply)return uploadReply;
            var path=upload["path"].TrimEnd('/')+"/"+upload["file"];
            if(Files.ContainsKey(path)&&!IgnoreCreateOnly)return SkipExisting?Success(new{}):Failure(1805);
            Files[path]=bytes;if(path==DropUpload)throw new HttpRequestException("simulated secret-containing lost reply");return Success(new{});
        }
        var fields=request.Method==HttpMethod.Get?Fields(request.RequestUri!.Query):Fields(await request.Content!.ReadAsStringAsync(token));Requests.Enqueue(fields);
        RawRequests.Enqueue(new(fields));
        var api=fields["api"];var method=fields["method"];
        if(JsonFormat&&api is not("SYNO.API.Info" or "SYNO.API.Auth"))foreach(var field in fields.ToArray())
            if(field.Value.StartsWith('"'))fields[field.Key]=JsonSerializer.Deserialize<string>(field.Value)!;
        if(Intercept is {} intercept&&await intercept(request,fields,token) is {} reply)return reply;
        if(api=="SYNO.API.Info")
        {
            Interlocked.Increment(ref Discoveries);if(DiscoveryStatus!=0)return new((HttpStatusCode)DiscoveryStatus);
            if(LegacyMissing&&request.RequestUri!.AbsolutePath.EndsWith("query.cgi",StringComparison.Ordinal))return new(HttpStatusCode.NotFound);
            var apis=new[]{"SYNO.API.Auth","SYNO.FileStation.List","SYNO.FileStation.Download","SYNO.FileStation.MD5","SYNO.FileStation.Upload","SYNO.FileStation.Delete","SYNO.FileStation.CreateFolder"};
            var result=apis.Where(x=>!OmitWrites||x is not("SYNO.FileStation.Upload" or "SYNO.FileStation.Delete" or "SYNO.FileStation.CreateFolder"))
                .ToDictionary(x=>x,x=>(object)new{path=x=="SYNO.API.Auth"&&AuthMax<6?"auth.cgi":"entry.cgi",minVersion=1,maxVersion=x=="SYNO.API.Auth"?AuthMax:17,requestFormat=JsonFormat?"JSON":""});
            result["UNRELATED.INVALID.API"]=new{path="../not-used",minVersion=0,maxVersion=0};return Success(result);
        }
        if(api=="SYNO.API.Auth")
        {
            if(method=="logout")return Success(new{});
            Logins.Enqueue(fields);if(RequireOtp&&!fields.ContainsKey("otp_code")&&!fields.ContainsKey("device_id"))return Failure(403);return LoginError!=0?Failure(LoginError):Success(new{sid="sid-"+Logins.Count,did="device-token",synotoken="csrf"});
        }
        if(api=="SYNO.FileStation.List"&&method=="getinfo")
        {
            if(Expire>0){Interlocked.Decrement(ref Expire);return Failure(SessionError);}if(MetadataError!=0)return Failure(MetadataError);
            var path=JsonSerializer.Deserialize<string[]>(fields["path"])![0];return path=="/share"||Files.ContainsKey(path)?Success(new{files=new[]{File(path)}}):Failure(408);
        }
        if(api=="SYNO.FileStation.List"&&method=="list")
        {
            var paths=Files.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();var offset=int.Parse(fields["offset"],System.Globalization.CultureInfo.InvariantCulture);
            return Success(new{total=paths.Length,files=paths.Skip(offset).Take(2).Select(File).ToArray()});
        }
        if(api=="SYNO.FileStation.List"&&method=="list_share")return Success(new{total=1,shares=new[]{new{name="share",path="/share",additional=new{volume_status=new{freespace=12,totalspace=32}}}}});
        if(api=="SYNO.FileStation.MD5")
        {
            if(method=="start"){var id=Guid.NewGuid().ToString("N");_md5Tasks[id]=fields["file_path"];HashStarted.TrySetResult();return Success(new{taskid=id});}
            if(method=="stop")return Success(new{});
            if(HoldMd5)return Success(new{finished=false});
            if(!Files.TryGetValue(_md5Tasks[fields["taskid"]],out var bytes))return Failure(408);
            return Success(new{finished=true,md5=Convert.ToHexString(MD5.HashData(bytes))});
        }
        if(api=="SYNO.FileStation.Delete")
        {
            if(method=="start"){Files.TryRemove(JsonSerializer.Deserialize<string[]>(fields["path"])![0],out _);return Success(new{taskid="delete-task"});}
            return Success(new{finished=true});
        }
        if(api=="SYNO.FileStation.Download")
        {
            Interlocked.Increment(ref Downloads);var path=JsonSerializer.Deserialize<string[]>(fields["path"])![0];var bytes=Files[path].ToArray();
            if(AfterDownload is {} change)Files[path]=change();return new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
        }
        throw new InvalidOperationException("Unexpected fake API: "+api+"/"+method);
    }
}
