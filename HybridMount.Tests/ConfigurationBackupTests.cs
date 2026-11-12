using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.Win32;

namespace HybridMount.Tests;

/// <summary>验证便携 JSON／凭据密文、零联网导入、每项提交及停止保护；全部目录和注册表均独立于用户挂载。</summary>
public sealed class ConfigurationBackupTests
{
    private const string Password = "  中文备份密码不可修剪  ";
    private static MountBackupItem Dav(string path = "旧机器目录") => new("中文 WebDAV", path,
        new(FileSourceKind.WebDav, "https://dav.example.test/dav/"), new("account-secret", "password-secret"), true, true);
    private static MountBackupItem S3(string path = "旧机器目录", bool defaults = false) => new("S3 配置", path,
        new(FileSourceKind.S3, "https://s3.example.test", "bucket-test", "目录/prefix", "us-west-2", false, UseDefaultCredentials: defaults),
        new(AccessKey: "access-secret", SecretKey: "key-secret", SessionToken: "token-secret"), false, false);
    private static MountConfigurationBackup Document(params MountBackupItem[] items) => new(1, DateTimeOffset.Parse("2026-11-02T12:00:00Z"), items);

    [Theory]
    [InlineData(Password)][InlineData("")]
    public void JsonRoundTripKeepsNonSensitiveSettingsReadableAndProtectsOnlyCredentials(string password)
    {
        var document = Document(Dav() with {ExclusionRules=FileExclusionRules.Defaults()}, S3());
        var bytes = MountBackupCodec.Encode(document,password); var restored=MountBackupCodec.Decode(bytes,password);
        Assert.Equal(document.ExportedUtc,restored.ExportedUtc);Assert.Equal(document.Mounts,restored.Mounts);
        var text=Encoding.UTF8.GetString(bytes);using var json=JsonDocument.Parse(bytes);
        Assert.Equal("hmxbk",json.RootElement.GetProperty("format").GetString());Assert.Equal(1,json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(password.Length==0?"none":"password",json.RootElement.GetProperty("protection").GetProperty("mode").GetString());
        Assert.Contains("dav.example.test",text);Assert.Contains("exclusionRules",text);Assert.Contains("中文 WebDAV",text);
        foreach(var secret in new[]{"account-secret","password-secret","access-secret","key-secret","token-secret"})
            if(password.Length==0)Assert.Contains(secret,text);else Assert.DoesNotContain(secret,text);
        Assert.Equal("挂载配置备份条目",restored.Mounts[0].ToString());
        Assert.DoesNotContain(typeof(MountBackupItem).GetProperties(),property=>property.Name is "Id" or "RootIdentity" or "RegistrationStage" or "RemovalPending");
    }
    [Fact]
    public void CredentialBlockInteroperatesWithIndependentDotNetGcm()
    {
        var root=Node(MountBackupCodec.Encode(Document(Dav()),Password));var protection=root["protection"]!;var mount=root["mounts"]![0]!;var block=mount["protectedCredentials"]!;
        var key=Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password),Convert.FromBase64String((string)protection["salt"]!),600000,HashAlgorithmName.SHA256,32);
        var ciphertext=Convert.FromBase64String((string)block["ciphertext"]!);var plain=new byte[ciphertext.Length];
        try
        {
            using var cipher=new AesGcm(key,16);
            var aad=Encoding.UTF8.GetBytes($"hmxbk|1|{Guid.Parse((string)mount["backupEntryId"]!):D}|credentials");
            cipher.Decrypt(Convert.FromBase64String((string)block["nonce"]!),ciphertext,Convert.FromBase64String((string)block["tag"]!),plain,aad);
            using var payload=JsonDocument.Parse(plain);Assert.Equal("password-secret",payload.RootElement.GetProperty("credentials").GetProperty("password").GetString());
            Assert.Null(mount["credentials"]);Assert.Null(mount["sqsCredentials"]);
        }
        finally {CryptographicOperations.ZeroMemory(key);CryptographicOperations.ZeroMemory(plain);}
    }
    [Fact]
    public void EncryptedBackupAllowsEditingPublicOptionsAndReorderingWholeEntries()
    {
        var root=Node(MountBackupCodec.Encode(Document(Dav(),S3()),Password));var mounts=root["mounts"]!.AsArray();
        mounts[0]!["source"]!["endpoint"]="https://new.example.test/中文/";mounts[0]!["localPath"]="D:/new";
        mounts[0]!["exclusionRules"]=JsonNode.Parse("[{\"match\":\"suffix\",\"text\":\".tmp\"}]");
        var first=mounts[0]!.DeepClone();mounts.RemoveAt(0);mounts.Add(first);
        var result=MountBackupCodec.Decode(Bytes(root),Password);Assert.Equal(S3(),result.Mounts[0]);
        Assert.Equal("https://new.example.test/中文/",result.Mounts[1].Source.Endpoint);Assert.Equal(Dav().Credentials,result.Mounts[1].Credentials);
        Assert.Equal(".tmp",Assert.Single(result.Mounts[1].ExclusionRules!).Text);
    }
    [Theory]
    [InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)]
    public async Task OldBinaryVersionsAreRejectedEvenAfterRenaming(int version)
    {
        await using var test=new Area();var file=Path.Combine(test.Directory,"renamed.hmxbk");
        var old=new byte[60];Encoding.ASCII.GetBytes("HMCONFIG").CopyTo(old,0);BinaryPrimitives.WriteInt32LittleEndian(old.AsSpan(8,4),version);
        await File.WriteAllBytesAsync(file,old);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>test.Manager.ReadConfigurationBackupAsync(file,Password));
        Assert.Contains("旧格式",error.Message);Assert.Equal(0,test.Factory.Created);
    }
    [Theory]
    [InlineData(".hymcfg")][InlineData(".hmconfig")][InlineData(".json")]
    public async Task OnlyNewExtensionIsAcceptedByReadAndWrite(string extension)
    {
        await using var test=new Area();var file=Path.Combine(test.Directory,"backup"+extension);var bytes=MountBackupCodec.Encode(Document(Dav()),"");
        await File.WriteAllBytesAsync(file,bytes);
        await Assert.ThrowsAsync<InvalidDataException>(()=>MountBackupCodec.ReadAsync(file,""));
        await Assert.ThrowsAsync<InvalidDataException>(()=>MountBackupCodec.WriteAtomicAsync(file,bytes,default));
    }
    [Theory]
    [InlineData(Password)][InlineData("")]
    public async Task ReadSessionDetectsProtectionAndKeepsOriginalSnapshotForPasswordRetries(string password)
    {
        await using var test=new Area();var file=Path.Combine(test.Directory,"backup.hmxbk");await File.WriteAllBytesAsync(file,MountBackupCodec.Encode(Document(Dav()),password));
        using var session=await test.Manager.OpenConfigurationBackupAsync(file);Assert.Equal(password.Length!=0,session.RequiresPassword);
        await File.WriteAllBytesAsync(file,MountBackupCodec.Encode(Document(S3()),password));
        if(session.RequiresPassword)await Assert.ThrowsAsync<InvalidDataException>(()=>test.Manager.DecodeConfigurationBackupAsync(session,"wrong-password"));
        Assert.Equal(Dav(),Assert.Single((await test.Manager.DecodeConfigurationBackupAsync(session,password)).Mounts));
        session.Dispose();session.Dispose();Assert.Throws<ObjectDisposedException>(()=>session.Decode(password));Assert.Equal(0,test.Factory.Created);
    }
    [Fact]
    public async Task ReadSessionRejectsConcurrentWriterAndHonorsCancellation()
    {
        await using var test=new Area();var file=Path.Combine(test.Directory,"backup.hmxbk");await File.WriteAllBytesAsync(file,MountBackupCodec.Encode(Document(Dav()),""));
        using(var writer=new FileStream(file,FileMode.Open,FileAccess.Write,FileShare.ReadWrite))
            await Assert.ThrowsAsync<IOException>(()=>MountBackupCodec.OpenAsync(file));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>MountBackupCodec.OpenAsync(file,new CancellationToken(true)));
        using var session=await MountBackupCodec.OpenAsync(file);Assert.ThrowsAny<OperationCanceledException>(()=>session.Decode("",new CancellationToken(true)));
    }
    [Fact]
    public void ReadSessionDisposalClearsOwnedBytes()
    {
        var bytes=MountBackupCodec.Encode(Document(Dav()),"");var session=new MountBackupReadSession(bytes,false);
        Assert.Equal(Dav(),session.Decode("").Mounts.Single());session.Dispose();Assert.All(bytes,value=>Assert.Equal((byte)0,value));
    }
    [Fact]
    public void RepeatedExportsUseFreshSaltEntryIdsAndNonces()
    {
        var a=Node(MountBackupCodec.Encode(Document(Dav(),S3()),Password));var b=Node(MountBackupCodec.Encode(Document(Dav(),S3()),Password));
        Assert.NotEqual((string)a["protection"]!["salt"]!,(string)b["protection"]!["salt"]!);
        Assert.NotEqual((string)a["mounts"]![0]!["backupEntryId"]!,(string)b["mounts"]![0]!["backupEntryId"]!);
        var nonces=new[]{a["mounts"]![0],a["mounts"]![1],b["mounts"]![0],b["mounts"]![1]}.Select(x=>(string)x!["protectedCredentials"]!["nonce"]!).ToArray();Assert.Equal(4,nonces.Distinct().Count());
    }
    [Theory]
    [InlineData("salt")][InlineData("nonce")][InlineData("tag")][InlineData("ciphertext")]
    public void CredentialTamperingReturnsOneSafeError(string field)
    {
        var root=Node(MountBackupCodec.Encode(Document(Dav()),Password));var owner=field=="salt"?root["protection"]!:root["mounts"]![0]!["protectedCredentials"]!;
        var bytes=Convert.FromBase64String((string)owner[field]!);bytes[0]^=1;owner[field]=Convert.ToBase64String(bytes);
        Assert.Equal("密码不正确或文件已损坏。",Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(Bytes(root),Password)).Message);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ExchangingCredentialBlocksOrChangingEntryIdCannotChangeCredentialOwnership(bool changeId)
    {
        var root=Node(MountBackupCodec.Encode(Document(Dav(),S3()),Password));var mounts=root["mounts"]!;
        if(changeId)mounts[0]!["backupEntryId"]=Guid.NewGuid().ToString("D");
        else {var first=mounts[0]!["protectedCredentials"]!.DeepClone();mounts[0]!["protectedCredentials"]=mounts[1]!["protectedCredentials"]!.DeepClone();mounts[1]!["protectedCredentials"]=first;}
        Assert.Equal("密码不正确或文件已损坏。",Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(Bytes(root),Password)).Message);
    }
    [Theory]
    [InlineData("version")][InlineData("format")][InlineData("iterations")][InlineData("algorithm")][InlineData("mode")][InlineData("length")][InlineData("mixed")][InlineData("missing")][InlineData("duplicateId")][InlineData("reusedNonce")]
    public void InvalidEnvelopeAndMixedProtectionAreRejectedBeforePasswordWork(string kind)
    {
        var root=Node(MountBackupCodec.Encode(Document(Dav(),S3()),Password));var protection=root["protection"]!;var mounts=root["mounts"]!;
        switch(kind)
        {
            case "version":root["schemaVersion"]=2;break;case "format":root["format"]="HMCONFIG";break;
            case "iterations":protection["iterations"]=int.MaxValue;break;case "algorithm":protection["cipher"]="AES-CBC";break;
            case "mode":protection["mode"]="none";break;case "length":mounts[0]!["protectedCredentials"]!["nonce"]="AA==";break;
            case "mixed":mounts[0]!["credentials"]=JsonNode.Parse("{}");break;case "missing":mounts[0]!.AsObject().Remove("protectedCredentials");break;
            case "duplicateId":mounts[1]!["backupEntryId"]=mounts[0]!["backupEntryId"]!.DeepClone();break;
            case "reusedNonce":mounts[1]!["protectedCredentials"]!["nonce"]=mounts[0]!["protectedCredentials"]!["nonce"]!.DeepClone();break;
        }
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(Bytes(root),Password));
    }
    [Theory]
    [InlineData("root")][InlineData("source")][InlineData("credentials")][InlineData("rule")][InlineData("protection")]
    public void UnknownAndDuplicatePropertiesAtEveryLevelAreRejected(string level)
    {
        var root=Node(MountBackupCodec.Encode(Document(Dav() with{ExclusionRules=FileExclusionRules.Defaults()}),""));
        var owner=level switch{"source"=>root["mounts"]![0]!["source"]!,"credentials"=>root["mounts"]![0]!["credentials"]!,"rule"=>root["mounts"]![0]!["exclusionRules"]![0]!,"protection"=>root["protection"]!,_=>root};
        owner["enabled"]=true;Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(Bytes(root),""));owner.AsObject().Remove("enabled");
        var pair=owner.AsObject().First();var raw=owner.ToJsonString();var duplicate=raw[..^1]+","+JsonSerializer.Serialize(pair.Key)+":"+pair.Value!.ToJsonString()+"}";
        var text=root.ToJsonString().Replace(raw,duplicate,StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(Encoding.UTF8.GetBytes(text),""));
    }
    [Fact]
    public void PasswordWhitespaceIsSignificantAndPlainJsonDoesNotNeedAPassword()
    {
        var bytes=MountBackupCodec.Encode(Document(Dav()),Password);
        Assert.Equal("密码不正确或文件已损坏。",Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(bytes,Password.Trim())).Message);
        Assert.Equal("密码不正确或文件已损坏。",Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(bytes,"different-password")).Message);
        Assert.Contains("需要密码",Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(bytes,"")).Message);
        var plain=MountBackupCodec.Encode(Document(Dav()),"");Assert.Equal(Dav(),MountBackupCodec.Decode(plain,"").Mounts.Single());
        Assert.Equal(Dav(),MountBackupCodec.Decode(new byte[]{0xef,0xbb,0xbf}.Concat(plain).ToArray(),"").Mounts.Single());
    }
    [Fact]
    public void TruncatedOversizedTooDeepAndTooManyItemsAreRejected()
    {
        var valid=MountBackupCodec.Encode(Document(Dav()),"");Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(valid[..^1],""));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(new byte[MountBackupCodec.MaximumFileBytes+1],Password));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(Document(Enumerable.Repeat(Dav(),1001).ToArray()),Password));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(Document(Dav() with{Credentials=new(Password:new string('x',MountBackupCodec.MaximumFileBytes))}),""));
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(Encoding.UTF8.GetBytes(new string('[',20)+new string(']',20)),""));
    }
    [Fact]
    public void ShortPasswordAndCanceledEncodingProduceNoBackup()
    {
        Assert.Throws<ArgumentException>(()=>MountBackupCodec.Encode(Document(Dav()),"1234567"));Assert.Throws<ArgumentException>(()=>MountBackupCodec.Encode(Document(Dav()),null!));
        Assert.ThrowsAny<OperationCanceledException>(()=>MountBackupCodec.Encode(Document(Dav()),Password,new CancellationToken(true)));
    }
    [Theory]
    [InlineData(Password)][InlineData("")]
    public async Task FailedAndCanceledAtomicReplacementKeepOriginalAndRemoveTemporaryBytes(string password)
    {
        await using var test=new Area();var path=Path.Combine(test.Directory,"backup.hmxbk");await File.WriteAllTextAsync(path,"original");var bytes=MountBackupCodec.Encode(Document(Dav()),password);
        using(var blocker=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
        {var failure=await Record.ExceptionAsync(()=>MountBackupCodec.WriteAtomicAsync(path,bytes,default));Assert.True(failure is IOException or UnauthorizedAccessException,failure?.GetType().Name);}
        Assert.Equal("original",await File.ReadAllTextAsync(path));Assert.Empty(System.IO.Directory.GetFiles(test.Directory,"*.tmp"));
        using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>MountBackupCodec.WriteAtomicAsync(path,bytes,cancel.Token,(_,_)=>{cancel.Cancel();cancel.Token.ThrowIfCancellationRequested();}));
        Assert.Equal("original",await File.ReadAllTextAsync(path));Assert.Empty(System.IO.Directory.GetFiles(test.Directory,"*.tmp"));
        await MountBackupCodec.WriteAtomicAsync(path,bytes,default);Assert.Equal(Dav(),(await MountBackupCodec.ReadAsync(path,password)).Mounts.Single());
    }
    [Fact]
    public async Task ConcurrentSessionDisposeEitherPreventsDecodeOrLetsItFinishWithoutCorruptingSnapshot()
    {
        var encoded=MountBackupCodec.Encode(Document(Dav()),Password);
        for(var attempt=0;attempt<8;attempt++)
        {
            var owned=encoded.ToArray();var session=new MountBackupReadSession(owned,true);
            var decoding=Task.Run(()=>session.Decode(Password));
            await Task.Delay(attempt);session.Dispose();
            try {Assert.Equal(Dav(),Assert.Single((await decoding).Mounts));}
            catch(ObjectDisposedException) { }
            Assert.All(owned,value=>Assert.Equal((byte)0,value));
        }
    }
    [Theory]
    [InlineData("source")][InlineData("credentials")][InlineData("mounts")][InlineData("protection")]
    public void MissingRequiredStructureFailsSafely(string field)
    {
        var root=Node(MountBackupCodec.Encode(Document(Dav()),""));
        if(field is "source" or "credentials")root["mounts"]![0]![field]=null;else root[field]=null;
        var error=Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Decode(Bytes(root),""));Assert.DoesNotContain("password-secret",error.Message);
    }
    private static JsonObject Node(byte[] bytes)=>JsonNode.Parse(bytes)!.AsObject();
    private static byte[] Bytes(JsonNode node)=>Encoding.UTF8.GetBytes(node.ToJsonString());

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
        var file=Path.Combine(source.Directory,"backup.hmxbk");await source.Manager.ExportConfigurationsAsync(created.Results.Select(item=>item.MountId!.Value).ToArray(),password,file);
        var document=await target.Manager.ReadConfigurationBackupAsync(file,password);Assert.Equal(new SourceCredentials(),document.Mounts[1].Credentials);
        var result=await target.Manager.ImportConfigurationsAsync(document.Mounts.Select((item,index)=>new MountImportItem(item,target.Folder("target"+index))).ToArray());
        Assert.All(result.Results,item=>Assert.Equal("Saved",item.State));Assert.Empty(created.Results.Select(item=>item.MountId).Intersect(result.Results.Select(item=>item.MountId)));Assert.Equal(0,source.Factory.Created+target.Factory.Created);
    }
    [Fact]
    public async Task CorruptDpapiDoesNotCreateOrOverwriteBackup()
    {
        await using var test=new Area();var result=await test.Manager.ImportConfigurationsAsync([new(Dav(),test.Folder("a"))]);var id=result.Results[0].MountId!.Value;
        await File.WriteAllBytesAsync(Path.Combine(test.Repository.DataDirectory,"Credentials",id.ToString("N")+".bin"),[1,2,3]);var file=Path.Combine(test.Directory,"backup.hmxbk");await File.WriteAllTextAsync(file,"original");
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
