using System.Security.Cryptography;
using System.Text;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>对上传提交保护释放和对象类型切换设置实例级屏障，验证真实 NTFS/CFAPI 状态与持久队列。</summary>
public sealed class P1UploadConsistencyTests
{
    /// <summary>精确安排保护释放后关闭新编辑，根锁尚未允许扫描，完成记录不能吸收新内容的状态戳。</summary>
    [Fact(Timeout=120000)]
    public async Task EditAfterProtectedCommitKeepsVerifiedStampAndUploadsNextGeneration()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"guard-release.txt");
        await File.WriteAllTextAsync(path,"content A");var committed=new TaskCompletionSource<LocalUpload>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? editedStamp=null;var injected=false;
        test.Manager.UploadCheckpointForTests=async(stage,item)=>
        {
            if(item.LocalPath!="guard-release.txt")return;
            if(stage=="CommitVerified"&&!injected)
            {
                injected=true;Assert.Equal(item.Generation,test.Repository.Upload(item.Id)!.Generation);
                await File.WriteAllTextAsync(path,"content B");editedStamp=LocalFileState.Read(path).Key;
                Assert.Equal(item.Generation,test.Repository.Upload(item.Id)!.Generation);
            }
            else if(stage=="Committed"&&injected&&!committed.Task.IsCompleted)
            {
                Assert.Equal("content A",Encoding.UTF8.GetString(test.Dav.Objects[item.SourcePath].Bytes));committed.TrySetResult(item);
            }
        };
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        var first=await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));Assert.Equal("Completed",first.State);Assert.NotNull(editedStamp);Assert.NotEqual(editedStamp,first.ObservedStamp);
        var final=await test.Wait("guard-release.txt",bytes:Encoding.UTF8.GetBytes("content B"));Assert.True(final.Generation>first.Generation);
        Assert.Equal(2,TargetWrites(test,"guard-release.txt"));Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);
    }

    /// <summary>保护已释放但索引事务未执行时中断，重建管理器后仍核对旧回执并上传新编辑。</summary>
    [Fact(Timeout=120000)]
    public async Task RestartAfterProtectedCommitDoesNotLoseClosedEdit()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"commit-restart.txt");await File.WriteAllTextAsync(path,"before stop");
        var interrupted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        test.Manager.UploadCheckpointForTests=async(stage,item)=>
        {
            if(stage!="CommitVerified"||item.LocalPath!="commit-restart.txt")return;
            await File.WriteAllTextAsync(path,"after restart");interrupted.TrySetResult();await release.Task;throw new IOException("模拟保护释放后、事务提交前中断");
        };
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // 先停止调度再释放故障屏障，不依赖测试线程在重试间隔内获得 CPU。
        var stopping=test.Manager.DisposeAsync().AsTask();release.TrySetResult();await stopping;
        var pending=test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="commit-restart.txt");Assert.Equal("RemoteCommitted",pending.State);
        Assert.Equal("before stop",Encoding.UTF8.GetString(test.Dav.Objects["commit-restart.txt"].Bytes));
        Assert.NotNull(pending.Receipt);Assert.NotNull(pending.SnapshotPath);Assert.True(File.Exists(pending.SnapshotPath));
        test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);
        await test.Wait("commit-restart.txt",bytes:Encoding.UTF8.GetBytes("after restart"));Assert.Equal(2,TargetWrites(test,"commit-restart.txt"));
    }

    /// <summary>模拟旧版完成记录错误保存了脏文件观察戳；重连扫描即使戳相同也必须恢复上传。</summary>
    [Fact(Timeout=120000)]
    public async Task LegacyCompletedDirtyStampIsRecoveredOnReconnect()
    {
        await using var test=await UploadFlowTests.Fixture.Create();var path=Path.Combine(test.Local,"legacy-dirty.txt");await File.WriteAllTextAsync(path,"uploaded baseline");
        var original=await test.Wait("legacy-dirty.txt");await test.Manager.DisposeAsync();await File.WriteAllTextAsync(path,"previously swallowed edit");
        var stamp=LocalFileState.Read(path);test.Repository.SaveUpload(original with{ObservedStamp=stamp.Key});
        test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);
        var result=await test.Wait("legacy-dirty.txt",bytes:Encoding.UTF8.GetBytes("previously swallowed edit"));
        Assert.True(result.Generation>original.Generation);Assert.Equal(2,TargetWrites(test,"legacy-dirty.txt"));
    }

    /// <summary>持久提交在同一事务保存核验戳；新代次出现时不覆盖其观察戳或解除保护。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryCommitsOnlyVerifiedGeneration(bool newerGeneration)
    {
        // 此处只测 SQLite 事务；不启动扫描器，避免未转换的普通测试文件被后台再次保护。
        var area=Directory.CreateTempSubdirectory("HybridMount.P1Repository.").FullName;
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");var mount=Guid.NewGuid();
        try
        {
            var repository=new MountRepository(Path.Combine(area,"data"),registry);var path=Path.Combine(area,"transaction.txt");await File.WriteAllTextAsync(path,"snapshot");
            var digest=Convert.ToHexString(SHA256.HashData("snapshot"u8));var stamp=LocalFileState.Read(path);var prepared=new LocalUpload(Guid.NewGuid().ToString("N"),mount,"transaction.txt","transaction.txt",false,stamp.Key,1,"RemoteCommitted",DateTimeOffset.UtcNow,
                SnapshotStamp:stamp.Key,SnapshotIdentity:stamp.Identity,SnapshotGeneration:1,SnapshotSha256:digest,Total:8);
            repository.SaveUpload(newerGeneration?prepared with{Generation=2,ObservedStamp="new-generation-observation"}:prepared);
            var receipt=new SourceEntry("","transaction.txt",false,8,DateTimeOffset.UtcNow,"remote-A");var entry=new MountEntry(prepared.Id,receipt.Path,prepared.LocalPath,receipt.Version,false,Materialized:true);
            var verified=stamp with{Usn=stamp.Usn+100};var result=repository.CommitUploadedEntry(prepared,entry,receipt,verified);
            Assert.Equal(newerGeneration?"Queued":"Completed",result.State);Assert.Equal(newerGeneration?"new-generation-observation":verified.Key,result.ObservedStamp);
            Assert.Equal(result,repository.Upload(prepared.Id));Assert.Equal(newerGeneration,repository.Entries(mount).Single(item=>item.Id==entry.Id).Protected);
            Assert.Equal("remote-A",result.ExpectedVersion);Assert.Null(result.SnapshotStamp);Assert.Equal(digest,repository.Baseline(mount,"transaction.txt","remote-A"));
        }
        finally{Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);Directory.Delete(area,true);}
    }

    /// <summary>工作器已选中任务后扫描撤销其资格，即使对象恢复原类型，准备阶段也不能清除冲突。</summary>
    [Theory(Timeout=120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedIntentCannotEraseTypeConflictAfterObjectRestored(bool directory)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"revoked-intent");Create(path,directory);await test.Manager.ScanUploadChangesAsync(test.Id);
        var selected=test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="revoked-intent");Replace(path,directory);await test.Manager.ScanUploadChangesAsync(test.Id);
        Replace(path,!directory);await test.Manager.ScanUploadChangesAsync(test.Id);
        // 使用旧的已选意图调用生产准备入口，当前数据库中已经是扫描确认的冲突。
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var root=typeof(MountManager).GetMethod("Root",flags)!.Invoke(test.Manager,[test.Id]);
        var operation=(Task<LocalUpload?>)typeof(MountManager).GetMethod("PrepareUploadAsync",flags)!.Invoke(test.Manager,[root,selected,CancellationToken.None])!;
        Assert.Null(await operation);var conflict=test.Repository.Upload(selected.Id)!;
        Assert.Equal("Conflict",conflict.State);Assert.Equal(CloudUploadState.TypeConflictMessage,conflict.Error);Assert.Equal(0,TargetWrites(test,"revoked-intent"));
    }

    /// <summary>排队期间文件和目录互换统一进入持久冲突；恢复原类型必须显式重试。</summary>
    [Theory(Timeout=120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedTypeChangeStaysConflictAcrossRestartAndRetry(bool directory)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"queued-type");Create(path,directory);await test.Manager.ScanUploadChangesAsync(test.Id);
        var initial=test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="queued-type");Replace(path,directory);await test.Manager.ScanUploadChangesAsync(test.Id);
        var conflict=test.Repository.Upload(initial.Id)!;Assert.Equal("Conflict",conflict.State);Assert.Equal(directory,conflict.IsDirectory);
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Equal(conflict.Generation,test.Repository.Upload(initial.Id)!.Generation);
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await test.Wait("queued-type",state:"Conflict");
        Assert.True(test.Manager.RetryUpload(initial.Id));await test.Wait("queued-type",state:"Conflict");Assert.Equal(0,TargetWrites(test,"queued-type"));
        Replace(path,!directory);await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Equal("Conflict",test.Repository.Upload(initial.Id)!.State);
        Assert.True(test.Manager.RetryUpload(initial.Id));await test.Wait("queued-type");Assert.Equal(1,TargetWrites(test,"queued-type"));
    }

    /// <summary>在准备、发送和远端提交边界切换类型；旧意图不得创建错误目标或标记新对象同步。</summary>
    [Theory(Timeout=120000)]
    [InlineData(false,"BeforePrepare")]
    [InlineData(true,"BeforePrepare")]
    [InlineData(false,"Prepared")]
    [InlineData(true,"Prepared")]
    [InlineData(false,"BeforeSend")]
    [InlineData(true,"BeforeSend")]
    [InlineData(false,"RemoteCommitted")]
    [InlineData(true,"RemoteCommitted")]
    public async Task TypeChangeAtPipelineBoundaryNeverMarksReplacementInSync(bool directory,string boundary)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"boundary-type");Create(path,directory);await test.Manager.ScanUploadChangesAsync(test.Id);var injected=false;
        test.Manager.UploadCheckpointForTests=(stage,item)=>
        {
            if(stage==boundary&&item.LocalPath=="boundary-type"&&!injected){injected=true;Replace(path,directory);}return Task.CompletedTask;
        };
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);var result=await test.Wait("boundary-type",state:"Conflict");
        Assert.True(injected);Assert.Equal(CloudUploadState.TypeConflictMessage,result.Error);Assert.Equal(directory,result.IsDirectory);
        Assert.NotEqual(1u,CloudFilesNative.Inspect(path)?.Info.InSyncState??0u);Assert.Equal(boundary=="RemoteCommitted"?1:0,TargetWrites(test,"boundary-type"));
        if(boundary=="RemoteCommitted"){Assert.NotNull(result.Receipt);Assert.NotNull(result.SnapshotStamp);if(!directory)Assert.True(File.Exists(result.SnapshotPath));}
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Equal("Conflict",test.Repository.Upload(result.Id)!.State);
    }

    /// <summary>网络发送期间扫描观察到类型替换，保留发送阶段与回执，远端确认后必须持久保持冲突。</summary>
    [Theory(Timeout=120000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightTypeChangeKeepsReceiptAndConflictAcrossRestart(bool directory)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"inflight-type");Create(path,directory);await test.Manager.ScanUploadChangesAsync(test.Id);
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var handler=test.Dav.Server.Handler;
        test.Dav.Server.Handler=async request=>
        {
            if(request.Method is "PUT" or "MKCOL"&&new Uri(test.Dav.Server.Address,request.Target).AbsolutePath.TrimEnd('/')=="/dav/inflight-type")
            {entered.TrySetResult();await release.Task;}
            return await handler(request);
        };
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));Replace(path,directory);await test.Manager.ScanUploadChangesAsync(test.Id);
            var sending=test.Repository.Uploads(test.Id).Single(item=>item.LocalPath=="inflight-type");Assert.Equal("Sending",sending.State);Assert.Equal(CloudUploadState.TypeConflictMessage,sending.Error);
        }
        finally{release.TrySetResult();}
        var conflict=await test.Wait("inflight-type",state:"Conflict");Assert.NotNull(conflict.Receipt);Assert.NotNull(conflict.SnapshotStamp);Assert.Equal(1,TargetWrites(test,"inflight-type"));
        await test.Manager.DisposeAsync();test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);
        var restored=await test.Wait("inflight-type",state:"Conflict");Assert.Equal(conflict.Receipt,restored.Receipt);Assert.Equal(conflict.SnapshotPath,restored.SnapshotPath);
        Assert.NotEqual(1u,CloudFilesNative.Inspect(path)?.Info.InSyncState??0u);Assert.Equal(1,TargetWrites(test,"inflight-type"));
    }

    /// <summary>错误远端回执类型必须在任何原生转换或仓库同步提交之前拒绝。</summary>
    [Fact(Timeout=120000)]
    public async Task WrongReceiptTypeCannotConvertOrCommitLocalFile()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"wrong-receipt.txt");await File.WriteAllTextAsync(path,"keep file");var stamp=LocalFileState.Read(path);
        var item=new LocalUpload(Guid.NewGuid().ToString("N"),test.Id,"wrong-receipt.txt","wrong-receipt.txt",false,stamp.Key,1,"RemoteCommitted",DateTimeOffset.UtcNow,
            SnapshotStamp:stamp.Key,SnapshotIdentity:stamp.Identity,SnapshotGeneration:1,SnapshotSha256:Convert.ToHexString(SHA256.HashData("keep file"u8)));
        var receipt=new SourceEntry("",item.SourcePath,true,0,DateTimeOffset.UtcNow,"bad-receipt");var entry=new MountEntry(item.Id,item.SourcePath,item.LocalPath,receipt.Version,true,Materialized:true);
        test.Repository.SaveUpload(item);var mount=test.Manager.Mounts.Single(x=>x.Definition.Id==test.Id).Definition;
        Assert.Throws<SourceConflictException>(()=>CloudUploadState.Commit(mount,entry,item));Assert.Null(CloudFilesNative.Inspect(path));
        Assert.Throws<SourceConflictException>(()=>test.Repository.CommitUploadedEntry(item,entry,receipt,stamp));Assert.Equal("RemoteCommitted",test.Repository.Upload(item.Id)!.State);
        Assert.DoesNotContain(test.Repository.Entries(test.Id),candidate=>candidate.Id==item.Id);
    }

    /// <summary>只在测试根中建立或替换空目录/普通文件，保留其他测试数据。</summary>
    private static void Create(string path,bool directory){if(directory)Directory.CreateDirectory(path);else File.WriteAllText(path,"original file bytes");}
    private static void Replace(string path,bool wasDirectory){if(wasDirectory)Directory.Delete(path);else File.Delete(path);Create(path,!wasDirectory);}
    private static int TargetWrites(UploadFlowTests.Fixture test,string path)=>test.Dav.Server.Requests.Count(request=>request.Method is "PUT" or "MKCOL"&&new Uri(test.Dav.Server.Address,request.Target).AbsolutePath.TrimEnd('/').Equals(new Uri(test.Dav.Options.Endpoint!).AbsolutePath.TrimEnd('/')+"/"+path,StringComparison.Ordinal));
}
