using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>真实 SDK 错误响应、只读测试、取消与恢复隔离；诊断禁止泄露响应正文。</summary>
public sealed class ConnectionTests
{
    [Theory]
    [InlineData(401,"Unauthorized",ConnectionFailureKind.Authentication)]
    [InlineData(403,"AccessDenied",ConnectionFailureKind.Permission)]
    [InlineData(403,"SignatureDoesNotMatch",ConnectionFailureKind.Signature)]
    [InlineData(400,"AuthorizationHeaderMalformed",ConnectionFailureKind.Signature)]
    [InlineData(404,"NoSuchBucket",ConnectionFailureKind.NotFound)]
    public async Task StructuredS3FailuresKeepSafeFieldsAndDoNotGuessRegion(int status,string code,ConnectionFailureKind kind)
    {
        await using var server=new HttpTestServer();
        server.Handler=_=>Task.FromResult(HttpTestServer.Response.Text(status,
            $"<Error><Code>{code}</Code><Message>NEVER-LOG-RAW-SECRETS</Message><RequestId>safe-request-123</RequestId><Region>cn-test-1</Region></Error>",
            new(){["x-amz-bucket-region"]="cn-test-1",["x-amz-request-id"]="safe-request-123"}));
        var options=new FileSourceConfiguration(FileSourceKind.S3,server.Address.AbsoluteUri,"test-bucket",Region:"us-east-1",AllowHttp:true);
        var result=await new FileSourceFactory().TestConnectionAsync(options,S3Fixture.Credentials);
        Assert.False(result.Success);var diagnostic=Assert.IsType<ConnectionDiagnostic>(result.Diagnostic);
        Assert.Equal(kind,diagnostic.Kind);Assert.Equal(status,diagnostic.HttpStatus);Assert.Equal(code,diagnostic.ServiceCode);
        Assert.Equal("ProbeAsync",diagnostic.Stage);Assert.Equal("safe-request-123",diagnostic.RequestId);Assert.Equal("cn-test-1",diagnostic.SuggestedRegion);
        Assert.DoesNotContain("NEVER-LOG-RAW-SECRETS",result.Message);Assert.DoesNotContain(S3Fixture.Credentials.AccessKey,result.Message);
        Assert.DoesNotContain(S3Fixture.Credentials.SecretKey,result.Message);Assert.DoesNotContain("AWS4-HMAC-SHA256",result.Message);
        Assert.Single(server.Requests);S3Fixture.VerifySignature(Assert.Single(server.Requests));Assert.Equal("us-east-1",options.Region);
    }
    [Fact]
    public async Task NonStandard401BodyDoesNotLeakOrInventRegion()
    {
        await using var server=new HttpTestServer();server.Handler=_=>Task.FromResult(HttpTestServer.Response.Text(401,"<html>NEVER-LOG-RAW-SECRETS</html>"));
        var result=await new FileSourceFactory().TestConnectionAsync(new(FileSourceKind.S3,server.Address.AbsoluteUri,"test-bucket",AllowHttp:true),S3Fixture.Credentials);
        Assert.False(result.Success);Assert.Equal(401,result.Diagnostic?.HttpStatus);
        Assert.Equal(ConnectionFailureKind.Authentication,result.Diagnostic?.Kind);Assert.Null(result.Diagnostic?.SuggestedRegion);
        Assert.DoesNotContain("NEVER-LOG-RAW-SECRETS",result.Message);Assert.Single(server.Requests);
    }
    [Fact]
    public async Task HtmlSuccessResponseIsNotReportedAsAWorkingS3Connection()
    {
        await using var server=new HttpTestServer();server.Handler=_=>Task.FromResult(HttpTestServer.Response.Text(200,"<html>NEVER-LOG-RAW-SECRETS</html>"));
        var result=await new FileSourceFactory().TestConnectionAsync(new(FileSourceKind.S3,server.Address.AbsoluteUri,"test-bucket",AllowHttp:true),S3Fixture.Credentials);
        Assert.False(result.Success);Assert.Equal(ConnectionFailureKind.Service,result.Diagnostic?.Kind);
        Assert.DoesNotContain("NEVER-LOG-RAW-SECRETS",result.Message);Assert.Single(server.Requests);
    }
    [Fact]
    public async Task RegionFromErrorXmlIsOnlyASuggestion()
    {
        await using var server=new HttpTestServer();server.Handler=_=>Task.FromResult(HttpTestServer.Response.Text(400,
            "<Error><Code>AuthorizationHeaderMalformed</Code><Region>eu-test-1</Region></Error>"));
        var result=await new FileSourceFactory().TestConnectionAsync(new(FileSourceKind.S3,server.Address.AbsoluteUri,"test-bucket",AllowHttp:true),S3Fixture.Credentials);
        Assert.Equal("eu-test-1",result.Diagnostic?.SuggestedRegion);Assert.Single(server.Requests);
    }
    [Fact(Timeout=15000)]
    public async Task TlsFailureIsDistinctFromNetworkAndCertificateIsNotBypassed()
    {
        using var rsa=System.Security.Cryptography.RSA.Create(2048);
        var request=new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost",rsa,System.Security.Cryptography.HashAlgorithmName.SHA256,System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
        var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var peer=Task.Run(async()=>
        {
            using var client=await listener.AcceptTcpClientAsync(stop.Token);
            await using var ssl=new System.Net.Security.SslStream(client.GetStream());
            try { await ssl.AuthenticateAsServerAsync(new(){ServerCertificate=certificate},stop.Token); }
            catch(Exception error)when(error is IOException or System.Security.Authentication.AuthenticationException) { }
        });
        try
        {
            var result=await new FileSourceFactory().TestConnectionAsync(new(FileSourceKind.S3,$"https://127.0.0.1:{port}","test-bucket"),S3Fixture.Credentials,stop.Token);
            Assert.False(result.Success);Assert.Equal(ConnectionFailureKind.Certificate,result.Diagnostic?.Kind);
            Assert.Null(result.Diagnostic?.HttpStatus);await peer;
        }
        finally { listener.Stop(); }
        var offline=await new FileSourceFactory().TestConnectionAsync(new(FileSourceKind.S3,$"http://127.0.0.1:{port}","test-bucket",AllowHttp:true),S3Fixture.Credentials);
        Assert.Equal(ConnectionFailureKind.Network,offline.Diagnostic?.Kind);
    }
    [Fact]
    public async Task ReadOnlyTestsNeverRestoreMultipartOrPersistMountOrWriteRemote()
    {
        await using var fixture=new S3Fixture();Directory.CreateDirectory(Path.Combine(fixture.Area,"work"));
        var journal=Path.Combine(fixture.Area,"work","known.multipart.json");
        await File.WriteAllTextAsync(journal,JsonSerializer.Serialize(new{Bucket="test-bucket",Key="remote/partial.bin",UploadId="known"}));
        fixture.Uploads["known"]=new("remote/partial.bin",new());
        await using(var source=fixture.Source())await source.ProbeAsync();
        var result=await new FileSourceFactory().TestConnectionAsync(fixture.Options,S3Fixture.Credentials);
        Assert.True(result.Success,result.Message);Assert.True(File.Exists(journal));Assert.True(fixture.Uploads.ContainsKey("known"));
        Assert.All(fixture.Server.Requests,r=>Assert.Equal("GET",r.Method));
        await using var dav=new DavFixture();var davResult=await new FileSourceFactory().TestConnectionAsync(dav.Options,new("user","app-password"));
        Assert.True(davResult.Success,davResult.Message);Assert.All(dav.Server.Requests,r=>Assert.Contains(r.Method,new[]{"OPTIONS","PROPFIND"}));
        Assert.False(Directory.Exists(Path.Combine(dav.Area,"cache")));
    }
    [Fact(Timeout=15000)]
    public async Task ProbeTimeoutAndCancellationCompleteWithoutWrites()
    {
        await using var server=new HttpTestServer();
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Handler=async _=>{await release.Task;return new(401,[]);};
        var options=new FileSourceConfiguration(FileSourceKind.S3,server.Address.AbsoluteUri,"test-bucket",AllowHttp:true);
        try
        {
            var result=await new FileSourceFactory().TestConnectionAsync(options,S3Fixture.Credentials,timeout:TimeSpan.FromMilliseconds(150));
            Assert.False(result.Success);Assert.Contains("超时",result.Message);
            using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new FileSourceFactory().TestConnectionAsync(options,S3Fixture.Credentials,cancel.Token));
            Assert.All(server.Requests,r=>Assert.Equal("GET",r.Method));
        }
        finally { release.TrySetResult(); }
    }
    [Fact(Timeout=60000)]
    public async Task CorruptKnownRecoveryDoesNotBlockReadableMount()
    {
        await using var fixture=new S3Fixture();var local=Path.Combine(fixture.Area,"view");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repo=new MountRepository(Path.Combine(fixture.Area,"data"),registry);var id=Guid.NewGuid();
        var definition=new MountDefinition(id,"recover",local,false,RootIdentity:MountPathGuard.FileIdentity(local),Source:fixture.Options,RegistrationStage:MountRegistrationStage.NotRegistered);
        repo.SaveDefinition(definition);new CredentialStore(repo.DataDirectory).Save(id,S3Fixture.Credentials);
        var work=Path.Combine(repo.DataDirectory,"RangeCache",id.ToString("N"));Directory.CreateDirectory(work);await File.WriteAllTextAsync(Path.Combine(work,"broken.multipart.json"),"invalid-json");
        await using var logger=new LoggerService(Path.Combine(fixture.Area,"log.db"));await using var manager=new MountManager(repo,logger);
        try
        {
            await manager.ConnectAsync(id);Assert.Equal("已连接",Assert.Single(manager.Mounts).Status);
            Assert.False(File.Exists(Path.Combine(work,"broken.multipart.json")));Assert.True(File.Exists(Path.Combine(work,"broken.multipart.json.bad")));
            var bytes=await Task.Run(()=>File.ReadAllBytes(Path.Combine(local,"文件.bin"))).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(fixture.Objects["remote/文件.bin"].Bytes,bytes);
        }
        finally
        {
            Assert.Equal("Completed",(await WebDavTests.Finished(manager,manager.Queue(id,"clearAndRemove"))).State);
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
        }
    }
}
