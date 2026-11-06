using System.Text;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>在隔离 NTFS/CFAPI 根和本地双协议服务验证删除后同名复制及历史队列收尾。</summary>
public sealed class LocalReplacementTests
{
    [Theory(Timeout=90000)]
    [InlineData(FileSourceKind.WebDav)][InlineData(FileSourceKind.S3)]
    public async Task DeletedPlaceholderReplacementKeepsOriginalBaselineAndMapping(FileSourceKind kind)
    {
        await using var test=await Fixture.Create(kind);const string name="文件.bin";var path=Path.Combine(test.Local,name);
        var original=test.Repository.Entries(test.Id).Single(x=>x.LocalPath==name);
        File.Delete(path);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        Assert.True(test.Repository.Entries(test.Id).Single(x=>x.Id==original.Id).Deleted);
        var bytes=Encoding.UTF8.GetBytes("replacement from a different file");await File.WriteAllBytesAsync(path,bytes);
        await test.Manager.ScanUploadChangesAsync(test.Id);var queued=test.Record(name);
        Assert.Equal(original.Version,queued.ExpectedVersion);Assert.Equal(original.Id,queued.EntryId);
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Wait(name,"Completed");
        Assert.Equal(bytes,test.Remote(name));Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);
        var mapped=Assert.Single(test.Repository.Entries(test.Id),x=>x.SourcePath==name);
        Assert.Equal(original.Id,mapped.Id);Assert.False(mapped.Deleted);Assert.False(mapped.Protected);
    }

    [Fact]
    public async Task RemovedMountWaitingHistoryIsRetiredWithoutCompletingWork()
    {
        await using var dav=new DavFixture();var registry=@"Software\HybridMount.NET.Tests\RemovedQueue-"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(dav.Area,"data"),registry);var removed=Guid.NewGuid();
        repository.SaveOperation(new("old-wait","remoteUpdate","Waiting",0,1,[new("EXX11901.shx","Waiting","waiting for upload")],"waiting for upload",removed,"EXX11901.shx"));
        await using var logger=new LoggerService(Path.Combine(dav.Area,"queue-log.db"));await using var manager=new MountManager(repository,logger);
        var history=Assert.Single(manager.Operations);Assert.Equal("Canceled",history.State);Assert.Equal(0,history.Completed);Assert.Contains("移除",history.Error);
        Assert.Equal(1,await manager.ClearOperationHistoryAsync(TaskHistoryClearMode.All));Assert.Empty(manager.Operations);
        Assert.Empty(dav.Server.Requests);
    }

    /// <summary>远端已变化时保留原基线；确认远端删除后绝不把本地同名复制重新创建到远端。</summary>
    [Theory(Timeout=90000)]
    [InlineData(FileSourceKind.WebDav,false)][InlineData(FileSourceKind.S3,false)]
    [InlineData(FileSourceKind.WebDav,true)][InlineData(FileSourceKind.S3,true)]
    public async Task ReplacementNeverAdoptsConcurrentRemoteChangeOrRecreatesDeletion(FileSourceKind kind,bool deleted)
    {
        await using var test=await Fixture.Create(kind);const string name="文件.bin";var path=Path.Combine(test.Local,name);
        var original=test.Repository.Entries(test.Id).Single(x=>x.LocalPath==name);
        File.Delete(path);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        if(deleted){test.DeleteRemote(name);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));}
        else test.PutRemote(name,"another client"u8.ToArray());
        await File.WriteAllTextAsync(path,"keep this replacement");await test.Manager.ScanUploadChangesAsync(test.Id);
        var item=test.Record(name);if(!deleted)Assert.Equal(original.Version,item.ExpectedVersion);else Assert.Equal(UploadRules.RemoteDeletedMessage,item.Error);
        var writes=test.TargetWrites(name);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Wait(name,"Conflict");
        if(deleted)Assert.Equal(writes,test.TargetWrites(name));
        else foreach(var request in test.TargetRequests(name).Skip(writes))
        {Assert.Equal("PUT",request.Method);Assert.True(request.Headers.ContainsKey("If-Match"));Assert.False(request.Headers.ContainsKey("If-None-Match"));}
        Assert.Equal("keep this replacement",await File.ReadAllTextAsync(path));
        if(deleted)Assert.False(test.RemoteExists(name));else Assert.Equal("another client",Encoding.UTF8.GetString(test.Remote(name)));
        Assert.True(test.Repository.Entries(test.Id).Single(x=>x.Id==original.Id).Protected);
        Assert.NotEqual(1u,CloudFilesNative.Inspect(path)?.Info.InSyncState??0);
    }

    /// <summary>旧版误建的新增任务只修复版本绑定；取消／冲突不自动解除，显式重试才执行条件更新。</summary>
    [Theory(Timeout=90000)]
    [InlineData("Conflict")][InlineData("Failed")][InlineData("Canceled")]
    public async Task LegacyMisclassifiedIntentIsRepairedButRequiresExplicitRetry(string state)
    {
        await using var test=await Fixture.Create(FileSourceKind.WebDav);const string name="文件.bin";var path=Path.Combine(test.Local,name);
        var original=test.Repository.Entries(test.Id).Single(x=>x.LocalPath==name);
        File.Delete(path);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        await File.WriteAllTextAsync(path,"legacy replacement");await test.Manager.ScanUploadChangesAsync(test.Id);var queued=test.Record(name);
        test.Repository.UpdateUpload(queued.Id,x=>x with{EntryId=null,ExpectedVersion=null,BaselineSha256=null,State=state,CancelRequested=state=="Canceled",Error="目标已有历史远端基线"});
        await test.Manager.ScanUploadChangesAsync(test.Id);var repaired=test.Record(name);
        Assert.Equal(original.Version,repaired.ExpectedVersion);Assert.Equal(original.Id,repaired.EntryId);Assert.Equal(state,repaired.State);Assert.Equal(queued.Generation,repaired.Generation);
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Task.Delay(2300);Assert.Equal(0,test.TargetWrites(name));
        Assert.True(test.Manager.RetryUpload(queued.Id));await test.Wait(name,"Completed");
        Assert.Equal("legacy replacement",Encoding.UTF8.GetString(test.Remote(name)));Assert.Equal(1,test.TargetWrites(name));
    }

    [Theory(Timeout=90000)]
    [InlineData(FileSourceKind.WebDav)][InlineData(FileSourceKind.S3)]
    public async Task RestartAfterObservedDeletionKeepsOriginalBaseline(FileSourceKind kind)
    {
        await using var test=await Fixture.Create(kind);const string name="文件.bin";var path=Path.Combine(test.Local,name);
        var original=test.Repository.Entries(test.Id).Single(x=>x.LocalPath==name);
        File.Delete(path);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));await test.Manager.DisposeAsync();
        await File.WriteAllTextAsync(path,"replacement while stopped");await test.Restart();
        Assert.Equal(original.Version,test.Record(name).ExpectedVersion);await test.Wait(name,"Completed");
        Assert.Equal("replacement while stopped",Encoding.UTF8.GetString(test.Remote(name)));
        Assert.Equal(original.Id,Assert.Single(test.Repository.Entries(test.Id),x=>x.SourcePath==name).Id);
    }

    /// <summary>远端回执到达后再次同名替换，必须先提交旧回执，再以新版本发送下一代。</summary>
    [Theory(Timeout=90000)]
    [InlineData(FileSourceKind.WebDav)][InlineData(FileSourceKind.S3)]
    public async Task ReplacementAfterRemoteCommitQueuesNextGeneration(FileSourceKind kind)
    {
        await using var test=await Fixture.Create(kind);const string name="文件.bin";var path=Path.Combine(test.Local,name);
        var original=test.Repository.Entries(test.Id).Single(x=>x.LocalPath==name);
        File.Delete(path);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        await File.WriteAllTextAsync(path,"first replacement");await test.Manager.ScanUploadChangesAsync(test.Id);var generation=test.Record(name).Generation;var changed=false;
        test.Manager.UploadCheckpointForTests=async(stage,item)=>
        {
            if(stage!="RemoteCommitted"||item.LocalPath!=name||changed)return;changed=true;
            File.Delete(path);await File.WriteAllTextAsync(path,"second replacement");await test.Manager.ScanUploadChangesAsync(test.Id);
        };
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var completed=await test.Wait(name,"Completed");
        Assert.True(changed);Assert.True(completed.Generation>generation);Assert.Equal("second replacement",Encoding.UTF8.GetString(test.Remote(name)));
        Assert.Equal(2,test.TargetWrites(name));Assert.Equal(original.Id,Assert.Single(test.Repository.Entries(test.Id),x=>x.SourcePath==name).Id);
    }

    [Theory(Timeout=90000)]
    [InlineData(false)][InlineData(true)]
    public async Task DeletedMappingDoesNotAllowFileDirectoryTypeSwap(bool directory)
    {
        await using var test=await Fixture.Create(FileSourceKind.WebDav);var name=directory?"空目录":"文件.bin";var path=Path.Combine(test.Local,name);
        if(directory)Directory.Delete(path);else File.Delete(path);
        await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        if(directory)await File.WriteAllTextAsync(path,"directory replaced by file");else Directory.CreateDirectory(path);
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Equal("Conflict",test.Record(name).State);Assert.Equal(UploadRules.TypeConflictMessage,test.Record(name).Error);
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Task.Delay(2300);Assert.Equal(0,test.TargetWrites(name));
        Assert.NotEqual(1u,CloudFilesNative.Inspect(path)?.Info.InSyncState??0);
    }

    /// <summary>已发送且结果不明的旧任务不能由扫描重新绑定版本；必须保持快照并先只读核对。</summary>
    [Fact(Timeout=90000)]
    public async Task UnknownResultDoesNotAdoptReplacementBaselineDuringScan()
    {
        await using var test=await Fixture.Create(FileSourceKind.WebDav);const string name="文件.bin";var path=Path.Combine(test.Local,name);
        File.Delete(path);await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        await File.WriteAllTextAsync(path,"replacement");await test.Manager.ScanUploadChangesAsync(test.Id);
        var entered=new TaskCompletionSource<LocalUpload>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.UploadCheckpointForTests=async(stage,item)=>{if(stage=="Prepared"&&item.LocalPath==name){entered.TrySetResult(item);await release.Task;}};
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var prepared=await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            test.Repository.UpdateUpload(prepared.Id,x=>x with{State="OutcomeUnknown",ExpectedVersion=null,EntryId=null,BaselineSha256=null});
            await test.Manager.ScanUploadChangesAsync(test.Id);var unknown=test.Record(name);
            Assert.Equal("OutcomeUnknown",unknown.State);Assert.Null(unknown.ExpectedVersion);Assert.Null(unknown.EntryId);Assert.Equal(prepared.SnapshotPath,unknown.SnapshotPath);Assert.True(File.Exists(unknown.SnapshotPath));
        }
        finally{test.Repository.UpdateUpload(prepared.Id,x=>x with{State="Canceled",CancelRequested=true});release.TrySetResult();await test.Manager.DisconnectAsync(test.Id);}
        Assert.Equal(0,test.TargetWrites(name));
    }

    [Fact]
    public void ReplacementBaselineRejectsAmbiguousUnmaterializedRenamedAndRemotelyDeletedMappings()
    {
        var entry=new MountEntry("original","file","file","old-version",false,Deleted:true,Materialized:true);
        Assert.Equal(entry,UploadRules.LocalReplacementBaseline([entry],"file"));
        Assert.Null(UploadRules.LocalReplacementBaseline([entry,entry with{Id="other",Version="another-version"}],"file"));
        Assert.Equal(entry,UploadRules.LocalReplacementBaseline([entry,entry with{Id="other"}],"file",entry.Id));
        foreach(var invalid in new[]{entry with{Materialized=false},entry with{SourcePath="other"},entry with{LocalPath="other"},entry with{RemotePresence=RemotePresence.Deleted}})
            Assert.Null(UploadRules.LocalReplacementBaseline([invalid],"file"));
        Assert.Null(UploadRules.LocalReplacementBaseline([entry],"File"));
    }

    /// <summary>缺少定义但仍有恢复证据不能结束；当前定义下的断开任务及上传未知结果也保持原状。</summary>
    [Fact]
    public async Task StartupRecoveryPreservesDisconnectedAndRecoverableWork()
    {
        await using var dav=new DavFixture();var registry=@"Software\HybridMount.NET.Tests\RecoveryQueue-"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(Path.Combine(dav.Area,"recovery-data"),registry);var active=Guid.NewGuid();var removed=Guid.NewGuid();
        var local=Path.Combine(dav.Area,"not-registered");Directory.CreateDirectory(local);
        repository.SaveDefinition(new(active,"disconnected",local,false,RootIdentity:MountPathGuard.FileIdentity(local),Source:dav.Options,RegistrationStage:MountRegistrationStage.NotRegistered));
        repository.SaveOperation(new("disconnected","remoteUpdate","Waiting",0,1,[],"busy",active,"file"));
        var pending=new RemotePendingChange("recovery",removed,"scope","file",null,"Hydrating",SnapshotPath:"must-preserve.snapshot");repository.SaveRemotePending(pending);
        repository.SaveOperation(new(pending.Id,"remoteUpdate","Waiting",0,1,[],"busy",removed,"file"));
        repository.SaveUpload(new("unknown",removed,"file","file",false,"stamp",1,"OutcomeUnknown",DateTimeOffset.UtcNow,SnapshotPath:"must-preserve.snapshot"));var upload=repository.Upload("unknown");
        await using var logger=new LoggerService(Path.Combine(dav.Area,"recovery-log.db"));await using(var manager=new MountManager(repository,logger))
        {
            Assert.Contains(manager.Operations,x=>x.Id=="disconnected"&&x.State=="Waiting");Assert.Contains(manager.Operations,x=>x.Id==pending.Id&&x.State=="Waiting");
            Assert.Equal(pending,Assert.Single(repository.AllRemotePending(removed)));Assert.Equal(upload,repository.Upload("unknown"));Assert.Empty(dav.Server.Requests);
        }
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);
    }

    [Fact(Timeout=90000)]
    public async Task SuccessfulRemovalRetiresRemoteHistoryInTheSameCleanup()
    {
        await using var test=await Fixture.Create(FileSourceKind.WebDav);var other=Guid.NewGuid();
        var pending=new RemotePendingChange("remove-pending",test.Id,"scope","文件.bin",null,"Waiting");test.Repository.SaveRemotePending(pending);
        test.Repository.SaveOperation(new(pending.Id,"remoteUpdate","Waiting",0,1,[],"busy",test.Id,pending.Path));
        test.Repository.SaveOperation(new("other-mount","remoteUpdate","Waiting",0,1,[],"other work",other,"file"));
        var result=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"clearAndRemove"));Assert.Equal("Completed",result.State);
        Assert.Contains(test.Manager.Operations,x=>x.Id==pending.Id&&x.State=="Canceled"&&x.Completed==0);
        Assert.Contains(test.Manager.Operations,x=>x.Id=="other-mount"&&x.State=="Waiting");Assert.Empty(test.Repository.AllRemotePending(test.Id));
    }

    /// <summary>测试独占定义、根及服务；销毁时通过公开清空移除流程注销，不接触正式目录。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DavFixture _dav=new();private readonly S3Fixture _s3=new();private readonly FileSourceKind _kind;
        private readonly string _registry=@"Software\HybridMount.NET.Tests\Replacement-"+Guid.NewGuid().ToString("N");
        public MountRepository Repository=null!;public MountManager Manager=null!;private LoggerService _logger=null!;
        public string Local="";public Guid Id;public HttpTestServer Server=>_kind==FileSourceKind.WebDav?_dav.Server:_s3.Server;
        private Fixture(FileSourceKind kind)=>_kind=kind;
        public static async Task<Fixture> Create(FileSourceKind kind)
        {
            var test=new Fixture(kind);var area=kind==FileSourceKind.WebDav?test._dav.Area:test._s3.Area;
            test.Local=Path.Combine(area,"replacement-view");Directory.CreateDirectory(test.Local);
            test.Repository=new(Path.Combine(area,"replacement-data"),test._registry);test._logger=new(Path.Combine(area,"replacement-log.db"));test.Manager=new(test.Repository,test._logger);
            var mount=await test.Manager.AddAsync("同名替换回归",test.Local,false,source:kind==FileSourceKind.WebDav?test._dav.Options:test._s3.Options,credentials:kind==FileSourceKind.WebDav?new("user","app-password"):S3Fixture.Credentials);
            test.Id=mount.Id;await test.Manager.SetSchedulingForTestsAsync(test.Id,false);return test;
        }
        public void PutRemote(string path,byte[] bytes){if(_kind==FileSourceKind.WebDav)_dav.Put(path,bytes);else _s3.Put("remote/"+path,bytes);}
        public void DeleteRemote(string path){if(_kind==FileSourceKind.WebDav)_dav.Objects.TryRemove(path,out _);else _s3.Objects.TryRemove("remote/"+path,out _);}
        public bool RemoteExists(string path)=>_kind==FileSourceKind.WebDav?_dav.Objects.ContainsKey(path):_s3.Objects.ContainsKey("remote/"+path);
        public IReadOnlyList<HttpTestServer.Request> TargetRequests(string path)=>Server.Requests.Where(x=>x.Method is "PUT" or "MKCOL"&&Uri.UnescapeDataString(new Uri(Server.Address,x.Target).AbsolutePath).TrimEnd('/').EndsWith("/"+path,StringComparison.Ordinal)).ToArray();
        public int TargetWrites(string path)=>TargetRequests(path).Count;
        public async Task Restart(){Manager=new(Repository,_logger);await Manager.ConnectAsync(Id);}
        public LocalUpload Record(string path)=>Repository.Uploads(Id).Single(x=>!x.IsCopy&&x.LocalPath==path);
        public byte[] Remote(string path)=>_kind==FileSourceKind.WebDav?_dav.Objects[path].Bytes:_s3.Objects["remote/"+path].Bytes;
        public async Task<LocalUpload> Wait(string path,string state)
        {
            var deadline=DateTime.UtcNow.AddSeconds(30);
            while(DateTime.UtcNow<deadline)
            {
                var item=Repository.Uploads(Id).FirstOrDefault(x=>!x.IsCopy&&x.LocalPath==path);
                if(item?.State==state)return item;
                if(item?.State is "Conflict" or "Failed")throw new IOException(item.State+": "+item.Error);
                await Task.Delay(50);
            }
            throw new TimeoutException(System.Text.Json.JsonSerializer.Serialize(Repository.Uploads(Id)));
        }
        public async ValueTask DisposeAsync()
        {
            try{if(Manager is not null){if(Manager.Mounts.Any(x=>x.Definition.Id==Id))await WebDavTests.Finished(Manager,Manager.Queue(Id,"clearAndRemove"));await Manager.DisposeAsync();}}
            finally{if(_logger is not null)await _logger.DisposeAsync();Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_registry,false);await _dav.DisposeAsync();await _s3.DisposeAsync();}
        }
    }
}
