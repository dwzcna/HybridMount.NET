using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Tests;

/// <summary>真实 NTFS／CFAPI 上验证清空、模式恢复与外部链接边界；所有数据均在隔离测试目录。</summary>
public sealed class ClearRemovalTests
{
    [Fact]
    public void OldPendingDefinitionDefaultsToPreserveFiles()
    {
        var definition=JsonSerializer.Deserialize<MountDefinition>("""
            {"Id":"2174f266-9382-41e1-9920-febd82ecdb90","Name":"旧挂载","LocalPath":"D:\\LegacyView","AutoConnect":true,"RemovalPending":true,"RootIdentity":"old"}
            """)!;
        Assert.Equal(MountRemovalMode.PreserveFiles,definition.RemovalMode);
        Assert.Equal(MountRemovalStage.Working,definition.RemovalStage);
    }
    [Fact(Timeout=120000)]
    public async Task ClearOfflineFilesAndLocalChangesWithoutSourceAndKeepExternalTarget()
    {
        await using var test=await Fixture.CreateAsync();
        var large=Path.Combine(test.Local,"范围读取.bin");
        Assert.False(CloudFilesNative.HasFullContent(large,new FileInfo(large).Length));
        await File.WriteAllTextAsync(Path.Combine(test.Local,"说明.txt"),"本地修改将被明确清空");
        Directory.CreateDirectory(Path.Combine(test.Local,"本地目录"));
        await File.WriteAllTextAsync(Path.Combine(test.Local,"本地目录","新文件.txt"),"new local");
        var outside=Path.Combine(test.Area,"outside");Directory.CreateDirectory(outside);
        var sentinel=Path.Combine(outside,"保留.txt");await File.WriteAllTextAsync(sentinel,"external sentinel");
        CreateJunction(Path.Combine(test.Local,"本地目录","外部链接"),outside);
        await test.Manager.DisconnectAsync(test.Mount.Id);
        SourceEntry original;
        using(var source=new MockFileSource(test.Source))original=await source.GetMetadataAsync("范围读取.bin");
        // 暂时移走自有模拟内容；清空若错误触发下载便无法成功，之后恢复源并核对版本和字节。
        var content=Path.Combine(test.Source,"content");var backup=Path.Combine(test.Source,"content-offline");
        Directory.Move(content,backup);
        try
        {
            var job=await Terminal(test.Manager,test.Manager.Queue(test.Mount.Id,"clearAndRemove"));
            Assert.Equal("Completed",job.State);Assert.NotEmpty(job.Results);Assert.All(job.Results,item=>Assert.Equal("Completed",item.Status));
            Assert.Empty(test.Manager.Mounts);Assert.Empty(test.Repository.Entries(test.Mount.Id));
            Assert.Contains(test.Repository.LoadOperations(),item=>item.Id==job.Id);
            Assert.True(Directory.Exists(test.Local));Assert.Empty(Directory.EnumerateFileSystemEntries(test.Local));
            Assert.Equal(test.Mount.RootIdentity,MountPathGuard.FileIdentity(test.Local));Assert.Null(MountPathGuard.ReadRoot(test.Local));
            Assert.Equal("external sentinel",await File.ReadAllTextAsync(sentinel));
        }
        finally { Directory.Move(backup,content); }
        using(var source=new MockFileSource(test.Source))
        {
            var entry=await source.GetMetadataAsync("范围读取.bin");Assert.Equal(original,entry);
            var bytes=new byte[37];Assert.Equal(37,await source.ReadRangeAsync(entry,entry.Size-37,bytes));
            Assert.Equal(Enumerable.Range((int)entry.Size-37,37).Select(index=>(byte)(index*31+17)),bytes);
        }
    }
    [Fact(Timeout=120000)]
    public async Task LockedFileRetainsClearModeAndRestartDoesNotReplayDeletion()
    {
        await using var test=await Fixture.CreateAsync();
        var path=Path.Combine(test.Local,"占用.txt");await File.WriteAllTextAsync(path,"locked");
        string first;
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            first=test.Manager.Queue(test.Mount.Id,"clearAndRemove");var job=await Terminal(test.Manager,first);
            Assert.Equal("PartialFailure",job.State);Assert.Contains(job.Results,item=>item.Path==path&&item.Status=="Failed");
            Assert.True(File.Exists(path));Assert.False(test.Manager.Cancel(first));
            var pending=Assert.Single(test.Repository.LoadDefinitions());Assert.True(pending.RemovalPending);Assert.Equal(MountRemovalMode.ClearContents,pending.RemovalMode);
            Assert.Throws<IOException>(()=>test.Manager.Queue(test.Mount.Id,"remove"));
        }
        await test.RestartAsync();Assert.True(File.Exists(path));Assert.Equal("移除未完成",Assert.Single(test.Manager.Mounts).Status);
        Assert.Equal("PartialFailure",test.Manager.GetOperation(first)!.State);
        Assert.Equal("Completed",(await Terminal(test.Manager,test.Manager.Queue(test.Mount.Id,"clearAndRemove"))).State);
        Assert.Empty(Directory.EnumerateFileSystemEntries(test.Local));Assert.Empty(test.Repository.LoadDefinitions());
    }
    [Fact(Timeout=120000)]
    public async Task CancelAfterFirstDeletePersistsIntentAndCanResume()
    {
        await using var test=await Fixture.CreateAsync();
        for(var index=0;index<12;index++)await File.WriteAllTextAsync(Path.Combine(test.Local,$"新增-{index}.txt"),"local");
        void CancelAfterProgress()
        {
            foreach(var job in test.Manager.Operations.Where(item=>item.Operation=="clearAndRemove"&&item.State=="Running"&&item.Completed>0))test.Manager.Cancel(job.Id);
        }
        test.Manager.Changed+=CancelAfterProgress;
        MountOperationSnapshot canceled;
        try { canceled=await Terminal(test.Manager,test.Manager.Queue(test.Mount.Id,"clearAndRemove")); }
        finally { test.Manager.Changed-=CancelAfterProgress; }
        Assert.Equal("Canceled",canceled.State);Assert.True(canceled.Completed>0);Assert.NotEmpty(Directory.EnumerateFileSystemEntries(test.Local));
        await test.RestartAsync();Assert.True(Assert.Single(test.Repository.LoadDefinitions()).RemovalPending);
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(test.Local));
        Assert.Equal("Completed",(await Terminal(test.Manager,test.Manager.Queue(test.Mount.Id,"clearAndRemove"))).State);
        Assert.Empty(Directory.EnumerateFileSystemEntries(test.Local));
    }
    [Fact(Timeout=120000)]
    public async Task ChangedRootIdentityRejectsClearWithoutDeletingAnything()
    {
        await using var test=await Fixture.CreateAsync();
        var before=Directory.EnumerateFileSystemEntries(test.Local).Order().ToArray();
        await test.Manager.DisconnectAsync(test.Mount.Id);
        test.Repository.SaveDefinition(test.Mount with{RootIdentity="wrong identity"});
        try
        {
            await test.RestartAsync();
            var job=await Terminal(test.Manager,test.Manager.Queue(test.Mount.Id,"clearAndRemove"));
            Assert.Equal("Failed",job.State);Assert.Empty(job.Results);
            Assert.Equal(before,Directory.EnumerateFileSystemEntries(test.Local).Order().ToArray());
        }
        finally { test.Repository.SaveDefinition(test.Mount);await test.RestartAsync(); }
    }
    [Fact(Timeout=120000)]
    public async Task ResumeAfterUnregistrationKeepsEmptyRootAndHistory()
    {
        await using var test=await Fixture.CreateAsync();
        void CancelAfterAllItems()
        {
            foreach(var job in test.Manager.Operations.Where(item=>item.Operation=="clearAndRemove"&&item.State=="Running"&&item.Total>0&&item.Completed==item.Total))test.Manager.Cancel(job.Id);
        }
        test.Manager.Changed+=CancelAfterAllItems;
        try { Assert.Equal("Canceled",(await Terminal(test.Manager,test.Manager.Queue(test.Mount.Id,"clearAndRemove"))).State); }
        finally { test.Manager.Changed-=CancelAfterAllItems; }
        Assert.Empty(Directory.EnumerateFileSystemEntries(test.Local));await test.Manager.DisconnectAsync(test.Mount.Id);
        // 模拟内容已清空、注销成功之后退出，注册表状态仍需在下次显式操作时收尾。
        var pending=Assert.Single(test.Repository.LoadDefinitions()) with{RemovalStage=MountRemovalStage.Unregistering};
        test.Repository.SaveDefinition(pending);CloudRootRegistration.Unregister(pending);
        await test.RestartAsync();Assert.Single(test.Manager.Mounts);
        Assert.Equal("Completed",(await Terminal(test.Manager,test.Manager.Queue(test.Mount.Id,"clearAndRemove"))).State);
        Assert.Empty(test.Manager.Mounts);Assert.True(Directory.Exists(test.Local));Assert.Empty(Directory.EnumerateFileSystemEntries(test.Local));
    }
    private static async Task<MountOperationSnapshot> Terminal(MountManager manager,string id)
    {
        var deadline=DateTime.UtcNow.AddSeconds(40);
        while(DateTime.UtcNow<deadline)
        {
            var job=manager.GetOperation(id)!;if(job.State is not("Queued" or "Running"))return job;
            await Task.Delay(20);
        }
        manager.Cancel(id);throw new TimeoutException("清空任务未在期限内结束。");
    }
    /// <summary>建立无需管理员权限的 NTFS 目录联接；数据与目标均由本测试创建。</summary>
    private static void CreateJunction(string link,string target)
    {
        Directory.CreateDirectory(link);
        using var handle=CloudFilesNative.CreateFile(link,0x40000000,0,0,3,0x02200000,0);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var substitute=@"\??\"+Path.GetFullPath(target);var print=Path.GetFullPath(target);
        var names=Encoding.Unicode.GetBytes(substitute+'\0'+print+'\0');var bytes=new byte[16+names.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,0xa0000003);BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4),(ushort)(8+names.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10),(ushort)(substitute.Length*2));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12),(ushort)((substitute.Length+1)*2));BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14),(ushort)(print.Length*2));
        names.CopyTo(bytes,16);
        if(!DeviceIoControl(handle,0x000900a4,bytes,(uint)bytes.Length,0,0,out _,0))throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file,uint code,byte[] input,uint inputSize,nint output,uint outputSize,out uint returned,nint overlapped);
    /// <summary>隔离根、索引与注册表；失败时先尝试自有挂载移除，无法恢复便保留现场。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        internal string Area { get; }=Path.Combine(Path.GetTempPath(),"HybridMount.ClearTests",Guid.NewGuid().ToString("N"));
        internal string Local=>Path.Combine(Area,"view");
        private string RegistryPath { get; }=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        internal MountRepository Repository { get; }
        internal LoggerService Logger { get; }
        internal MountManager Manager { get; private set; }
        internal MountDefinition Mount { get; private set; }=null!;
        internal string Source=>Path.Combine(Repository.DataDirectory,"MockSources",Mount.Id.ToString("N"));
        private Fixture()
        {
            Directory.CreateDirectory(Local);Repository=new MountRepository(Path.Combine(Area,"data"),RegistryPath);
            Logger=new LoggerService(Path.Combine(Area,"log.db"));Manager=new MountManager(Repository,Logger);
        }
        internal static async Task<Fixture> CreateAsync()
        {
            var test=new Fixture();
            try { test.Mount=await test.Manager.AddAsync("清空测试",test.Local,true);return test; }
            catch { await test.DisposeAsync();throw; }
        }
        internal async Task RestartAsync()
        { await Manager.DisposeAsync();Manager=new MountManager(Repository,Logger);await Manager.StartAsync(); }
        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            if(Repository.LoadDefinitions().Count>0)
            {
                await using var recovery=new MountManager(Repository,Logger);
                foreach(var definition in Repository.LoadDefinitions())
                {
                    if(definition.LocalPath!=Local || definition.RootIdentity!=MountPathGuard.FileIdentity(Local))continue;
                    try
                    {
                        var operation=definition.RemovalPending&&definition.RemovalMode==MountRemovalMode.PreserveFiles?"remove":"clearAndRemove";
                        await Terminal(recovery,recovery.Queue(definition.Id,operation));
                    }
                    catch(Exception error) { System.Diagnostics.Trace.WriteLine("保留清空测试现场："+error.Message); }
                }
            }
            await Logger.DisposeAsync();
            // 核对实际绝对范围和残余注册后才删除本测试目录；不递归删除尚未解除的云文件。
            var intendedBase=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"HybridMount.ClearTests"));
            if(MountPathGuard.IsUnder(intendedBase,Path.GetFullPath(Area))&&Area!=intendedBase&&Repository.LoadDefinitions().Count==0&&MountPathGuard.ReadRoot(Local) is null&&!MountPathGuard.ContainsCloudPlaceholders(Local))
            { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(RegistryPath,false);Directory.Delete(Area,true); }
        }
    }
}
