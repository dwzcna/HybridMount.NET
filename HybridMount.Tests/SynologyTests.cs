using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting.Synology;

namespace HybridMount.Tests;

public sealed class SynologyTests
{
    private static FileSourceConfiguration Options=>new(FileSourceKind.Synology,"https://nas.example.test:5001",Prefix:"/share");
    [Theory]
    [InlineData("share")][InlineData("/")][InlineData("/share/")][InlineData("/share/../other")]
    public void RootMustBeAnAbsoluteSharedFolder(string root)=>Assert.Throws<ArgumentException>(()=>SynologyFileSource.ValidateOptions(Options with{Prefix=root}));
    [Fact]
    public void OldConfigurationKeepsEnumValuesAndOptionalPin()
    {
        Assert.Equal(4,(int)FileSourceKind.Nfs);Assert.Equal(5,(int)FileSourceKind.Synology);
        var old=JsonSerializer.Deserialize<FileSourceConfiguration>("{\"Kind\":1,\"Endpoint\":\"https://example.test\"}")!;
        Assert.Null(old.PinnedCertificateSha256);Assert.DoesNotContain("PinnedCertificateSha256",JsonSerializer.Serialize(old));
        Assert.Throws<ArgumentException>(()=>SynologyFileSource.ValidateOptions(Options with{Endpoint="http://nas.example.test:5000"}));
        SynologyFileSource.ValidateOptions(Options with{Endpoint="http://nas.example.test:5000",AllowHttp=true});
    }
    [Theory]
    [InlineData(null)][InlineData("")][InlineData("   ")]
    public void EmptyFingerprintUsesSystemTrust(string? value)=>Assert.Null(SynologyFileSource.NormalizeFingerprint(value));
    [Theory]
    [InlineData("abc")][InlineData("zz:zz")]
    public void InvalidFingerprintIsRejected(string value)=>Assert.Throws<ArgumentException>(()=>SynologyFileSource.NormalizeFingerprint(value));
    [Fact]
    public void CertificatePolicyAcceptsOnlySystemTrustOrMatchingLeafPin()
    {
        using var key=RSA.Create(2048);var request=new CertificateRequest("CN=nas.example.test",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint=Convert.ToHexString(SHA256.HashData(certificate.RawData));
        var colon=string.Join(":",Enumerable.Range(0,32).Select(i=>fingerprint.Substring(i*2,2).ToLowerInvariant()));
        Assert.Equal(fingerprint,SynologyFileSource.NormalizeFingerprint(colon));
        Assert.True(SynologyFileSource.AcceptCertificate(certificate,SslPolicyErrors.None,null));
        Assert.True(SynologyFileSource.AcceptCertificate(certificate,SslPolicyErrors.RemoteCertificateChainErrors,colon));
        Assert.False(SynologyFileSource.AcceptCertificate(certificate,SslPolicyErrors.RemoteCertificateNameMismatch,new string('0',64)));
        Assert.False(SynologyFileSource.AcceptCertificate(certificate,SslPolicyErrors.RemoteCertificateChainErrors,null));
        Assert.False(SynologyFileSource.AcceptCertificate(null,SslPolicyErrors.RemoteCertificateNotAvailable,fingerprint));
    }
    [Fact]
    public void SynologyBackupRequiresSixAndPreservesEncryptedDeviceTokenAndPin()
    {
        var item=new MountBackupItem("群晖","local",Options with{PinnedCertificateSha256=new string('A',64)},new("user","password",SessionToken:"device-token"));
        var document=new MountConfigurationBackup(6,DateTimeOffset.UtcNow,[item]);var bytes=MountBackupCodec.Encode(document,"");
        Assert.Equal(item,MountBackupCodec.Decode(bytes,"").Mounts.Single());
        Assert.DoesNotContain("device-token",Encoding.UTF8.GetString(bytes));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(document with{SchemaVersion=5},""));
    }
    [Fact]
    public async Task MetadataUsesDiscoveredVersionAndKeepsSidOutOfCredentials()
    {
        var server=new Dsm();await using var source=Source(server,"123456");var entry=await source.GetMetadataAsync("file.bin");
        Assert.Equal("s:8;m:10;c:1",entry.Version);Assert.Equal("",entry.StableId);Assert.False(source.HasReliableVersion(entry));
        Assert.Equal("device-token",source.DeviceCredentials.SessionToken);
        Assert.Equal("123456",server.Logins[0]["otp_code"]);Assert.Equal("old-device",server.Logins[0]["device_id"]);
        Assert.Equal("FileStation",server.Logins[0]["session"]);Assert.Equal("17",server.Requests.Last()["version"]);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ListingCompletesEveryPageAndHandlesEmptyDirectory(bool empty)
    {
        var server=new Dsm{Empty=empty};await using var source=Source(server);var entries=new List<SourceEntry>();
        await foreach(var entry in source.ListAsync(""))entries.Add(entry);
        Assert.Equal(empty?0:3,entries.Count);Assert.Equal(empty?1:2,server.Lists);
        Assert.All(server.Requests.Where(x=>x["method"]=="list"),x=>Assert.Equal("[\"size\",\"time\",\"type\",\"real_path\"]",x["additional"]));
    }
    [Fact]
    public async Task OnlyNotFoundBecomesNull()
    {
        var server=new Dsm{MetadataError=408};await using var source=Source(server);Assert.Null(await source.TryGetMetadataAsync("file.bin"));
        server.MetadataError=407;await Assert.ThrowsAsync<SynologyApiException>(()=>source.TryGetMetadataAsync("file.bin"));
    }
    [Fact]
    public async Task CapacityDistinguishesVolumeSpaceFromUnknownAndErrors()
    {
        CapacityRules.Validate(FileSourceKind.Synology,true,null);
        var server=new Dsm();await using var source=Source(server);var capacity=await source.GetRemoteCapacityAsync();
        Assert.Equal(12L,capacity.AvailableBytes);Assert.Equal(20L,capacity.UsedBytes);Assert.Equal(32L,capacity.TotalBytes);
        server.Empty=true;Assert.Null((await source.GetRemoteCapacityAsync()).AvailableBytes);
        server.CapacityError=403;await Assert.ThrowsAsync<SynologyApiException>(()=>source.GetRemoteCapacityAsync());
    }
    [Fact]
    public async Task SharedFolderDiscoveryHandlesAccessibleEmptyAndDeniedLists()
    {
        var server=new Dsm();await using var source=Source(server);
        Assert.Equal(new SynologySharedFolder("share","/share"),Assert.Single(await source.ListSharedFoldersAsync()));
        server.Empty=true;Assert.Empty(await source.ListSharedFoldersAsync());
        server.CapacityError=403;await Assert.ThrowsAsync<SynologyApiException>(()=>source.ListSharedFoldersAsync());
    }
    [Fact]
    public async Task ProbeRejectsUnapprovedHttpWithoutReturningCredentials()
    {
        var result=await new FileSourceFactory().TestSynologyConnectionAsync(Options with{Endpoint="http://nas.example.test:5000",Prefix=""},new("user","password"),"123456");
        Assert.False(result.Success);Assert.Null(result.Credentials);Assert.Empty(result.Shares);
        Assert.DoesNotContain("password",result.ToString());Assert.DoesNotContain("123456",result.ToString());
    }
    [Fact]
    public void SynologyLabelsDescribeFullPollingRatherThanIncrementalSync()
    {
        var mount=new MountSnapshot(new(Guid.NewGuid(),"NAS","local",Source:Options),"已连接","");
        Assert.Equal("群晖 NAS（Synology）",mount.SourceDisplay);Assert.Contains("完整",mount.RemoteDetectionDisplay);Assert.DoesNotContain("增量",mount.RemoteDetectionDisplay);
        var row=new HybridMount.ViewModels.MountBackupRow(new("NAS","local",Options,new("user","password")),false);
        Assert.Contains("群晖 NAS",row.SourceDisplay);Assert.Contains("/share",row.SourceDisplay);Assert.DoesNotContain("password",row.SourceDisplay);
    }
    [Theory]
    [InlineData(106)][InlineData(107)][InlineData(119)]
    public async Task ExpiredSessionRelogsExactlyOnce(int error)
    {
        var server=new Dsm{Expire=1,SessionError=error};await using var source=Source(server,"123456");
        Assert.Equal(8,(await source.GetMetadataAsync("file.bin")).Size);Assert.Equal(2,server.Logins.Count);
        Assert.False(server.Logins[1].ContainsKey("otp_code"));
    }
    [Fact]
    public async Task RepeatedExpirationAndLoginFailureDoNotLoop()
    {
        var server=new Dsm{Expire=2};await using var source=Source(server);
        await Assert.ThrowsAsync<SynologyAuthenticationException>(()=>source.GetMetadataAsync("file.bin"));Assert.Equal(2,server.Logins.Count);
        var rejected=new Dsm{LoginError=400};await using var other=Source(rejected);
        await Assert.ThrowsAsync<SynologyAuthenticationException>(()=>other.ProbeAsync());Assert.Single(rejected.Logins);
    }
    [Fact]
    public async Task HonoredRangeReadsExactBytesAndMalformedRangeUsesValidatedFallback()
    {
        var server=new Dsm();await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");var bytes=new byte[3];
        Assert.Equal(3,await source.ReadRangeAsync(entry,2,bytes));Assert.Equal("cde",Encoding.UTF8.GetString(bytes));
        server.BadRange=true;var fallback=new byte[2];Assert.Equal(2,await source.ReadRangeAsync(entry,1,fallback));
        Assert.Equal("bc",Encoding.UTF8.GetString(fallback));Assert.Equal(CapabilitySupport.Unsupported,source.Capabilities.RangeRead);
    }
    [Fact]
    public async Task IgnoredRangeDoesNotDownloadWholeFileForPreview()
    {
        var server=new Dsm{IgnoreRange=true};await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");
        Assert.False((await source.TryReadPreviewRangeAsync(entry,2,new byte[3])).Supported);Assert.Equal(1,server.Downloads);Assert.Equal(0,server.Md5Starts);
    }
    [Fact]
    public async Task IgnoredRangeUsesValidatedWholeSnapshotForOrdinaryReads()
    {
        var server=new Dsm{IgnoreRange=true};await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");var bytes=new byte[3];
        Assert.Equal(3,await source.ReadRangeAsync(entry,2,bytes));Assert.Equal("cde",Encoding.UTF8.GetString(bytes));Assert.Equal(2,server.Md5Starts);
        Assert.Equal(0,await source.ReadRangeAsync(entry,entry.Size,Memory<byte>.Empty));Assert.Equal(2,server.Downloads);
    }
    [Fact]
    public async Task SnapshotHashMismatchRejectsPublication()
    {
        var server=new Dsm{IgnoreRange=true,BadMd5=true};await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.ReadRangeAsync(entry,0,new byte[2]).AsTask());
    }
    [Fact]
    public async Task LostMutationReplyIsUnknownAndIsNeverReplayed()
    {
        var server=new Dsm();await using var source=Source(server);using var content=new MemoryStream([1,2,3]);
        await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.UploadAsync("new.bin",content,null));
        Assert.Equal(1,server.Uploads);Assert.True(content.CanRead);
    }
    [Fact]
    public async Task CreateUploadsWithoutOverwritingAndValidatesReceipt()
    {
        var server=new Dsm{Writes=true};await using var source=Source(server);var bytes=Encoding.UTF8.GetBytes("new content");using var content=new MemoryStream(bytes);
        var entry=await source.UploadAsync("new.bin",content,null);
        Assert.Equal("new.bin",entry.Path);Assert.Equal(bytes,server.Files["/share/new.bin"]);
        var upload=Assert.Single(server.Requests.Where(x=>x["api"]=="SYNO.FileStation.Upload"));
        Assert.Equal("false",upload["overwrite"]);Assert.Equal("true",upload["create_parents"]);Assert.Equal("/share",upload["path"]);
        Assert.DoesNotContain(server.Requests,x=>x["api"] is "SYNO.FileStation.Delete" or "SYNO.FileStation.Rename");
    }
    [Fact]
    public async Task ReplaceStagesValidatesDeletesAndRenamesWithOnlyOneBaselineDownload()
    {
        var server=new Dsm{Writes=true};await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");
        var hash=Convert.ToHexString(SHA256.HashData(server.Files["/share/file.bin"]));var bytes=Encoding.UTF8.GetBytes("replacement");using var content=new MemoryStream(bytes);
        var receipt=await source.UploadWithPreconditionAsync("file.bin",content,new(entry.Version,hash));
        Assert.Equal("file.bin",receipt.Path);Assert.Equal(bytes,server.Files["/share/file.bin"]);Assert.Single(server.Files);Assert.Equal(1,server.Downloads);
        var upload=server.Requests.FindIndex(x=>x["api"]=="SYNO.FileStation.Upload");
        var delete=server.Requests.FindIndex(x=>x["api"]=="SYNO.FileStation.Delete"&&x["method"]=="start");
        var rename=server.Requests.FindIndex(x=>x["api"]=="SYNO.FileStation.Rename");
        Assert.True(upload<delete&&delete<rename);
        Assert.Contains(server.Requests.Skip(upload+1).Take(delete-upload-1),x=>x["api"]=="SYNO.FileStation.MD5"&&x["method"]=="start"&&x["file_path"]=="/share/file.bin");
        Assert.Equal("false",server.Requests[rename]["overwrite"]);
        Assert.StartsWith(".hm-",server.Requests[upload]["file"]);Assert.EndsWith(".tmp",server.Requests[upload]["file"]);
    }
    [Theory]
    [InlineData(true,0)][InlineData(false,2)]
    public async Task ChangedTargetPreservesVisibleNumberedConflictCopyAndNeverDeletesOriginal(bool versionChanged,int occupied)
    {
        var server=new Dsm{Writes=true,ChangeTargetAfterUpload=true,ChangeVersion=versionChanged,OccupiedConflictNames=occupied};
        await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");var bytes=Encoding.UTF8.GetBytes("local replacement");using var content=new MemoryStream(bytes);
        var error=await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("file.bin",content,entry.Version));
        var copy=Assert.Single(server.Files.Where(x=>x.Value.SequenceEqual(bytes))).Key;
        var suffix=occupied==0?"":"-"+(occupied+1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Matches(@"^/share/file \(冲突副本 \d{8}-\d{6}\)"+suffix+@"\.bin$",copy);Assert.Contains(copy[7..],error.Message);Assert.Equal(bytes,server.Files[copy]);
        Assert.Equal("CHANGED!",Encoding.UTF8.GetString(server.Files["/share/file.bin"]));
        Assert.DoesNotContain(server.Requests,x=>x["api"]=="SYNO.FileStation.Delete");Assert.Single(server.Requests.Where(x=>x["api"]=="SYNO.FileStation.Rename"));
        Assert.DoesNotContain(server.Files.Keys,x=>x.EndsWith(".tmp",StringComparison.Ordinal));
    }
    [Fact]
    public async Task DropAfterTemporaryUploadIsUnknownWithoutReplayOrTargetMutation()
    {
        var server=new Dsm{Writes=true,DropAfterTemporaryUpload=true};await using var source=Source(server);var entry=await source.GetMetadataAsync("file.bin");using var content=new MemoryStream([1,2,3]);
        await Assert.ThrowsAsync<SourceOutcomeUnknownException>(()=>source.UploadAsync("file.bin",content,entry.Version));
        Assert.Equal(1,server.Uploads);Assert.Single(server.Logins);Assert.Equal("abcdefgh",Encoding.UTF8.GetString(server.Files["/share/file.bin"]));
        Assert.Single(server.Files.Keys.Where(x=>x.EndsWith(".tmp",StringComparison.Ordinal)));
        Assert.DoesNotContain(server.Requests,x=>x["api"] is "SYNO.FileStation.Delete" or "SYNO.FileStation.Rename");
    }
    [Fact]
    public async Task PaginationFiltersTemporaryFilesAndMetadataHidesThemWithoutRequests()
    {
        var server=new Dsm{ListingPaths=["/share/.hm-a.tmp","/share/.HM-b.TMP","/share/visible","/share/.hm-keep.txt"]};await using var source=Source(server);var entries=new List<SourceEntry>();
        await foreach(var entry in source.ListAsync(""))entries.Add(entry);
        Assert.Equal(new[]{"visible",".hm-keep.txt"},entries.Select(x=>x.Path));Assert.Equal(2,server.Lists);
        var count=server.Requests.Count;Assert.Null(await source.TryGetMetadataAsync(".hm-a.tmp"));
        await Assert.ThrowsAsync<FileNotFoundException>(()=>source.GetMetadataAsync(".HM-b.TMP"));Assert.Equal(count,server.Requests.Count);
    }
    [Fact]
    public async Task CanceledOperationsNeverSendRequests()
    {
        var server=new Dsm();await using var source=Source(server);var token=new CancellationToken(true);using var content=new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>source.GetMetadataAsync("file.bin",token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>source.UploadAsync("new.bin",content,null,token));
        Assert.Empty(server.Requests);Assert.Equal(0,server.Uploads);
    }
    [Theory]
    [InlineData(400)][InlineData(401)][InlineData(402)][InlineData(403)][InlineData(404)][InlineData(407)]
    public async Task CredentialAndMfaErrorsAreNotRetried(int code)
    {
        var server=new Dsm{LoginError=code};await using var source=Source(server);
        Assert.Equal(code,(await Assert.ThrowsAsync<SynologyAuthenticationException>(()=>source.ProbeAsync())).Code);Assert.Single(server.Logins);
    }
    private static SynologyFileSource Source(Dsm server,string? otp=null)=>new(Options,new("user","password",SessionToken:"old-device"),
        new WholeFileCache(Path.Combine(Path.GetTempPath(),"HybridMount.Synology.Tests",Guid.NewGuid().ToString("N"))),server,otp);
    private sealed class Dsm:HttpMessageHandler
    {
        internal readonly List<Dictionary<string,string>> Requests=[],Logins=[];
        internal int Expire,SessionError=106,LoginError,MetadataError,CapacityError,Lists,Downloads,Md5Starts,Uploads;
        internal bool Empty,IgnoreRange,BadRange,BadMd5,Writes,ChangeTargetAfterUpload,ChangeVersion=true,DropAfterTemporaryUpload;
        internal int OccupiedConflictNames;
        internal string[]? ListingPaths;
        private static readonly byte[] Bytes=Encoding.UTF8.GetBytes("abcdefgh");
        internal readonly Dictionary<string,byte[]> Files=new(){["/share/file.bin"]=Bytes.ToArray()};
        private readonly Dictionary<string,long> _modified=[];
        private readonly Dictionary<string,string> _md5Tasks=[];
        private static Dictionary<string,string> Fields(string text)=>text.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries)
            .Select(x=>x.Split('=',2)).ToDictionary(x=>Uri.UnescapeDataString(x[0]),x=>Uri.UnescapeDataString(x[1].Replace("+"," ")));
        private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
        private static HttpResponseMessage Success(object data)=>Json(new{success=true,data});
        private static HttpResponseMessage Failure(int code)=>Json(new{success=false,error=new{code}});
        private object File(string path)=>new{path,isdir=path=="/share",additional=new{size=Writes&&Files.TryGetValue(path,out var bytes)?bytes.Length:Bytes.Length,time=new{mtime=_modified.GetValueOrDefault(path,10),crtime=1},type="bin",real_path=path}};
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.Content is MultipartFormDataContent form)
            {
                Uploads++;if(!Writes){await request.Content.ReadAsByteArrayAsync(token);throw new HttpRequestException("lost reply");}
                var upload=new Dictionary<string,string>();byte[]? bytes=null;
                foreach(var part in form)
                {
                    var name=part.Headers.ContentDisposition!.Name!.Trim('"');
                    if(name=="file"){upload[name]=part.Headers.ContentDisposition.FileName!.Trim('"');bytes=await part.ReadAsByteArrayAsync(token);}
                    else upload[name]=await part.ReadAsStringAsync(token);
                }
                Requests.Add(upload);var path=upload["path"].TrimEnd('/')+"/"+upload["file"];
                if(upload["overwrite"]=="false"&&Files.ContainsKey(path))return Failure(414);
                Files[path]=bytes!;
                if(ChangeTargetAfterUpload){Files["/share/file.bin"]=Encoding.UTF8.GetBytes("CHANGED!");if(ChangeVersion)_modified["/share/file.bin"]=20;}
                return Success(new{});
            }
            var fields=request.Method==HttpMethod.Get?Fields(request.RequestUri!.Query):Fields(await request.Content!.ReadAsStringAsync(token));Requests.Add(fields);
            var api=fields["api"];var method=fields["method"];
            if(api=="SYNO.API.Info")return Success(new[]{"SYNO.API.Auth","SYNO.FileStation.List","SYNO.FileStation.Download","SYNO.FileStation.MD5","SYNO.FileStation.Upload","SYNO.FileStation.Delete","SYNO.FileStation.Rename"}
                .ToDictionary(x=>x,x=>new{path="entry.cgi",minVersion=1,maxVersion=17}));
            if(api=="SYNO.API.Auth"){Logins.Add(fields);return LoginError!=0?Failure(LoginError):Success(new{sid="sid-"+Logins.Count,did="device-token"});}
            if(api=="SYNO.FileStation.List"&&method=="getinfo")
            {
                if(Expire>0){Expire--;return Failure(SessionError);}if(MetadataError!=0)return Failure(MetadataError);
                var path=JsonSerializer.Deserialize<string[]>(fields["path"])![0];
                if(Writes)
                {
                    if(DropAfterTemporaryUpload&&Uploads>0&&path=="/share/file.bin")throw new HttpRequestException("connection dropped after staging");
                    if(path.Contains(" (冲突副本 ",StringComparison.Ordinal)&&!Files.ContainsKey(path)&&OccupiedConflictNames>0){Files[path]=Bytes.ToArray();OccupiedConflictNames--;}
                    return Files.ContainsKey(path)?Success(new{files=new[]{File(path)}}):Failure(408);
                }
                return path.EndsWith("new.bin",StringComparison.Ordinal)?Failure(408):Success(new{files=new[]{File(path)}});
            }
            if(api=="SYNO.FileStation.List"&&method=="list")
            {
                Lists++;var offset=int.Parse(fields["offset"],System.Globalization.CultureInfo.InvariantCulture);
                var paths=ListingPaths??["/share/a","/share/b","/share/c"];
                return Success(new{total=Empty?0:paths.Length,files=Empty?[]:paths.Skip(offset).Take(2).Select(File).ToArray()});
            }
            if(api=="SYNO.FileStation.List"&&method=="list_share")
            {
                if(CapacityError!=0)return Failure(CapacityError);
                return Success(new{total=Empty?0:1,shares=Empty?[]:new[]{new{name="share",path="/share",additional=new{volume_status=new{freespace=12,totalspace=32}}}}});
            }
            if(api=="SYNO.FileStation.MD5")
            {
                if(method=="start"){Md5Starts++;var id="md5-"+Md5Starts;_md5Tasks[id]=fields["file_path"];return Success(new{taskid=id});}
                var bytes=Writes?Files[_md5Tasks[fields["taskid"]]]:Bytes;
                return Success(new{finished=true,md5=BadMd5?new string('0',32):Convert.ToHexString(MD5.HashData(bytes))});
            }
            if(api=="SYNO.FileStation.Delete")
            {
                if(method=="start"){Files.Remove(JsonSerializer.Deserialize<string[]>(fields["path"])![0]);return Success(new{taskid="delete-task"});}
                return Success(new{finished=true});
            }
            if(api=="SYNO.FileStation.Rename")
            {
                var path=JsonSerializer.Deserialize<string[]>(fields["path"])![0];var name=JsonSerializer.Deserialize<string[]>(fields["name"])![0];
                var destination=path[..(path.LastIndexOf('/')+1)]+name;if(Files.ContainsKey(destination))return Failure(414);
                var bytes=Files[path];Files.Remove(path);Files[destination]=bytes;return Success(new{});
            }
            if(api=="SYNO.FileStation.Download")
            {
                Downloads++;var range=request.Headers.Range?.Ranges.Single();
                if(range is null||IgnoreRange)return new(HttpStatusCode.OK){Content=new ByteArrayContent(Bytes)};
                var from=(int)range.From!.Value;var to=(int)range.To!.Value;var response=new HttpResponseMessage(HttpStatusCode.PartialContent){Content=new ByteArrayContent(Bytes[from..(to+1)])};
                response.Content.Headers.ContentRange=new ContentRangeHeaderValue(from,BadRange?to+1:to,Bytes.Length);return response;
            }
            throw new InvalidOperationException("Unexpected fake API.");
        }
    }
}
