using System.Security.Cryptography;
using System.Text;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>通过真实 NTFS/CFAPI 与本地 HTTP 服务验证文件系统触发的完整上传流程。</summary>
public sealed class UploadFlowTests
{
    [Fact(Timeout=120000)]
    public async Task CreateEditReplaceRenameAndDeletePreserveRemote()
    {
        await using var test=await Fixture.Create();var path=Path.Combine(test.Local,"中文目录","零字节.txt");Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllBytesAsync(path,[]);
        await test.Wait("中文目录");await test.Wait("中文目录/零字节.txt");
        Assert.Empty(test.Dav.Objects["中文目录/零字节.txt"].Bytes);Assert.Equal(1u,CloudFilesNative.Inspect(path)!.Value.Info.InSyncState);
        await File.WriteAllTextAsync(path,"first");await test.Wait("中文目录/零字节.txt",bytes:Encoding.UTF8.GetBytes("first"));
        await File.WriteAllTextAsync(path,"other");await test.Wait("中文目录/零字节.txt",bytes:Encoding.UTF8.GetBytes("other"));
        var evicted=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"freeSpace",[path]));Assert.Contains(evicted.Results,x=>x.Status=="Completed");
        Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));Assert.Equal("other",await File.ReadAllTextAsync(path));
        var temporary=Path.Combine(Path.GetDirectoryName(path)!,"temporary.tmp");await File.WriteAllTextAsync(temporary,"editor replaced");File.Move(temporary,path,true);
        await test.Wait("中文目录/零字节.txt",bytes:Encoding.UTF8.GetBytes("editor replaced"));
        var renamed=Path.Combine(Path.GetDirectoryName(path)!,"改名.txt");File.Move(path,renamed);
        await test.Wait("中文目录/改名.txt",bytes:Encoding.UTF8.GetBytes("editor replaced"));
        Assert.True(test.Dav.Objects.ContainsKey("中文目录/零字节.txt"));File.Delete(renamed);await Task.Delay(2500);Assert.True(test.Dav.Objects.ContainsKey("中文目录/改名.txt"));
    }
    [Fact(Timeout=120000)]
    public async Task LegacyDisabledMountUploadsOldIgnoredButPreservesCanceledGeneration()
    {
        await using var test=await Fixture.Create(false);
        await File.WriteAllTextAsync(Path.Combine(test.Local,"纳入.txt"),"old ignored");await File.WriteAllTextAsync(Path.Combine(test.Local,"取消.txt"),"old canceled");
        await test.Manager.ScanUploadChangesAsync(test.Id);
        foreach(var item in test.Repository.Uploads(test.Id))test.Repository.SaveUpload(item with{State=item.LocalPath=="纳入.txt"?"Ignored":"Canceled",CancelRequested=item.LocalPath=="取消.txt"});
        await test.Manager.DisposeAsync();var definition=test.Repository.LoadDefinitions().Single() with{UploadInitialReviewCompleted=false,AutoUploadEnabled=false,ContinuousUploadMigrated=false};test.Repository.SaveDefinition(definition);
        await File.WriteAllTextAsync(Path.Combine(test.Local,"旧新增.txt"),"new while offline");
        test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await test.Wait("纳入.txt");await test.Wait("旧新增.txt");
        Assert.False(test.Dav.Objects.ContainsKey("取消.txt"));Assert.Equal("Canceled",test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="取消.txt").State);
        Assert.True(test.Repository.LoadDefinitions().Single().ContinuousUploadMigrated);Assert.True(test.Repository.LoadDefinitions().Single().AutoUploadEnabled);
        await test.Manager.DisconnectAsync(test.Id);await test.Manager.ConnectAsync(test.Id);Assert.False(test.Dav.Objects.ContainsKey("取消.txt"));
    }
    [Fact(Timeout=120000)]
    public async Task RemoteConflictCopyAndCacheProtection()
    {
        await using var test=await Fixture.Create();var path=Path.Combine(test.Local,"文件.bin");await Task.Run(()=>File.ReadAllBytes(path));
        test.Dav.Put("文件.bin",[7,8,9]);await File.WriteAllTextAsync(path,"keep local");var conflict=await test.Wait("文件.bin",state:"Conflict");
        Assert.Equal(new byte[]{7,8,9},test.Dav.Objects["文件.bin"].Bytes);Assert.True(test.Repository.Entries(test.Id).Single(x=>x.LocalPath=="文件.bin").Protected);
        var release=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"freeSpace",[path]));Assert.Contains(release.Results,x=>x.Status=="Skipped");
        var copyId=test.Manager.UploadAsCopy(conflict.Id,"本地副本.txt");var deadline=DateTime.UtcNow.AddSeconds(25);
        while(test.Manager.GetOperation(copyId)?.State!="Completed"){if(DateTime.UtcNow>deadline)throw new TimeoutException(test.Manager.GetOperation(copyId)?.Error);await Task.Delay(100);}
        Assert.Equal("keep local",Encoding.UTF8.GetString(test.Dav.Objects["本地副本.txt"].Bytes));Assert.Equal("keep local",await File.ReadAllTextAsync(path));
    }
    [Fact(Timeout=120000)]
    public async Task EditDuringUploadQueuesNextGenerationAndCancelDoesNotReplay()
    {
        await using var test=await Fixture.Create();var handler=test.Dav.Server.Handler;var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var delay=true;
        test.Dav.Server.Handler=async request=>{if(delay&&request.Method=="PUT"&&request.Target.Contains("concurrent.txt",StringComparison.Ordinal)){delay=false;entered.TrySetResult();await release.Task;}return await handler(request);};
        var path=Path.Combine(test.Local,"concurrent.txt");await File.WriteAllTextAsync(path,"generation one");await entered.Task.WaitAsync(TimeSpan.FromSeconds(25));
        await File.WriteAllTextAsync(path,"generation two");release.TrySetResult();await test.Wait("concurrent.txt",bytes:Encoding.UTF8.GetBytes("generation two"));
        await test.Manager.SetSchedulingForTestsAsync(test.Id,false);await File.WriteAllTextAsync(path,"canceled content");await test.Manager.ScanUploadChangesAsync(test.Id);
        var item=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="concurrent.txt");Assert.True(test.Manager.Cancel(item.Id));await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await Task.Delay(2500);
        Assert.Equal("generation two",Encoding.UTF8.GetString(test.Dav.Objects["concurrent.txt"].Bytes));
        Assert.True(test.Manager.RetryUpload(item.Id));await test.Wait("concurrent.txt",bytes:Encoding.UTF8.GetBytes("canceled content"));
    }
    [Fact(Timeout=120000)]
    public async Task RestartReconcilesSentSnapshotWithoutDuplicatePut()
    {
        await using var test=await Fixture.Create(false);var path=Path.Combine(test.Local,"recovery.txt");var content=Encoding.UTF8.GetBytes("response lost");await File.WriteAllBytesAsync(path,content);
        await test.Manager.DisposeAsync();test.Dav.Put("recovery.txt",content);
        var stamp=LocalFileState.Read(path);var directory=Path.Combine(test.Repository.DataDirectory,"UploadStaging",test.Id.ToString("N"));Directory.CreateDirectory(directory);var snapshot=Path.Combine(directory,"recovery.snapshot");await File.WriteAllBytesAsync(snapshot,content);
        var record=new LocalUpload(Guid.NewGuid().ToString("N"),test.Id,"recovery.txt","recovery.txt",false,stamp.Key,1,"Sending",DateTimeOffset.UtcNow.AddSeconds(-5),SnapshotPath:snapshot,SnapshotSha256:Convert.ToHexString(SHA256.HashData(content)),SnapshotStamp:stamp.Key,SnapshotIdentity:stamp.Identity,SnapshotGeneration:1,Total:content.Length);
        test.Repository.SaveUpload(record);test.Repository.SaveOperation(new(record.Id,"upload","Sending",0,1,[],null));var before=test.Dav.Server.Requests.Count(x=>x.Method=="PUT"&&x.Target.EndsWith("recovery.txt",StringComparison.Ordinal));
        test.Manager=new(test.Repository,test.Logger);await test.Manager.ConnectAsync(test.Id);await test.Wait("recovery.txt");
        // Completed 先提交到数据库，快照回收随后完成；等待工作器收尾后再核对清理，避免把提交与删除误当作原子操作。
        await test.Manager.DisconnectAsync(test.Id);
        Assert.Equal(before,test.Dav.Server.Requests.Count(x=>x.Method=="PUT"&&x.Target.EndsWith("recovery.txt",StringComparison.Ordinal)));Assert.False(File.Exists(snapshot));
    }
    [Fact(Timeout=120000)]
    public async Task ResponseLostIsReconciledWithoutRepeatingMutation()
    {
        await using var test=await Fixture.Create();var handler=test.Dav.Server.Handler;var dropped=false;
        test.Dav.Server.Handler=async request=>{var response=await handler(request);if(!dropped&&request.Method=="PUT"&&request.Target.EndsWith("lost.txt",StringComparison.Ordinal)){dropped=true;throw new IOException("模拟服务写入成功后连接断开");}return response;};
        var path=Path.Combine(test.Local,"lost.txt");await File.WriteAllTextAsync(path,"saved once");var unknown=await test.Wait("lost.txt",state:"OutcomeUnknown");
        Assert.Equal("saved once",Encoding.UTF8.GetString(test.Dav.Objects["lost.txt"].Bytes));Assert.True(File.Exists(unknown.SnapshotPath));Assert.True(test.Manager.RetryUpload(unknown.Id));await test.Wait("lost.txt");
        Assert.Single(test.Dav.Server.Requests,x=>x.Method=="PUT"&&x.Target.EndsWith("lost.txt",StringComparison.Ordinal));
    }
    [Fact(Timeout=120000)]
    public async Task WriterAndExternalJunctionAreProtected()
    {
        await using var test=await Fixture.Create();var outside=Path.Combine(test.Dav.Area,"outside");Directory.CreateDirectory(outside);await File.WriteAllTextAsync(Path.Combine(outside,"private.txt"),"outside");
        ClearRemovalTests.CreateJunction(Path.Combine(test.Local,"link"),outside);var path=Path.Combine(test.Local,"writer.txt");
        await using(var writer=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read)){await writer.WriteAsync("unfinished"u8.ToArray());await writer.FlushAsync();await test.Wait("writer.txt",state:"Waiting");Assert.False(test.Dav.Objects.ContainsKey("writer.txt"));}
        await test.Wait("writer.txt");Assert.DoesNotContain(test.Dav.Objects.Keys,x=>x.StartsWith("link",StringComparison.Ordinal));Assert.Equal("outside",await File.ReadAllTextAsync(Path.Combine(outside,"private.txt")));
    }
    [Fact(Timeout=120000)]
    public async Task OfflineLocalCreateDoesNotAdoptFreshRemoteVersion()
    {
        await using var test=await Fixture.Create(false);await test.Manager.DisconnectAsync(test.Id);var path=Path.Combine(test.Local,"offline.txt");await File.WriteAllTextAsync(path,"local content");test.Dav.Put("offline.txt",Encoding.UTF8.GetBytes("another client"));
        await test.Manager.ConnectAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Wait("offline.txt",state:"Conflict");
        Assert.Equal("another client",Encoding.UTF8.GetString(test.Dav.Objects["offline.txt"].Bytes));Assert.Equal("local content",await File.ReadAllTextAsync(path));
    }
    [Fact(Timeout=120000)]
    public async Task CaseOnlyRenameUsesNewRemotePathAndKeepsOldContent()
    {
        await using var test=await Fixture.Create();var old=Path.Combine(test.Local,"Case.txt");await File.WriteAllTextAsync(old,"old path content");await test.Wait("Case.txt");
        var intermediate=Path.Combine(test.Local,"case-rename.tmp");File.Move(old,intermediate);File.Move(intermediate,Path.Combine(test.Local,"case.txt"));
        await test.Wait("case.txt");Assert.Equal("old path content",Encoding.UTF8.GetString(test.Dav.Objects["Case.txt"].Bytes));Assert.Equal("old path content",Encoding.UTF8.GetString(test.Dav.Objects["case.txt"].Bytes));
    }
    [Fact(Timeout=120000)]
    public async Task ConcurrentDisconnectStopsWorkerOnceAndReconnectRestoresPending()
    {
        await using var test=await Fixture.Create(false);await File.WriteAllTextAsync(Path.Combine(test.Local,"pending.txt"),"pending work");await test.Manager.ScanUploadChangesAsync(test.Id);
        await Task.WhenAll(test.Manager.DisconnectAsync(test.Id),test.Manager.DisconnectAsync(test.Id));await test.Manager.ConnectAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Wait("pending.txt");
    }
    [Fact(Timeout=120000)]
    public async Task CorruptPreparedSnapshotNeverUploadsAndRetryRecreatesIt()
    {
        await using var test=await Fixture.Create(false);var path=Path.Combine(test.Local,"snapshot.txt");var bytes=Encoding.UTF8.GetBytes("original local bytes");await File.WriteAllBytesAsync(path,bytes);await test.Manager.ScanUploadChangesAsync(test.Id);
        var item=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="snapshot.txt");var stamp=LocalFileState.Read(path);var directory=Path.Combine(test.Repository.DataDirectory,"UploadStaging",test.Id.ToString("N"));Directory.CreateDirectory(directory);var snapshot=Path.Combine(directory,"changed.snapshot");await File.WriteAllTextAsync(snapshot,"tampered data");
        test.Repository.SaveUpload(item with{State="Prepared",SnapshotPath=snapshot,SnapshotIdentity=stamp.Identity,SnapshotStamp=stamp.Key,SnapshotSha256=Convert.ToHexString(SHA256.HashData(bytes)),SnapshotGeneration=item.Generation,Total=bytes.Length});
        await test.Manager.SetSchedulingForTestsAsync(test.Id,true);await test.Wait("snapshot.txt",state:"Conflict");Assert.False(test.Dav.Objects.ContainsKey("snapshot.txt"));
        Assert.True(test.Manager.RetryUpload(item.Id));await test.Wait("snapshot.txt",bytes:bytes);Assert.Equal(bytes,test.Dav.Objects["snapshot.txt"].Bytes);
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public DavFixture Dav=new();public MountRepository Repository=null!;public LoggerService Logger=null!;public MountManager Manager=null!;public string Local="",Registry="";public Guid Id;
        public static async Task<Fixture> Create(bool auto=true)
        {
            var test=new Fixture();test.Local=Path.Combine(test.Dav.Area,"view");Directory.CreateDirectory(test.Local);test.Registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
            test.Repository=new(Path.Combine(test.Dav.Area,"data"),test.Registry);test.Logger=new(Path.Combine(test.Dav.Area,"logs.db"));test.Manager=new(test.Repository,test.Logger);
            var mount=await test.Manager.AddAsync("自动上传回归",test.Local,true,source:test.Dav.Options,credentials:new("user","app-password"),autoUpload:auto);test.Id=mount.Id;if(!auto)await test.Manager.SetSchedulingForTestsAsync(test.Id,false);return test;
        }
        public async Task<LocalUpload> Wait(string path,string state="Completed",byte[]? bytes=null)
        {
            var deadline=DateTime.UtcNow.AddSeconds(30);LocalUpload? record=null;
            while(DateTime.UtcNow<deadline)
            {
                record=Repository.Uploads(Id).FirstOrDefault(x=>!x.IsCopy&&x.LocalPath==path);
                if(record?.State==state&&(bytes is null||Dav.Objects.TryGetValue(path,out var item)&&item.Bytes.SequenceEqual(bytes)))return record;
                if(record?.State is "Conflict" or "Failed")throw new IOException(path+": "+record.State+" "+record.Error);
                await Task.Delay(80);
            }
            throw new TimeoutException(path+": "+System.Text.Json.JsonSerializer.Serialize(record));
        }
        public async ValueTask DisposeAsync()
        {
            if(Manager is not null){if(Manager.Mounts.Any(x=>x.Definition.Id==Id)){try{await WebDavTests.Finished(Manager,Manager.Queue(Id,"clearAndRemove"));}finally{await Manager.DisposeAsync();}}else await Manager.DisposeAsync();}
            if(Logger is not null)await Logger.DisposeAsync();if(Registry.Length!=0)Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Registry,false);await Dav.DisposeAsync();
        }
    }
}
