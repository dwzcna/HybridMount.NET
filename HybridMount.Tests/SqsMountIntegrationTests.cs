using System.Buffers.Binary;
using System.Security.Cryptography;
using HybridMount.Commons;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>内存队列配合真实 S3 HTTP SDK/NTFS/CFAPI；覆盖挂载所有权，非真实 AWS SQS 认证。</summary>
public sealed class SqsMountIntegrationTests
{
    private static async Task Until(Func<bool> check,int milliseconds=10000)
    {var end=DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);while(!check()){if(DateTimeOffset.UtcNow>end)throw new TimeoutException("未达到预期队列状态。");await Task.Delay(20);}}
    [Fact(Timeout=30000)]
    public async Task EventUpdatesPinnedFileWithoutWaitingForPeriodicListAndDisconnectStopsLongPoll()
    {
        await using var host=await Host.Create();var path=Path.Combine(host.Local,"文件.bin");
        Assert.Equal("Completed",(await WebDavTests.Finished(host.Manager,host.Manager.Queue(host.Id,"keepOnDevice",[path]))).State);
        var queue=new SqsEventTests.FakeQueue();host.Manager.SqsClientForTests=(_,_,_)=>queue;
        await host.Manager.SetSqsConfigurationAsync(host.Id,SqsEventTests.Queue);await Until(()=>queue.Receives>0);
        var before=host.Fixture.Server.Requests.Count;var bytes=Enumerable.Repeat((byte)91,34099).ToArray();host.Fixture.Put("remote/文件.bin",bytes);
        queue.Add(new("updated",SqsEventTests.Body(sequence:"F"),"receipt"));await queue.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 完成行先落库，检测汇总在本轮应用结束后发布；等待两者，不把中间快照当成最终结果。
        await Until(()=>host.Repository.AllRemotePending(host.Id).Any(x=>x.Path=="文件.bin"&&x.Entry?.Size==bytes.Length&&x.State=="Completed")&&
            host.Manager.Mounts.Single().Detection is {LastEventUtc:not null,PendingHints:0});
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path));Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);
        Assert.DoesNotContain(host.Fixture.Server.Requests.Skip(before),x=>x.Target.Contains("list-type=")||x.Method is "PUT" or "POST");
        var status=host.Manager.Mounts.Single().Detection!;Assert.NotNull(status.LastEventUtc);Assert.Equal(0,status.PendingHints);
        var start=DateTimeOffset.UtcNow;await host.Manager.DisconnectAsync(host.Id);Assert.True(DateTimeOffset.UtcNow-start<TimeSpan.FromSeconds(5));Assert.Equal(1,queue.Disposals);
        Assert.Equal("未连接",host.Manager.Mounts.Single().Status);
    }
    [Fact(Timeout=30000)]
    public async Task ReadOnlyQueueTestDoesNotReceiveDeleteRegisterOrSaveConfiguration()
    {
        await using var host=await Host.Create();await host.Manager.DisconnectAsync(host.Id);var queue=new SqsEventTests.FakeQueue();host.Manager.SqsClientForTests=(_,_,_)=>queue;
        var before=host.Repository.LoadDefinitions().Single();var requests=host.Fixture.Server.Requests.Count;
        var result=await host.Manager.TestSqsConnectionAsync(host.Id,SqsEventTests.Queue);Assert.True(result.Success,result.Message);
        Assert.Equal(1,queue.Attributes);Assert.Equal(0,queue.Receives);Assert.Equal(0,queue.Acks);Assert.Equal(1,queue.Disposals);
        Assert.Equal(before,host.Repository.LoadDefinitions().Single());Assert.Equal(requests,host.Fixture.Server.Requests.Count);Assert.DoesNotContain(host.Repository.AllRemotePending(host.Id),x=>x.State!="Completed");
    }
    [Fact(Timeout=30000)]
    public async Task SqsFailureKeepsS3MountedAndManualFullReconciliationAvailable()
    {
        await using var host=await Host.Create();var queue=new DeniedQueue();host.Manager.SqsClientForTests=(_,_,_)=>queue;
        await host.Manager.SetSqsConfigurationAsync(host.Id,SqsEventTests.Queue);await Until(()=>host.Manager.Mounts.Single().Detection?.QueueError is not null);
        Assert.Equal("已连接",host.Manager.Mounts.Single().Status);host.Fixture.Put("remote/new.txt",[8]);
        var refreshed=await WebDavTests.Finished(host.Manager,host.Manager.Queue(host.Id,"refresh"));Assert.True(refreshed.State=="Completed",refreshed.Error);
        Assert.Equal(new byte[]{8},await File.ReadAllBytesAsync(Path.Combine(host.Local,"new.txt")));Assert.NotNull(host.Manager.Mounts.Single().Detection!.QueueError);
    }
    [Fact(Timeout=30000)]
    public async Task ChangingCredentialScopeRetiresUnsentOldHintsButPreservesNativeRecovery()
    {
        await using var host=await Host.Create();var repository=host.Repository;var metadata=new SourceEntry("","file",false,1,DateTimeOffset.UnixEpoch,"v");
        repository.PersistSqsMessage("queue","old",[new(Guid.NewGuid().ToString("N"),host.Id,"old","file","bucket","1","ObjectCreated:Put")]);Assert.True(repository.HasPendingRemote(host.Id,"file"));
        var recover=new RemotePendingChange(Guid.NewGuid().ToString("N"),host.Id,"old","recovery",metadata with{Path="recovery"},"Hydrating");repository.SaveRemotePending(recover);
        repository.ActivateRemoteScope(host.Id,"new");Assert.False(repository.HasPendingRemote(host.Id,"file"));Assert.True(repository.HasPendingRemote(host.Id,"recovery"));
        Assert.Equal("Hydrating",repository.RemotePending(host.Id,"old").Single(x=>x.Id==recover.Id).State);
        repository.SaveRemotePending(recover with{State="Completed"});
    }
    [Fact(Timeout=30000)]
    public async Task BackupV3ImportsSeparateQueueSecretsWithoutConnectingAndLegacyFormatsExcludeThem()
    {
        await using var fixture=new S3Fixture();var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");var repository=new MountRepository(Path.Combine(fixture.Area,"backup"),registry);
        await using var logger=new LoggerService(Path.Combine(fixture.Area,"logs.db"));await using var manager=new MountManager(repository,logger,new NeverSource());
        var queueCreates=0;manager.SqsClientForTests=(_,_,_)=>{queueCreates++;throw new InvalidOperationException("导入不应创建队列客户端");};
        var keys=new SourceCredentials(AccessKey:"queue-access",SecretKey:"queue-secret",SessionToken:"queue-session");var item=new MountBackupItem("队列备份","old",SqsEventTests.Aws,S3Fixture.Credentials,
            Sqs:SqsEventTests.Queue with{CredentialMode=SqsCredentialMode.SeparateKeys},SqsCredentials:keys);
        var bytes=MountBackupCodec.Encode(new(3,DateTimeOffset.UtcNow,[item]),"long-password");Assert.Equal(3,BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8,4)));
        Assert.Equal(item,MountBackupCodec.Decode(bytes,"long-password").Mounts.Single());Assert.DoesNotContain("queue-secret",System.Text.Encoding.UTF8.GetString(bytes));
        foreach(var version in new[]{1,2})Assert.Null(MountBackupCodec.Decode(MountBackupCodec.Encode(new(version,DateTimeOffset.UtcNow,[item]),"long-password"),"long-password").Mounts.Single().Sqs);
        var local=Path.Combine(fixture.Area,"import-view");Directory.CreateDirectory(local);var result=await manager.ImportConfigurationsAsync([new(item,local)]);
        var saved=Assert.Single(result.Results);Assert.Equal("Saved",saved.State);var definition=Assert.Single(repository.LoadDefinitions());Assert.Equal(item.Sqs,definition.Sqs);Assert.Equal(keys,manager.GetSqsCredentials(definition.Id));
        Assert.Equal(0,queueCreates);Assert.Null(MountPathGuard.ReadRoot(local));Assert.Empty(repository.Entries(definition.Id));Assert.Empty(repository.Uploads(definition.Id));
        var file=Path.Combine(fixture.Area,"backup.hmconfig");await manager.ExportConfigurationsAsync([definition.Id],"long-password",file);var document=await manager.ReadConfigurationBackupAsync(file,"long-password");Assert.Equal(5,document.SchemaVersion);Assert.Empty(document.Mounts.Single().ExclusionRules??[]);Assert.Equal(keys,document.Mounts.Single().SqsCredentials);
        var registryJson=System.Text.Json.JsonSerializer.Serialize(definition);Assert.DoesNotContain("queue-secret",registryJson);Assert.DoesNotContain("queue-access",registryJson);
        Assert.Equal("Completed",(await WebDavTests.Finished(manager,manager.Queue(definition.Id,"clearAndRemove"))).State);Assert.Equal(new SourceCredentials(),new CredentialStore(Path.Combine(repository.DataDirectory,"Sqs")).Load(definition.Id));
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
    }
    private sealed class NeverSource : IFileSourceFactory
    {public Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)=>throw new InvalidOperationException("导入不应连接远端");}
    private sealed class DeniedQueue : ISqsQueueClient
    {
        public Task<IReadOnlyDictionary<string,string>> AttributesAsync(CancellationToken token)=>Task.FromResult<IReadOnlyDictionary<string,string>>(new Dictionary<string,string>{{"QueueArn","arn:aws:sqs:us-east-1:123456789012:hybrid-test"}});
        public Task<IReadOnlyList<SqsMessage>> ReceiveAsync(CancellationToken token)=>throw new SqsOperationException(403,"AccessDenied");
        public Task AcknowledgeAsync(SqsMessage message,CancellationToken token)=>throw new InvalidOperationException();
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
    private sealed class LocalS3Factory(S3Fixture fixture):IFileSourceFactory
    {public Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)=>Task.FromResult<IFileSource>(fixture.Source());}
    private sealed class Host : IAsyncDisposable
    {
        public readonly S3Fixture Fixture=new();public MountRepository Repository=null!;public LoggerService Logger=null!;public MountManager Manager=null!;public Guid Id;public string Local="",Registry="";
        public static async Task<Host> Create()
        {
            var host=new Host();host.Registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");host.Repository=new(Path.Combine(host.Fixture.Area,"data"),host.Registry);host.Logger=new(Path.Combine(host.Fixture.Area,"logs.db"));host.Manager=new(host.Repository,host.Logger,new LocalS3Factory(host.Fixture));
            host.Local=Path.Combine(host.Fixture.Area,"view");Directory.CreateDirectory(host.Local);var mount=await host.Manager.AddAsync("SQS 集成回归",host.Local,false,source:SqsEventTests.Aws,credentials:S3Fixture.Credentials);host.Id=mount.Id;return host;
        }
        public async ValueTask DisposeAsync()
        {
            if(Manager.Mounts.Count>0){var result=await WebDavTests.Finished(Manager,Manager.Queue(Id,"clearAndRemove"));Assert.True(result.State=="Completed",result.Error);}await Manager.DisposeAsync();await Logger.DisposeAsync();Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Registry,false);await Fixture.DisposeAsync();
        }
    }
}
