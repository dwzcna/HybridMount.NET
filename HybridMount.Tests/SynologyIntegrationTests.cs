using System.Net;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting.Synology;

namespace HybridMount.Tests;

/// <summary>内存会话、真实调度器和加密备份验证，不访问外部 DSM 或正式配置。</summary>
public sealed class SynologyIntegrationTests:IDisposable
{
    private readonly string _area=Path.Combine(Path.GetTempPath(),"HybridMount.SynologyIntegration",Guid.NewGuid().ToString("N"));
    private SynologyFileSource Source(SynologyFakeDsm server,SourceCredentials? keys=null,string? otp=null)=>new(SynologyTests.Options,keys??new("user","password"),new WholeFileCache(_area),server,otp);
    public void Dispose(){if(Directory.Exists(_area))Directory.Delete(_area,true);}
    [Fact]
    public async Task OtpWithoutDeviceTokensIsHandedOffExactlyOnceThroughActualFactory()
    {
        var firstServer=new SynologyFakeDsm{AuthMax=3,RequireOtp=true};var owner=Source(firstServer,otp:"123456");await owner.ProbeAsync();
        await using var ticket=new SynologySessionTicket(owner);var nextServer=new SynologyFakeDsm{AuthMax=3,RequireOtp=true};
        var factory=new FileSourceFactory();factory.Register(FileSourceKind.Smb,(options,credentials,work)=>new SynologyFileSource(options with{Kind=FileSourceKind.Synology},credentials,new WholeFileCache(work),nextServer,null));
        var repo=new MountRepository(Path.Combine(_area,"repo"),definitions:new MemoryStore());
        var definition=new MountDefinition(Guid.NewGuid(),"NAS",_area,Source:SynologyTests.Options with{Kind=FileSourceKind.Smb});new CredentialStore(repo.DataDirectory).Save(definition.Id,new("user","password"));
        using(SynologySessionTicket.Use(ticket))
        {
            await using var connected=await factory.CreateAsync(definition,repo,false,default);Assert.Empty(nextServer.Logins);
            var entry=await connected.GetMetadataAsync("file.bin");Assert.Contains(";md5:",entry.Version);
        }
        Assert.True(ticket.IsReleased);Assert.DoesNotContain(firstServer.Requests,x=>x["api"]=="SYNO.API.Auth"&&x["method"]=="logout");
        await using var second=Source(new SynologyFakeDsm{AuthMax=3,RequireOtp=true});await ticket.ApplyAsync(second);
        await Assert.ThrowsAsync<SourceConnectionException>(()=>second.ProbeAsync());
    }
    [Fact]
    public async Task SessionCannotCrossAccountOrCertificatePolicyAndDiscardLogsOut()
    {
        var server=new SynologyFakeDsm();var owner=Source(server);await owner.ProbeAsync();await using var ticket=new SynologySessionTicket(owner);
        await using var other=Source(new SynologyFakeDsm{RequireOtp=true},new("different","password"));await ticket.ApplyAsync(other);
        await Assert.ThrowsAsync<SourceConnectionException>(()=>other.ProbeAsync());Assert.False(ticket.IsReleased);
        await ticket.DisposeAsync();Assert.Contains(server.Requests,x=>x["api"]=="SYNO.API.Auth"&&x["method"]=="logout");
        await ticket.DisposeAsync();Assert.Single(server.Requests,x=>x["api"]=="SYNO.API.Auth"&&x["method"]=="logout");
    }
    [Fact]
    public async Task AbandonedProbeExpiresAndSerializedResultContainsNoCredentials()
    {
        var server=new SynologyFakeDsm();var owner=Source(server);await owner.ProbeAsync();
        await using var ticket=new SynologySessionTicket(owner,TimeSpan.FromMilliseconds(20));
        var result=new SynologyProbeResult(true,"ok",[],new("user","password",SessionToken:"device-secret")){Session=ticket};
        var text=JsonSerializer.Serialize(result);Assert.DoesNotContain("password",text);Assert.DoesNotContain("device-secret",text);Assert.DoesNotContain("sid-",text);
        var deadline=DateTimeOffset.UtcNow.AddSeconds(10);while(!ticket.IsReleased&&DateTimeOffset.UtcNow<deadline)await Task.Delay(10);
        Assert.True(ticket.IsReleased);Assert.Contains(server.Requests,x=>x["api"]=="SYNO.API.Auth"&&x["method"]=="logout");
    }
    [Fact]
    public async Task JsonRequestFormatPreservesStringsAndArraysAndVersionLimits()
    {
        var server=new SynologyFakeDsm{JsonFormat=true};await using var source=Source(server);
        await source.GetMetadataAsync("file.bin");
        var start=Assert.Single(server.RawRequests,x=>x["api"]=="SYNO.FileStation.MD5"&&x["method"]=="start");
        Assert.Equal("\"/share/file.bin\"",start["file_path"]);
        var metadata=server.RawRequests.First(x=>x["api"]=="SYNO.FileStation.List"&&x["method"]=="getinfo");Assert.Equal("[\"/share/file.bin\"]",metadata["path"]);
    }
    [Fact(Timeout=15000)]
    public async Task SnapshotBodyRetainsNetworkSlotUntilConsumptionAndOtherMountCanRun()
    {
        await using var queue=new TasksQueueService();var binding=queue.StartMount(Guid.NewGuid());var other=queue.StartMount(Guid.NewGuid());
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server=new SynologyFakeDsm();server.Intercept=(request,fields,token)=>Task.FromResult(fields["api"]=="SYNO.FileStation.Download"?
            new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new HeldStream(Encoding.UTF8.GetBytes("abcdefgh"),started,release))}:null);
        await using var source=Source(server);((IMountNetworkSource)source).BindQueue(binding);var entry=await source.GetMetadataAsync("file.bin");
        Task reading;using(queue.Enter(binding,new("read",binding.MountId,binding.Generation,MountingPriority.Demand,DateTimeOffset.UtcNow,"download","file.bin",Version:entry.Version)))reading=source.ReadRangeAsync(entry,0,new byte[8]).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));var logins=server.Requests.Count;
            Task waiting;using(queue.Enter(binding,new("maintenance",binding.MountId,binding.Generation,MountingPriority.Maintenance,DateTimeOffset.UtcNow,"capacity")))waiting=source.GetRemoteCapacityAsync();
            await Task.Delay(30);Assert.False(waiting.IsCompleted);Assert.Equal(logins,server.Requests.Count);
            using(var independent=await queue.AcquireAsync(other,new("other",other.MountId,other.Generation,MountingPriority.User,DateTimeOffset.UtcNow,"test"),default))Assert.False(waiting.IsCompleted);
            release.TrySetResult();await Task.WhenAll(reading,waiting);
        }
        finally{release.TrySetResult();}
    }
    [Fact(Timeout=15000)]
    public async Task ConcurrentHashWaiterInheritsPriorityAndCancellationKeepsOtherOwner()
    {
        await using var queue=new TasksQueueService(1);var binding=queue.StartMount(Guid.NewGuid());var blocker=queue.StartMount(Guid.NewGuid());
        var server=new SynologyFakeDsm();await using var source=Source(server);((IMountNetworkSource)source).BindQueue(binding);await source.ProbeAsync();
        using var held=await queue.AcquireAsync(blocker,new("held",blocker.MountId,blocker.Generation,MountingPriority.User,DateTimeOffset.UtcNow,"test"),default);
        using var cancel=new CancellationTokenSource();Task first,second;
        using(queue.Enter(binding,new("low",binding.MountId,binding.Generation,MountingPriority.Observation,DateTimeOffset.UtcNow,"scan","file.bin")))first=source.GetMetadataAsync("file.bin",cancel.Token);
        using(queue.Enter(binding,new("high",binding.MountId,binding.Generation,MountingPriority.Demand,DateTimeOffset.UtcNow,"read","file.bin")))second=source.GetMetadataAsync("file.bin");
        var deadline=DateTimeOffset.UtcNow.AddSeconds(5);
        while(!queue.QueueSnapshots.Any(x=>x.Task.Id=="low"&&x.EffectivePriority==MountingPriority.Demand)&&DateTimeOffset.UtcNow<deadline)await Task.Delay(5);
        Assert.Contains(queue.QueueSnapshots,x=>x.Task.Id=="low"&&x.EffectivePriority==MountingPriority.Demand);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
        Assert.Contains(queue.QueueSnapshots,x=>x.Task.Id=="low"&&x.EffectivePriority==MountingPriority.Demand);held.Dispose();await second;
    }
    [Theory]
    [InlineData("")][InlineData("password-123")]
    public void VersionSixKeepsEncryptionAndOldVersionsRemainReadable(string password)
    {
        var item=new MountBackupItem("NAS","old-path",SynologyTests.Options with{PinnedCertificateSha256=new string('A',64)},new("user","password"));
        var bytes=MountBackupCodec.Encode(new(6,DateTimeOffset.UtcNow,[item]),password);var decoded=MountBackupCodec.Decode(bytes,password);
        Assert.Equal(item,Assert.Single(decoded.Mounts));Assert.Equal(6,decoded.SchemaVersion);
        Assert.Throws<InvalidDataException>(()=>MountBackupCodec.Encode(new(5,DateTimeOffset.UtcNow,[item]),password));
        for(var version=1;version<=5;version++)
        {
            var old=item with{Source=new(FileSourceKind.WebDav,"https://example.test/")};
            Assert.Equal(version,MountBackupCodec.Decode(MountBackupCodec.Encode(new(version,DateTimeOffset.UtcNow,[old]),password),password).SchemaVersion);
        }
    }
    [Fact]
    public void DeviceTokenIsNotImportedOrExportedAndNullPinDoesNotChangeOldJson()
    {
        var credentials=new SourceCredentials("user","password",SessionToken:"device-secret");var protocol=new SynologySourceProtocol();
        Assert.Empty(protocol.NormalizeImportCredentials(SynologyTests.Options,credentials).SessionToken);
        var id=Guid.NewGuid();new CredentialStore(_area).Save(id,credentials);
        Assert.Empty(protocol.BackupCredentials(new(id,"NAS",_area,Source:SynologyTests.Options),_area).SessionToken);
        Assert.DoesNotContain("PinnedCertificate",JsonSerializer.Serialize(new FileSourceConfiguration(FileSourceKind.WebDav,"https://example.test/")));
        Assert.Equal(5,(int)FileSourceKind.Synology);
    }
    private sealed class MemoryStore:IMountDefinitionStore
    {
        private readonly Dictionary<Guid,MountDefinition> _values=[];
        public long CacheLimitBytes {get;set;}
        public IReadOnlyList<MountDefinition> LoadDefinitions()=>_values.Values.ToArray();
        public void SaveDefinition(MountDefinition definition)=>_values[definition.Id]=definition;
        public void DeleteDefinition(Guid id)=>_values.Remove(id);
    }
    /// <summary>直到放行才交付正文，验证响应头到达不释放存储执行位。</summary>
    private sealed class HeldStream(byte[] bytes,TaskCompletionSource started,TaskCompletionSource release):MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {started.TrySetResult();await release.Task.WaitAsync(token);return await base.ReadAsync(buffer,token);}
    }
}
