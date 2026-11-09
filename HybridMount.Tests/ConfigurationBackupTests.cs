using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.Win32;

namespace HybridMount.Tests;

/// <summary>验证便携密文、零联网导入、每项提交及停止保护；全部目录和注册表均独立于用户挂载。</summary>
public sealed class ConfigurationBackupTests
{
    private const string Password = "  中文备份密码不可修剪  ";
    private static MountBackupItem Dav(string path = "旧机器目录") => new("中文 WebDAV", path,
        new(FileSourceKind.WebDav, "https://dav.example.test/dav/"), new("account-secret", "password-secret"), true, true);
    private static MountBackupItem S3(string path = "旧机器目录", bool defaults = false) => new("S3 配置", path,
        new(FileSourceKind.S3, "https://s3.example.test", "bucket-test", "目录/prefix", "us-west-2", false, UseDefaultCredentials: defaults),
        new(AccessKey: "access-secret", SecretKey: "key-secret", SessionToken: "token-secret"), false, false);
    private static MountConfigurationBackup Document(params MountBackupItem[] items) => new(1, DateTimeOffset.Parse("2026-11-02T12:00:00Z"), items);

    [Fact]
    public void EncryptedRoundTripPreservesUnicodeOptionsAndCredentialsWithoutRuntimeIdentity()
    {
        var document = Document(Dav(), S3()); var bytes = MountBackupCodec.Encode(document, Password);
        var restored = MountBackupCodec.Decode(bytes, Password);
        Assert.Equal(document.ExportedUtc, restored.ExportedUtc); Assert.Equal(document.Mounts, restored.Mounts);
        var text = Encoding.UTF8.GetString(bytes);
        foreach (var secret in new[] { "account-secret", "password-secret", "access-secret", "key-secret", "token-secret", "dav.example.test" }) Assert.DoesNotContain(secret, text);
        Assert.Equal("挂载配置备份条目", restored.Mounts[0].ToString());
        Assert.DoesNotContain(typeof(MountBackupItem).GetProperties(), property => property.Name is "Id" or "RootIdentity" or "RegistrationStage" or "RemovalPending");
    }
    [Theory]
    [InlineData(Password)][InlineData("")]
    public void ContainerInteroperatesWithIndependentDotNetGcmAndAuthenticatesHeader(string password)
    {
        var bytes = MountBackupCodec.Encode(Document(Dav()), password);
        Assert.Equal("HMCONFIG", Encoding.ASCII.GetString(bytes, 0, 8)); Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8,4)));
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), bytes.AsSpan(12,16), 600000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[bytes.Length-60];
        try
        {
            using var cipher = new AesGcm(key,16); cipher.Decrypt(bytes.AsSpan(28,12),bytes.AsSpan(60),bytes.AsSpan(44,16),plain,bytes.AsSpan(0,44));
            using var json = JsonDocument.Parse(plain); var item = json.RootElement.GetProperty("mounts")[0];
            Assert.Equal("password-secret",item.GetProperty("credentials").GetProperty("password").GetString());
            Assert.False(item.TryGetProperty("id",out _)); Assert.False(item.TryGetProperty("rootIdentity",out _));
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public async Task EmptyPasswordAndRenamedLegacyContainerUseUnchangedFormat(int version)
    {
        await using var test=new Area();var document=Document(Dav()) with{SchemaVersion=version};
        var encoded=MountBackupCodec.Encode(document,"");
        Assert.Equal(version,BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(8,4)));
        Assert.DoesNotContain("password-secret",Encoding.UTF8.GetString(encoded));
        var path=Path.Combine(test.Directory,"old.hmconfig");await File.WriteAllBytesAsync(path,encoded);
        var renamed=Path.ChangeExtension(path,".hymcfg");File.Move(path,renamed);
        var restored=await test.Manager.ReadConfigurationBackupAsync(renamed,"");
        Assert.Equal(document.Mounts,restored.Mounts);Assert.Equal(0,test.Factory.Created);
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(encoded,Password));
        encoded[^1]^=1;Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(encoded,""));
    }
    [Fact]
    public async Task EmptyPasswordAtomicExportReplacesOnlyOnSuccessfulCommit()
    {
        await using var test=new Area();var path=Path.Combine(test.Directory,"backup.hymcfg");
        await File.WriteAllTextAsync(path,"original");var bytes=MountBackupCodec.Encode(Document(Dav()),"");
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>MountBackupCodec.WriteAtomicAsync(path,bytes,cancel.Token,(_,_)=>{cancel.Cancel();cancel.Token.ThrowIfCancellationRequested();}));
        Assert.Equal("original",await File.ReadAllTextAsync(path));
        await MountBackupCodec.WriteAtomicAsync(path,bytes,CancellationToken.None);
        Assert.Equal(Dav(),(await MountBackupCodec.ReadAsync(path,"")).Mounts.Single());
        Assert.Empty(System.IO.Directory.GetFiles(test.Directory,"*.tmp"));
    }
    [Fact]
    public void RepeatedExportsUseFreshSaltAndNonce()
    {
        var document = Document(Dav()); var a = MountBackupCodec.Encode(document,Password); var b = MountBackupCodec.Encode(document,Password);
        Assert.False(a.SequenceEqual(b)); Assert.False(a.AsSpan(12,16).SequenceEqual(b.AsSpan(12,16))); Assert.False(a.AsSpan(28,12).SequenceEqual(b.AsSpan(28,12)));
    }
    [Theory]
    [InlineData(12)][InlineData(28)][InlineData(44)][InlineData(60)]
    public void SaltNonceTagAndCiphertextTamperingReturnsOneSafeError(int offset)
    {
        var bytes=MountBackupCodec.Encode(Document(Dav()),Password);bytes[offset]^=1;
        var error=Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(bytes,Password));Assert.Equal("密码不正确或文件已损坏。",error.Message);
    }
    [Fact]
    public void PasswordWhitespaceIsSignificantAndWrongPasswordDoesNotExposePayload()
    {
        var bytes=MountBackupCodec.Encode(Document(Dav()),Password);
        Assert.Equal("密码不正确或文件已损坏。",Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(bytes,Password.Trim())).Message);
        Assert.Equal("密码不正确或文件已损坏。",Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(bytes,"different-password")).Message);
    }
    [Theory]
    [InlineData(0)][InlineData(8)][InlineData(40)]
    public void InvalidMagicVersionAndLengthAreRejected(int offset)
    {
        var bytes=MountBackupCodec.Encode(Document(Dav()),Password);bytes[offset]^=1;Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(bytes,Password));
    }
    [Fact]
    public void TruncatedOversizedAndTooManyItemsAreRejected()
    {
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(new byte[59],Password));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(new byte[MountBackupCodec.MaximumFileBytes+1],Password));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(Document(Enumerable.Repeat(Dav(),1001).ToArray()),Password));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(Document(Dav() with {Credentials=new(Password:new string('x',MountBackupCodec.MaximumFileBytes))}),Password));
    }
    [Fact]
    public void ShortPasswordAndCanceledEncodingProduceNoBackup()
    {
        Assert.Throws<ArgumentException>(()=>MountBackupCodec.Encode(Document(Dav()),"1234567"));
        Assert.Throws<ArgumentException>(()=>MountBackupCodec.Encode(Document(Dav()),null!));
        Assert.ThrowsAny<OperationCanceledException>(()=>MountBackupCodec.Encode(Document(Dav()),Password,new CancellationToken(true)));
    }
    [Fact]
    public async Task FailedAndCanceledAtomicReplacementKeepOriginalAndRemoveTemporaryCiphertext()
    {
        await using var test=new Area();var path=Path.Combine(test.Directory,"backup.hmconfig");await File.WriteAllTextAsync(path,"original");var bytes=MountBackupCodec.Encode(Document(Dav()),Password);
        using(var blocker=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            var failure=await Record.ExceptionAsync(()=>MountBackupCodec.WriteAtomicAsync(path,bytes,CancellationToken.None));
            Assert.True(failure is IOException or UnauthorizedAccessException,failure?.GetType().Name);
        }
        Assert.Equal("original",await File.ReadAllTextAsync(path));Assert.Empty(System.IO.Directory.GetFiles(test.Directory,"*.tmp"));
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>MountBackupCodec.WriteAtomicAsync(path,bytes,cancel.Token,(_,_)=>{cancel.Cancel();cancel.Token.ThrowIfCancellationRequested();}));
        Assert.Equal("original",await File.ReadAllTextAsync(path));Assert.Empty(System.IO.Directory.GetFiles(test.Directory,"*.tmp"));
    }
    [Fact]
    public async Task ImportSavesFreshIdsDpapiAndOptionsWithoutSourceRegistrationIndexOrUpload()
    {
        await using var test=new Area();var a=test.Folder("a");var b=test.Folder("b");
        var result=await test.Manager.ImportConfigurationsAsync([new(Dav(),a),new(S3(),b)]);
        Assert.False(result.Canceled);Assert.All(result.Results,item=>Assert.Equal("Saved",item.State));
        var definitions=test.Repository.LoadDefinitions();Assert.Equal(2,definitions.Count);Assert.Equal(2,definitions.Select(item=>item.Id).Distinct().Count());
        Assert.Equal(0,test.Factory.Created);Assert.Empty(test.Repository.Uploads());Assert.Empty(test.Repository.LoadOperations());
        foreach(var definition in definitions)
        {
            Assert.Equal(MountRegistrationStage.NotRegistered,definition.RegistrationStage);Assert.False(definition.RemovalPending);
            Assert.Equal(MountPathGuard.FileIdentity(definition.LocalPath),definition.RootIdentity);Assert.Null(MountPathGuard.ReadRoot(definition.LocalPath));Assert.Empty(test.Repository.Entries(definition.Id));
            var source=definition.Source!.Kind==FileSourceKind.WebDav?Dav():S3();Assert.Equal(source.Credentials,test.Manager.GetCredentials(definition.Id));Assert.Equal(source.AutoConnect,definition.AutoConnect);Assert.Equal(source.ThumbnailsEnabled,definition.ThumbnailsEnabled);
        }
        var id=definitions[0].Id;await test.Manager.DisposeAsync();
        await using var restarted=new MountManager(test.Repository,test.Logger,test.Factory);Assert.Equal(2,restarted.Mounts.Count);Assert.Equal(0,test.Factory.Created);Assert.NotEqual(Guid.Empty,id);
    }
    [Fact]
    public async Task DuplicateImportSkipsAndNeverReplacesExistingCredentialsOrFlags()
    {
        await using var test=new Area();var path=test.Folder("a");var first=await test.Manager.ImportConfigurationsAsync([new(Dav() with {AutoConnect=false},path)]);var id=first.Results[0].MountId!.Value;
        var second=await test.Manager.ImportConfigurationsAsync([new(Dav() with {Credentials=new("other","other"),AutoConnect=true},path)]);
        Assert.Equal("Skipped",second.Results[0].State);Assert.Single(test.Manager.Mounts);Assert.Equal(Dav().Credentials,test.Manager.GetCredentials(id));Assert.False(test.Repository.LoadDefinitions()[0].AutoConnect);
    }
    [Fact]
    public async Task BatchDuplicatesAndOverlapArePreviewedAndRecheckedWhileSaving()
    {
        await using var test=new Area();var path=test.Folder("a");MountImportItem[] items=[new(Dav(),path),new(Dav(),path),new(S3(),Path.Combine(path,"nested"))];
        var preview=await test.Manager.PreviewImportConfigurationsAsync(items);Assert.Equal(new[]{"Ready","Duplicate","Conflict"},preview.Select(item=>item.State));Assert.Empty(test.Manager.Mounts);
        var result=await test.Manager.ImportConfigurationsAsync(items);Assert.Equal(new[]{"Saved","Skipped","Failed"},result.Results.Select(item=>item.State));Assert.Single(test.Manager.Mounts);Assert.Equal(0,test.Factory.Created);
    }
    [Fact]
    public async Task SameRemoteCanUseDifferentEmptyDirectoriesAndUnselectedRowsDoNotReservePaths()
    {
        await using var test=new Area();var path=test.Folder("a");MountImportItem[] items=[new(Dav(),path,false),new(Dav(),path),new(Dav(),test.Folder("b"))];
        var preview=await test.Manager.PreviewImportConfigurationsAsync(items);Assert.All(preview,item=>Assert.Equal("Ready",item.State));
        var result=await test.Manager.ImportConfigurationsAsync(items);Assert.Equal(new[]{"Skipped","Saved","Saved"},result.Results.Select(item=>item.State));Assert.Equal(2,test.Manager.Mounts.Count);
    }
    [Fact]
    public async Task ConcurrentImportsRecheckOneDirectoryAndOnlyOneDefinitionWins()
    {
        await using var test=new Area();var path=test.Folder("shared");
        var results=await Task.WhenAll(Enumerable.Range(0,16).Select(_=>test.Manager.ImportConfigurationsAsync([new(Dav(),path)])));
        Assert.Single(results.SelectMany(item=>item.Results),item=>item.State=="Saved");
        Assert.Equal(15,results.SelectMany(item=>item.Results).Count(item=>item.State=="Skipped"));
        Assert.Single(test.Repository.LoadDefinitions());Assert.Single(test.Manager.Mounts);Assert.Equal(0,test.Factory.Created);
    }
    [Fact]
    public async Task InvalidRowDoesNotPreventIndependentValidImport()
    {
        await using var test=new Area();var path=test.Folder("not-empty");await File.WriteAllTextAsync(Path.Combine(path,"user.txt"),"keep");
        var result=await test.Manager.ImportConfigurationsAsync([new(Dav(),path),new(Dav(),test.Folder("empty"))]);
        Assert.Equal(new[]{"Failed","Saved"},result.Results.Select(item=>item.State));Assert.Equal("keep",await File.ReadAllTextAsync(Path.Combine(path,"user.txt")));Assert.Single(test.Manager.Mounts);
    }
    [Theory]
    [InlineData("http://dav.example.test/dav/")][InlineData("https://user:password@dav.example.test/dav/")][InlineData("https://dav.example.test/dav/?token=secret")]
    public async Task InvalidEndpointCannotBeSavedOrContacted(string endpoint)
    {
        await using var test=new Area();var result=await test.Manager.ImportConfigurationsAsync([new(Dav() with {Source=new(FileSourceKind.WebDav,endpoint)},test.Folder("a"))]);
        Assert.Equal("Failed",result.Results[0].State);Assert.Empty(test.Manager.Mounts);Assert.Equal(0,test.Factory.Created);
    }
    [Theory]
    [InlineData(FileSourceKind.LegacyMock)][InlineData(FileSourceKind.Smb)][InlineData(FileSourceKind.Nfs)]
    public async Task ReservedProtocolsAreNotImported(FileSourceKind kind)
    {
        await using var test=new Area();var result=await test.Manager.ImportConfigurationsAsync([new(Dav() with {Source=new(kind)},test.Folder("a"))]);Assert.Equal("Failed",result.Results[0].State);Assert.Empty(test.Manager.Mounts);
    }
    [Fact]
    public async Task MissingDirectorySelfDataRootAndExternalLinkAreRejectedWithoutTouchingTarget()
    {
        await using var test=new Area();var outside=test.Folder("outside");await File.WriteAllTextAsync(Path.Combine(outside,"keep.txt"),"keep");var link=Path.Combine(test.Directory,"link");
        System.IO.Directory.CreateSymbolicLink(link,outside);
        var result=await test.Manager.ImportConfigurationsAsync([new(Dav(),Path.Combine(test.Directory,"missing")),new(Dav(),test.Repository.DataDirectory),new(Dav(),link)]);
        Assert.All(result.Results,item=>Assert.Equal("Failed",item.State));Assert.Equal("keep",await File.ReadAllTextAsync(Path.Combine(outside,"keep.txt")));Assert.Empty(test.Manager.Mounts);
    }
    [Theory]
    [InlineData(Password)][InlineData("")]
    public async Task ExportAndReimportPreserveSettingsButResetRuntimeIdentityAndDefaultChainSecrets(string password)
    {
        await using var source=new Area();await using var target=new Area();
        var created=await source.Manager.ImportConfigurationsAsync([new(Dav(),source.Folder("a")),new(S3(defaults:true),source.Folder("b"))]);
        var file=Path.Combine(source.Directory,"backup.hymcfg");await source.Manager.ExportConfigurationsAsync(created.Results.Select(item=>item.MountId!.Value).ToArray(),password,file);
        var document=await target.Manager.ReadConfigurationBackupAsync(file,password);Assert.Equal(new SourceCredentials(),document.Mounts[1].Credentials);
        var result=await target.Manager.ImportConfigurationsAsync(document.Mounts.Select((item,index)=>new MountImportItem(item,target.Folder("target"+index))).ToArray());
        Assert.All(result.Results,item=>Assert.Equal("Saved",item.State));Assert.Empty(created.Results.Select(item=>item.MountId).Intersect(result.Results.Select(item=>item.MountId)));Assert.Equal(0,source.Factory.Created+target.Factory.Created);
    }
    [Fact]
    public async Task CorruptDpapiDoesNotCreateOrOverwriteBackup()
    {
        await using var test=new Area();var result=await test.Manager.ImportConfigurationsAsync([new(Dav(),test.Folder("a"))]);var id=result.Results[0].MountId!.Value;
        await File.WriteAllBytesAsync(Path.Combine(test.Repository.DataDirectory,"Credentials",id.ToString("N")+".bin"),[1,2,3]);var file=Path.Combine(test.Directory,"backup.hmconfig");await File.WriteAllTextAsync(file,"original");
        await Assert.ThrowsAnyAsync<CryptographicException>(()=>test.Manager.ExportConfigurationsAsync([id],Password,file));Assert.Equal("original",await File.ReadAllTextAsync(file));
    }
    [Fact]
    public async Task CredentialSaveFailureDoesNotLeaveDefinitionOrIndex()
    {
        await using var test=new Area();await File.WriteAllTextAsync(Path.Combine(test.Repository.DataDirectory,"Credentials"),"block directory");
        var result=await test.Manager.ImportConfigurationsAsync([new(Dav(),test.Folder("a"))]);Assert.Equal("Failed",result.Results[0].State);Assert.Empty(test.Repository.LoadDefinitions());Assert.Empty(test.Manager.Mounts);Assert.Empty(test.Repository.Uploads());
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CancelOrShutdownBetweenItemsKeepsFirstAndStopsLaterSaves(bool shutdown)
    {
        await using var test=new Area();using var cancel=new CancellationTokenSource();
        var progress=new ImmediateProgress(item=>{if(item.State=="Saved"){if(shutdown)test.Manager.RequestStop();else cancel.Cancel();}});
        var result=await test.Manager.ImportConfigurationsAsync([new(Dav(),test.Folder("a")),new(Dav(),test.Folder("b"))],cancel.Token,progress);
        Assert.True(result.Canceled);Assert.Equal(new[]{"Saved","Canceled"},result.Results.Select(item=>item.State));Assert.Single(test.Repository.LoadDefinitions());Assert.Equal(0,test.Factory.Created);
    }
    [Fact]
    public async Task CurrentStartupSkipsImportedRowsAndNextProcessRestoresAutoConnectChoice()
    {
        await using var test=new Area();
        await test.Manager.ImportConfigurationsAsync([new(Dav(),test.Folder("auto")),new(S3(),test.Folder("manual"))]);
        await test.Manager.StartAsync();Assert.Equal(0,test.Factory.Created);
        await test.Manager.DisposeAsync();
        await using var restarted=new MountManager(test.Repository,test.Logger,test.Factory);
        await restarted.StartAsync();Assert.Equal(1,test.Factory.Created);
        Assert.All(test.Repository.LoadDefinitions(),definition=>Assert.Null(MountPathGuard.ReadRoot(definition.LocalPath)));
    }
    [Fact]
    public async Task StoppedManagerRejectsAllBackupOperations()
    {
        await using var test=new Area();test.Manager.RequestStop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>test.Manager.ImportConfigurationsAsync([new(Dav(),test.Folder("a"))]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>test.Manager.ReadConfigurationBackupAsync("unused",Password));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>test.Manager.ExportConfigurationsAsync([],Password,"unused"));Assert.Empty(test.Repository.LoadDefinitions());
    }

    private sealed class ImmediateProgress(Action<MountImportResult> report):IProgress<MountImportResult>{public void Report(MountImportResult value)=>report(value);}
    private sealed class NeverConnectFactory:IFileSourceFactory
    {
        public int Created;
        public Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token){Interlocked.Increment(ref Created);throw new InvalidOperationException("备份或导入不允许建立文件源。");}
    }
    private sealed class Area:IAsyncDisposable
    {
        public string Directory {get;}=Path.Combine(Path.GetTempPath(),"HybridMount.BackupTests",Guid.NewGuid().ToString("N"));
        private readonly string _registry=@"Software\HybridMount.NET.Tests\Backup-"+Guid.NewGuid().ToString("N");
        public MountRepository Repository {get;}
        public LoggerService Logger {get;}
        public MountManager Manager {get;}
        public NeverConnectFactory Factory {get;}=new();
        public Area(){System.IO.Directory.CreateDirectory(Directory);Repository=new(Path.Combine(Directory,"data"),_registry);Logger=new(Path.Combine(Directory,"logs.db"));Manager=new(Repository,Logger,Factory);}
        public string Folder(string name){var path=Path.Combine(Directory,name);System.IO.Directory.CreateDirectory(path);return path;}
        public async ValueTask DisposeAsync(){await Manager.DisposeAsync();await Logger.DisposeAsync();Registry.CurrentUser.DeleteSubKeyTree(_registry,false);System.IO.Directory.Delete(Directory,true);}
    }
}
