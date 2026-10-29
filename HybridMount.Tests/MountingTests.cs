using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using System.Runtime.InteropServices;

namespace HybridMount.Tests;

/// <summary>模拟源的真实持久读写，以及 Windows 驱动上的真实占位生命周期。</summary>
public sealed class MountingTests
{
    [Fact]
    public async Task SourceRangeVersionsMoveDeleteAndRestart()
    {
        var root=Path.Combine(Path.GetTempPath(),"HybridMount.SourceTests",Guid.NewGuid().ToString("N"));
        try
        {
            string id,version;
            using(var source=new MockFileSource(root))
            {
                await source.CreateDirectoryAsync("中文");
                var original=Enumerable.Range(0,10037).Select(i=>(byte)(i*31)).ToArray();
                var entry=await source.UploadAsync("中文/文件.bin",new MemoryStream(original),null);
                id=entry.StableId;version=entry.Version;
                var bytes=new byte[5000];Assert.Equal(5000,await source.ReadRangeAsync(entry,4093,bytes));Assert.Equal(original[4093..9093],bytes);
                await Assert.ThrowsAsync<SourceConflictException>(()=>source.UploadAsync(entry.Path,Stream.Null,"wrong"));
                var replacement=await source.UploadAsync(entry.Path,new MemoryStream([1,2,3]),entry.Version);
                Assert.Equal(id,replacement.StableId);Assert.NotEqual(version,replacement.Version);
                await Assert.ThrowsAsync<SourceConflictException>(async()=>await source.ReadRangeAsync(entry,0,bytes));
                var moved=await source.MoveAsync("中文","新目录",(await source.GetMetadataAsync("中文")).Version);
                Assert.True(moved.IsDirectory);Assert.Equal(id,(await source.GetMetadataAsync("新目录/文件.bin")).StableId);
                using var canceled=new CancellationTokenSource();canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>source.UploadAsync("取消.bin",Stream.Null,null,canceled.Token));
            }
            using(var reopened=new MockFileSource(root))
            {
                var entry=await reopened.GetMetadataAsync("新目录/文件.bin");Assert.Equal(id,entry.StableId);
                var bytes=new byte[9];Assert.Equal(3,await reopened.ReadRangeAsync(entry,0,bytes));Assert.Equal(new byte[]{1,2,3},bytes[..3]);
                await reopened.DeleteAsync("新目录",(await reopened.GetMetadataAsync("新目录")).Version);
                await Assert.ThrowsAsync<FileNotFoundException>(()=>reopened.GetMetadataAsync("新目录/文件.bin"));
                Assert.Equal(CapabilitySupport.Unsupported,reopened.Capabilities.ChangeNotify);
            }
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory]
    [InlineData("../a")][InlineData("a/../b")][InlineData("a\\b")][InlineData("CON.txt")][InlineData("a.")]
    public void RejectUnsafeNames(string path)=>Assert.Throws<ArgumentException>(()=>RelativePaths.Normalize(path));
    [Fact]
    public void CfApiAbiMatchesWindowsSdk()
    {
        Assert.Equal(152,Marshal.SizeOf<CloudFilesNative.CallbackInfo>());
        Assert.Equal(48,Marshal.SizeOf<CloudFilesNative.OperationInfo>());
        Assert.Equal(48,Marshal.SizeOf<CloudFilesNative.OperationParameters>());
        Assert.Equal(48,Marshal.SizeOf<CloudFilesNative.FsMetadata>());
        Assert.Equal(88,Marshal.SizeOf<CloudFilesNative.PlaceholderCreateInfo>());
    }
    /// <summary>本机 NTFS/Cloud Files 驱动必须真实可用；失败如实报告，不降级为普通空文件。</summary>
    [Fact(Timeout=120000)]
    public async Task RealCloudFilesHydrateProtectRestartAndRemove()
    {
        await RecoverPreviousTestRoots();
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.CfTests",Guid.NewGuid().ToString("N"));
        var local=Path.Combine(area,"view");var data=Path.Combine(area,"data");Directory.CreateDirectory(local);
        var registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        var repository=new MountRepository(data,registry);
        await using var logger=new LoggerService(Path.Combine(area,"log.db"));
        Guid id=default;
        try
        {
            await using(var manager=new MountManager(repository,logger))
            {
                var mount=await manager.AddAsync("CFAPI 测试",local,true).WaitAsync(TimeSpan.FromSeconds(30));id=mount.Id;
                Assert.Equal(id,MountPathGuard.ReadRoot(local)!.MountId);
                var sample=Path.Combine(local,"说明.txt");Assert.NotNull(CloudFilesNative.Inspect(sample));
                var read=await Task.Run(()=>File.ReadAllText(sample)).WaitAsync(TimeSpan.FromSeconds(20));Assert.Contains("按需下载",read);
                var canceled=manager.Queue(id,"keepOnDevice");manager.Cancel(canceled);
                var cancelDeadline=DateTime.UtcNow.AddSeconds(15);
                while(manager.GetOperation(canceled)!.State is "Queued" or "Running") { if(DateTime.UtcNow>cancelDeadline)throw new TimeoutException("取消未完成。");await Task.Delay(20); }
                Assert.Contains(manager.GetOperation(canceled)!.State,new[]{"Canceled","Completed"});
                var job=manager.Queue(id,"keepOnDevice",[sample]);await Finished(manager,job);
                Assert.Equal(1u,CloudFilesNative.Inspect(sample)!.Value.Info.PinState);
                job=manager.Queue(id,"freeSpace",[sample]);var released=await Finished(manager,job);
                Assert.True(released.Results.Any(item=>item.Status=="Completed"),string.Join(";",released.Results)+" index="+System.Text.Json.JsonSerializer.Serialize(repository.Entries(id))+" info="+CloudFilesNative.Inspect(sample)!.Value.Info.InSyncState);
                Assert.False(CloudFilesNative.HasFullContent(sample,new FileInfo(sample).Length));
                Assert.Contains("按需下载",await Task.Run(()=>File.ReadAllText(sample)).WaitAsync(TimeSpan.FromSeconds(20)));
                await File.WriteAllTextAsync(sample,"本地修改必须保留");
                job=manager.Queue(id,"freeSpace",[sample]);var protectedJob=await Finished(manager,job);Assert.Contains(protectedJob.Results,item=>item.Status=="Skipped");
                await File.WriteAllTextAsync(Path.Combine(local,"本地新建.txt"),"local only");
                File.Delete(Path.Combine(local,"空文件.txt"));
                File.Move(Path.Combine(local,"中文目录","多层","示例.txt"),Path.Combine(local,"中文目录","多层","改名.txt"));
                Assert.True(MountPathGuard.ContainsCloudPlaceholders(local));
                await manager.DisconnectAsync(id);await manager.ConnectAsync(id);
                Assert.False(File.Exists(Path.Combine(local,"空文件.txt")));
                Assert.True(File.Exists(Path.Combine(local,"中文目录","多层","改名.txt")));
            }
            await using(var reopened=new MountManager(repository,logger))
            {
                await reopened.StartAsync();Assert.Equal("已连接",Assert.Single(reopened.Mounts).Status);
                Assert.Equal("本地修改必须保留",await File.ReadAllTextAsync(Path.Combine(local,"说明.txt")));
                var job=reopened.Queue(id,"remove");await Finished(reopened,job);
                Assert.Equal("local only",await File.ReadAllTextAsync(Path.Combine(local,"本地新建.txt")));
                Assert.Empty(reopened.Mounts);Assert.Null(MountPathGuard.ReadRoot(local));
                Assert.False(MountPathGuard.ContainsCloudPlaceholders(local));
                Assert.Null(CloudFilesNative.Inspect(Path.Combine(local,"说明.txt")));
                Assert.Equal("本地修改必须保留",await File.ReadAllTextAsync(Path.Combine(local,"说明.txt")));
                Assert.False(File.Exists(Path.Combine(local,"空文件.txt")));
                var large=Path.Combine(local,"范围读取.bin");Assert.Equal(32*1024*1024+37,new FileInfo(large).Length);
                await using var stream=File.OpenRead(large);stream.Position=4093;var bytes=new byte[9000];await stream.ReadExactlyAsync(bytes);
                Assert.Equal(Enumerable.Range(4093,9000).Select(index=>(byte)(index*31+17)),bytes);
                stream.Position=stream.Length-37;var tail=new byte[37];await stream.ReadExactlyAsync(tail);
                Assert.Equal(Enumerable.Range((int)stream.Length-37,37).Select(index=>(byte)(index*31+17)),tail);
            }
        }
        finally
        {
            var leftovers=await logger.ReadAsync();
            foreach(var line in leftovers)System.Diagnostics.Trace.WriteLine(line.DisplayText);
            // 未成功解除时保留测试现场及配置，避免删除尚未下载的云文件。
            if(id==default||(MountPathGuard.ReadRoot(local) is null&&!MountPathGuard.ContainsCloudPlaceholders(local)))
            {Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(registry,false);await logger.DisposeAsync();Directory.Delete(area,true);}
        }
    }
    private static async Task RecoverPreviousTestRoots()
    {
        using var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HybridMount.NET.Tests");
        if(key is null)return;
        foreach(var name in key.GetSubKeyNames())
        {
            var registry=@"Software\HybridMount.NET.Tests\"+name;
            using var child=key.OpenSubKey(name);
            if(child is null)continue;
            foreach(var mountName in child.GetSubKeyNames())
            {
                using var mountKey=child.OpenSubKey(mountName);
                if(mountKey?.GetValue("Definition") is not string json)continue;
                var mount=System.Text.Json.JsonSerializer.Deserialize<MountDefinition>(json)!;
                var testBase=Path.Combine(Path.GetTempPath(),"HybridMount.CfTests");
                if(mount.Name!="CFAPI 测试"||!MountPathGuard.IsUnder(testBase,mount.LocalPath)||!Directory.Exists(mount.LocalPath)||MountPathGuard.ReadRoot(mount.LocalPath)?.MountId!=mount.Id)continue;
                var area=Path.GetDirectoryName(mount.LocalPath)!;
                var repository=new MountRepository(Path.Combine(area,"data"),registry);
                await using var logger=new LoggerService(Path.Combine(area,"recovery.db"));
                await using var manager=new MountManager(repository,logger);
                var job=manager.Queue(mount.Id,"remove");await Finished(manager,job);
                // 仅解除测试注册，保留上次测试目录供诊断。
            }
        }
    }
    private static async Task<MountOperationSnapshot> Finished(MountManager manager,string id)
    {
        var deadline=DateTime.UtcNow.AddSeconds(45);
        while(DateTime.UtcNow<deadline)
        {
            var job=manager.GetOperation(id)!;
            if(job.State is not("Queued" or "Running")){Assert.True(job.State=="Completed",$"{job.State}: {job.Error}; "+string.Join(";",job.Results.Select(item=>item.Message)));return job;}
            await Task.Delay(30);
        }
        manager.Cancel(id);throw new TimeoutException("真实 CFAPI 操作超时。");
    }
}
