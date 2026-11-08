using System.Text;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>远端删除与本地占位、固定缓存及上传屏障的真实 CFAPI 回归。</summary>
[Collection("Process exception diagnostics")]
public sealed class RemoteDeletionTests
{
    private static Task<MountOperationSnapshot> Refresh(UploadFlowTests.Fixture test)=>WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
    private static int Mutations(DavFixture dav)=>dav.Server.Requests.Count(request=>request.Method is "PUT" or "MKCOL" or "MOVE");
    /// <summary>未下载、已缓存和已固定文件均可按远端删除清理，刷新及重连不下载或回传它们。</summary>
    [Fact(Timeout=120000)]
    public async Task CleanOnlineCachedAndPinnedFilesDisappearWithoutHydrationOrReupload()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Dav.Put("cached.bin",[1,2,3]);test.Dav.Put("pinned.bin",[4,5,6]);await Refresh(test);
        await Task.Run(()=>File.ReadAllBytes(Path.Combine(test.Local,"cached.bin")));
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[Path.Combine(test.Local,"pinned.bin")]))).State);
        foreach(var name in new[]{"文件.bin","cached.bin","pinned.bin"})Assert.True(test.Dav.Objects.TryRemove(name,out _));
        var gets=test.Dav.Server.Requests.Count(request=>request.Method=="GET");var writes=Mutations(test.Dav);var result=await Refresh(test);
        Assert.Equal("Completed",result.State);
        foreach(var name in new[]{"文件.bin","cached.bin","pinned.bin"})
        {
            Assert.False(File.Exists(Path.Combine(test.Local,name)),System.Text.Json.JsonSerializer.Serialize(result));
            var entry=test.Repository.Entries(test.Id).Single(item=>item.SourcePath==name);Assert.Equal(RemotePresence.Deleted,entry.RemotePresence);Assert.False(entry.Deleted);Assert.False(entry.Materialized);
        }
        Assert.Equal(gets,test.Dav.Server.Requests.Count(request=>request.Method=="GET"));
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Manager.ScanUploadChangesAsync(test.Id);await Task.Delay(2400);
        await test.Manager.DisconnectAsync(test.Id);await test.Manager.ConnectAsync(test.Id);await Task.Delay(2400);Assert.Equal(writes,Mutations(test.Dav));
    }
    /// <summary>远端目录删除时清理干净子项，保留修改及新文件，且不会自动重建父目录。</summary>
    [Fact(Timeout=120000)]
    public async Task DeletedDirectoryKeepsDirtyAndNewContentWithoutRecreatingRemoteParents()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Dav.Objects["folder"]=new([],"\"folder\"",true);test.Dav.Put("folder/clean.bin",[1,2]);test.Dav.Put("folder/dirty.txt",[3,4]);await Refresh(test);
        var directory=Path.Combine(test.Local,"folder");var dirty=Path.Combine(directory,"dirty.txt");await Task.Run(()=>File.ReadAllBytes(dirty));await File.WriteAllTextAsync(dirty,"local protected");await File.WriteAllTextAsync(Path.Combine(directory,"new.txt"),"new protected");
        foreach(var path in test.Dav.Objects.Keys.Where(path=>path=="folder"||path.StartsWith("folder/",StringComparison.Ordinal)).ToArray())test.Dav.Objects.TryRemove(path,out _);
        var writes=Mutations(test.Dav);var result=await Refresh(test);Assert.Equal("PartialFailure",result.State);
        Assert.False(File.Exists(Path.Combine(directory,"clean.bin")));Assert.Equal("local protected",await File.ReadAllTextAsync(dirty));Assert.True(File.Exists(Path.Combine(directory,"new.txt")));
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Manager.ScanUploadChangesAsync(test.Id);await Task.Delay(2600);
        Assert.Equal(writes,Mutations(test.Dav));Assert.DoesNotContain(test.Dav.Objects.Keys,path=>path=="folder"||path.StartsWith("folder/",StringComparison.Ordinal));
        Assert.Contains(test.Repository.Uploads(test.Id),item=>item.LocalPath=="folder/new.txt"&&item.State=="Conflict");
        test.Manager.CacheLimitBytes=1;await test.Manager.EnforceCacheLimitAsync();Assert.Equal("local protected",await File.ReadAllTextAsync(dirty));
    }
    /// <summary>已经自动上传的文件远端删除后不复活；同路径新本地内容继续要求显式冲突处理。</summary>
    [Fact(Timeout=120000)]
    public async Task UploadedThenRemotelyDeletedDoesNotResurrectOnMetadataEventsOrLocalReplacement()
    {
        await using var test=await UploadFlowTests.Fixture.Create();var path=Path.Combine(test.Local,"uploaded.txt");await File.WriteAllTextAsync(path,"uploaded once");await test.Wait("uploaded.txt");
        test.Dav.Objects.TryRemove("uploaded.txt",out _);var writes=Mutations(test.Dav);Assert.Equal("Completed",(await Refresh(test)).State);Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path,"new local replacement");await test.Manager.ScanUploadChangesAsync(test.Id);await test.Wait("uploaded.txt",state:"Conflict");await Task.Delay(2400);
        Assert.Equal(writes,Mutations(test.Dav));Assert.False(test.Dav.Objects.ContainsKey("uploaded.txt"));Assert.Equal("new local replacement",await File.ReadAllTextAsync(path));
        var current=test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="uploaded.txt");
        Assert.Contains(test.Manager.Operations,item=>item.Id!=current.Id&&item.Operation=="upload"&&item.State=="Completed"&&item.ItemPath=="uploaded.txt");
    }
    /// <summary>远端同名项重新出现时可恢复新占位，不被本地删除标志永久隐藏。</summary>
    [Fact(Timeout=120000)]
    public async Task RemoteReappearanceRestoresFreshPlaceholderAndVersion()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");test.Dav.Objects.TryRemove("文件.bin",out _);await Refresh(test);Assert.False(File.Exists(path));
        test.Dav.Put("文件.bin",Encoding.UTF8.GetBytes("remote returned"));Assert.Equal("Completed",(await Refresh(test)).State);
        Assert.Equal("remote returned",Encoding.UTF8.GetString(await Task.Run(()=>File.ReadAllBytes(path))));
        var entry=test.Repository.Entries(test.Id).Single(item=>item.SourcePath=="文件.bin");Assert.Equal(RemotePresence.Present,entry.RemotePresence);Assert.False(entry.Deleted);Assert.False(entry.Protected);
    }
    /// <summary>不完整枚举和确认缺失时的权限错误均不能触发本地删除。</summary>
    [Fact(Timeout=120000)]
    public async Task EnumerationOrConfirmationFailureNeverDeletesLocalContent()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Dav.Objects.TryRemove("文件.bin",out _);var handler=test.Dav.Server.Handler;
        test.Dav.Server.Handler=request=>request.Method=="PROPFIND"?Task.FromResult(new HttpTestServer.Response(503,[])):handler(request);
        Assert.Equal("Failed",(await Refresh(test)).State);Assert.True(File.Exists(Path.Combine(test.Local,"文件.bin")));
        test.Dav.Server.Handler=request=>request.Method=="PROPFIND"&&request.Headers.GetValueOrDefault("Depth")=="0"&&Uri.UnescapeDataString(request.Target).Contains("文件.bin",StringComparison.Ordinal)?Task.FromResult(new HttpTestServer.Response(403,[])):handler(request);
        Assert.Equal("Failed",(await Refresh(test)).State);Assert.True(File.Exists(Path.Combine(test.Local,"文件.bin")));Assert.Equal(RemotePresence.Present,test.Repository.Entries(test.Id).Single(item=>item.SourcePath=="文件.bin").RemotePresence);
        test.Dav.Server.Handler=handler;var final=await Refresh(test);
        Assert.True(final.State=="Completed",System.Text.Json.JsonSerializer.Serialize(new{Refresh=final,Entries=test.Repository.Entries(test.Id),Uploads=test.Repository.Uploads(test.Id)}));Assert.False(File.Exists(Path.Combine(test.Local,"文件.bin")));
    }
    /// <summary>共享占用失败保留文件，句柄关闭后刷新可重试，不主动下载正文。</summary>
    [Fact(Timeout=120000)]
    public async Task OccupiedCleanFileIsRetainedUntilNextExplicitRefresh()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");test.Dav.Objects.TryRemove("文件.bin",out _);
        using(var held=CloudFilesNative.CreateFile(path,0x80,0,0,3,0x02200000,0))
        {
            Assert.False(held.IsInvalid);var result=await Refresh(test);Assert.Equal("PartialFailure",result.State);Assert.True(File.Exists(path));Assert.Contains(result.Results,item=>item.Status=="Conflict");
        }
        Assert.Equal("Completed",(await Refresh(test)).State);Assert.False(File.Exists(path));
    }
    /// <summary>远端目录删除清理不能穿越用户随后放入的外部联接。</summary>
    [Fact(Timeout=120000)]
    public async Task RemoteDeleteNeverTraversesExternalJunction()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);test.Dav.Objects["folder"]=new([],"\"folder\"",true);await Refresh(test);
        var outside=Path.Combine(test.Dav.Area,"external");Directory.CreateDirectory(outside);await File.WriteAllTextAsync(Path.Combine(outside,"private.txt"),"keep outside");
        ClearRemovalTests.CreateJunction(Path.Combine(test.Local,"folder","link"),outside);test.Dav.Objects.TryRemove("folder",out _);
        var result=await Refresh(test);Assert.Equal("PartialFailure",result.State);Assert.Equal("keep outside",await File.ReadAllTextAsync(Path.Combine(outside,"private.txt")));
        Assert.True(Directory.Exists(Path.Combine(test.Local,"folder","link")));Assert.DoesNotContain(test.Dav.Server.Requests,request=>request.Method=="DELETE");
    }
    /// <summary>原创建请求发送后结果丢失且目标不存在时，不自动猜测重发；显式重试仍可创建。</summary>
    [Fact(Timeout=120000)]
    public async Task UnknownCreateMissingAfterRestartRequiresExplicitRetry()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"unknown.txt");var bytes=Encoding.UTF8.GetBytes("uncertain write");await File.WriteAllBytesAsync(path,bytes);await test.Manager.DisposeAsync();
        var stamp=LocalFileState.Read(path);var area=Path.Combine(test.Repository.DataDirectory,"UploadStaging",test.Id.ToString("N"));Directory.CreateDirectory(area);var snapshot=Path.Combine(area,"unknown.snapshot");await File.WriteAllBytesAsync(snapshot,bytes);
        var item=new LocalUpload(Guid.NewGuid().ToString("N"),test.Id,"unknown.txt","unknown.txt",false,stamp.Key,1,"Sending",DateTimeOffset.UtcNow.AddSeconds(-5),SnapshotPath:snapshot,SnapshotSha256:Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),SnapshotStamp:stamp.Key,SnapshotIdentity:stamp.Identity,SnapshotGeneration:1,Total:bytes.Length);
        test.Repository.SaveUpload(item);var writes=Mutations(test.Dav);test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await test.Wait("unknown.txt",state:"Conflict");await Task.Delay(2300);
        Assert.Equal(writes,Mutations(test.Dav));Assert.False(test.Dav.Objects.ContainsKey("unknown.txt"));Assert.True(test.Manager.RetryUpload(item.Id));await test.Wait("unknown.txt");
    }
    /// <summary>远端删除撞上已发出的条件更新时，保留本地修改并核对请求，不提交错误同步状态。</summary>
    [Fact(Timeout=120000)]
    public async Task RemoteDeletionDuringSentUpdatePreservesLocalAndDoesNotRecreate()
    {
        await using var test=await UploadFlowTests.Fixture.Create();var path=Path.Combine(test.Local,"racing.txt");await File.WriteAllTextAsync(path,"first version");await test.Wait("racing.txt");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var handler=test.Dav.Server.Handler;
        test.Dav.Server.Handler=async request=>{if(request.Method=="PUT"&&request.Target.EndsWith("racing.txt",StringComparison.Ordinal)){entered.TrySetResult();await release.Task;}return await handler(request);};
        try
        {
            await File.WriteAllTextAsync(path,"edited during deletion");await entered.Task.WaitAsync(TimeSpan.FromSeconds(25));test.Dav.Objects.TryRemove("racing.txt",out _);
            var result=await Refresh(test);Assert.Equal("PartialFailure",result.State);Assert.Equal("edited during deletion",await File.ReadAllTextAsync(path));release.TrySetResult();
            var deadline=DateTime.UtcNow.AddSeconds(20);
            while(DateTime.UtcNow<deadline)
            {
                var current=test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="racing.txt");if(current.State=="Conflict")break;
                if(current.State=="OutcomeUnknown")test.Manager.RetryUpload(current.Id);await Task.Delay(100);
            }
            Assert.Equal("Conflict",test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="racing.txt").State);Assert.False(test.Dav.Objects.ContainsKey("racing.txt"));
            Assert.True(test.Repository.Entries(test.Id).Single(item=>item.SourcePath=="racing.txt").Protected);
        }
        finally{release.TrySetResult();test.Dav.Server.Handler=handler;}
    }
    /// <summary>旧九列索引迁移后默认未知，远端删除状态可持久化且不污染本地删除标记。</summary>
    [Fact]
    public async Task LegacyIndexMigrationKeepsLocalFlagsAndPersistsRemotePresence()
    {
        await using var dav=new DavFixture();var data=Path.Combine(dav.Area,"legacy");Directory.CreateDirectory(data);var id=Guid.NewGuid();
        using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(data,"mounts.db")+";Pooling=False"))
        {
            db.Open();using var command=db.CreateCommand();command.CommandText="CREATE TABLE entries(mount TEXT,id TEXT,source TEXT,local TEXT,version TEXT,directory INTEGER,deleted INTEGER,protected INTEGER,materialized INTEGER,PRIMARY KEY(mount,id));INSERT INTO entries VALUES($m,'item','file','file','v1',0,0,0,1);PRAGMA user_version=2";command.Parameters.AddWithValue("$m",id.ToString("N"));command.ExecuteNonQuery();
        }
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");var repository=new MountRepository(data,registry);var entry=Assert.Single(repository.Entries(id));Assert.Equal(RemotePresence.Unknown,entry.RemotePresence);
        Assert.True(repository.SetRemotePresence(id,entry,RemotePresence.Deleted));repository.SaveEntry(id,entry);
        repository=new(data,registry);entry=Assert.Single(repository.Entries(id));Assert.Equal(RemotePresence.Deleted,entry.RemotePresence);Assert.False(entry.Deleted);Assert.False(entry.Protected);
        Assert.False(repository.SetRemotePresence(id,entry with{Version="stale"},RemotePresence.Present));Assert.Equal(RemotePresence.Deleted,Assert.Single(repository.Entries(id)).RemotePresence);
        Assert.True(repository.SetRemotePresence(id,entry,RemotePresence.Present));Assert.Equal(RemotePresence.Present,Assert.Single(new MountRepository(data,registry).Entries(id)).RemotePresence);
    }
    /// <summary>通知风暴及停止后的迟到通知不以信号量已满或已释放异常作为正常控制流。</summary>
    [Fact]
    public void ConcurrentWakeStopAndLateNotificationsProduceNoControlFlowExceptions()
    {
        var type=typeof(MountManager).GetNestedType("UploadRuntime",System.Reflection.BindingFlags.NonPublic)!;
        var runtime=Activator.CreateInstance(type,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,[CancellationToken.None],null)!;
        var wake=(Action)type.GetMethod("Wake")!.CreateDelegate(typeof(Action),runtime);var stop=(Action)type.GetMethod("RequestStop")!.CreateDelegate(typeof(Action),runtime);
        var signal=(SemaphoreSlim)type.GetField("Signal")!.GetValue(runtime)!;var count=0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe=(_,args)=>{if(args.Exception is SemaphoreFullException||args.Exception is ObjectDisposedException&&args.Exception.StackTrace?.Contains("UploadRuntime",StringComparison.Ordinal)==true)Interlocked.Increment(ref count);};
        AppDomain.CurrentDomain.FirstChanceException+=observe;
        try
        {
            Parallel.Invoke(()=>Parallel.For(0,20000,_=>wake()),()=>{for(var index=0;index<20000;index++)signal.Wait(0);});
            signal.Wait(0);wake();Assert.Equal(1,signal.CurrentCount);Parallel.For(0,32,_=>stop());
            Parallel.Invoke(()=>((IDisposable)runtime).Dispose(),()=>Parallel.For(0,20000,_=>wake()));Assert.Equal(0,Volatile.Read(ref count));
        }
        finally{AppDomain.CurrentDomain.FirstChanceException-=observe;((IDisposable)runtime).Dispose();}
    }
    /// <summary>可空存在性查询不产生自有 FileNotFound／冲突包装，真实条件冲突仍按原接口抛出。</summary>
    [Fact]
    public async Task NullableMetadataAndExpectedS3ProbeAvoidExtraDomainExceptions()
    {
        await using var dav=new DavFixture();await using var davSource=dav.Source();Assert.Null(await davSource.TryGetMetadataAsync("missing"));
        await using var s3=new S3Fixture();await using var source=s3.Source();var count=0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe=(_,args)=>{if(args.Exception is SourceConflictException||args.Exception is FileNotFoundException&&args.Exception.StackTrace?.Contains("S3FileSource",StringComparison.Ordinal)==true)Interlocked.Increment(ref count);};
        AppDomain.CurrentDomain.FirstChanceException+=observe;
        try{Assert.Null(await source.TryGetMetadataAsync("missing"));await source.UploadAsync("fresh.txt",new MemoryStream([1,2,3]),null);Assert.Equal(0,Volatile.Read(ref count));}
        finally{AppDomain.CurrentDomain.FirstChanceException-=observe;}
        await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync("fresh.txt",new MemoryStream([4]),null));
    }

    /// <summary>空但仍标记有后续页的 S3 响应不能被当作远端不存在。</summary>
    [Fact]
    public async Task IncompleteS3ExistenceResponseCannotConfirmRemoteDeletion()
    {
        await using var fixture=new S3Fixture();var handler=fixture.Server.Handler;
        fixture.Server.Handler=request=>request.Method=="GET"&&request.Target.Contains("list-type=2",StringComparison.Ordinal)
            ?Task.FromResult(HttpTestServer.Response.Text(200,"<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><IsTruncated>true</IsTruncated><NextContinuationToken>next</NextContinuationToken></ListBucketResult>")):handler(request);
        await using var source=fixture.Source();await Assert.ThrowsAsync<IOException>(()=>source.TryGetMetadataAsync("missing"));
    }

    /// <summary>确定性覆盖准备已完成但尚未发送的窗口：刷新确认删除后，旧快照不能把冲突状态覆盖为 Sending。</summary>
    [Fact(Timeout=120000)]
    public async Task PreparedSnapshotCannotCrossDeletionBarrierAtSendBoundary()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");await Task.Run(()=>File.ReadAllBytes(path));await File.WriteAllTextAsync(path,"prepared local modification");await test.Manager.ScanUploadChangesAsync(test.Id);
        var current=test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="文件.bin");var stamp=LocalFileState.Read(path);
        var staging=Path.Combine(test.Repository.DataDirectory,"UploadStaging",test.Id.ToString("N"));Directory.CreateDirectory(staging);var snapshot=Path.Combine(staging,"boundary.snapshot");await File.WriteAllTextAsync(snapshot,"prepared local modification");
        var prepared=current with{State="Prepared",SnapshotPath=snapshot,SnapshotStamp=stamp.Key,SnapshotIdentity=stamp.Identity,SnapshotGeneration=current.Generation,SnapshotSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(snapshot))),Total=new FileInfo(snapshot).Length};test.Repository.SaveUpload(prepared);
        Assert.Null(await test.Manager.BeginUploadSendAsync(test.Id,prepared));Assert.Equal("Prepared",test.Repository.Upload(current.Id)!.State);
        test.Dav.Objects.TryRemove("文件.bin",out _);var writes=Mutations(test.Dav);Assert.Equal("PartialFailure",(await Refresh(test)).State);
        await Assert.ThrowsAsync<SourceConflictException>(()=>test.Manager.BeginUploadSendAsync(test.Id,prepared));
        Assert.Equal("Conflict",test.Repository.Upload(current.Id)!.State);Assert.Equal(writes,Mutations(test.Dav));Assert.False(test.Dav.Objects.ContainsKey("文件.bin"));Assert.Equal("prepared local modification",await File.ReadAllTextAsync(path));
    }

}

/// <summary>进程级首次异常事件必须与其他测试隔离，防止把其他协议测试的预期冲突计入本轮。</summary>
[CollectionDefinition("Process exception diagnostics",DisableParallelization=true)]
public sealed class ProcessExceptionDiagnosticsCollection;
