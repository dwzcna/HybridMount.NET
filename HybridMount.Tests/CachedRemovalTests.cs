using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>真实隔离CFAPI根的离线缓存保留，不操作用户挂载或Explorer进程。</summary>
public sealed class CachedRemovalTests
{
    [Fact(Timeout=120000)]
    public async Task KeepsCompleteAndOrdinaryFilesDropsCleanPartialAndNeverReadsOfflineSource()
    {
        await using var test=await ClearRemovalTests.Fixture.CreateAsync();
        var complete=Path.Combine(test.Local,"说明.txt");
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Mount.Id,"keepOnDevice",new[]{complete}))).State);
        Assert.Equal(1u,CloudFilesNative.Inspect(complete)!.Value.Info.PinState);
        var bytes=await File.ReadAllBytesAsync(complete);
        var partial=Path.Combine(test.Local,"范围读取.bin");Assert.False(CloudFilesNative.HasFullContent(partial,new FileInfo(partial).Length));
        await test.Manager.DisconnectAsync(test.Mount.Id);
        var ordinary=Path.Combine(test.Local,"local-only.txt");await File.WriteAllTextAsync(ordinary,"retain unuploaded content");
        var outside=Directory.CreateDirectory(Path.Combine(test.Area,"outside")).FullName;
        var sentinel=Path.Combine(outside,"sentinel.txt");await File.WriteAllTextAsync(sentinel,"outside");
        var link=Path.Combine(test.Local,"link");ClearRemovalTests.CreateJunction(link,outside);
        var source=Path.Combine(test.Source,"content");var offline=source+"-offline";Directory.Move(source,offline);
        try
        {
            var job=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Mount.Id,"removeCached"));
            Assert.True(job.State=="Completed",job.Error+"\n"+string.Join("\n",job.Results.Where(item=>item.Status=="Failed").Select(item=>item.Path+": "+item.Message)));
            Assert.Empty(test.Repository.LoadDefinitions());Assert.Null(CloudFilesNative.RootIdentity(test.Local,true));
            Assert.False(File.Exists(partial));Assert.Null(CloudFilesNative.Inspect(complete));
            Assert.Equal(bytes,await File.ReadAllBytesAsync(complete));Assert.Equal("retain unuploaded content",await File.ReadAllTextAsync(ordinary));
            Assert.True(Directory.Exists(link));Assert.Equal("outside",await File.ReadAllTextAsync(sentinel));
            Assert.False(MountPathGuard.ContainsCloudPlaceholders(test.Local));
        }
        finally{Directory.Move(offline,source);ClearRemovalTests.DeleteJunction(link);}
    }
    [Fact(Timeout=120000)]
    public async Task LockedFilePersistsModeAndRestartRequiresExplicitRetry()
    {
        await using var test=await ClearRemovalTests.Fixture.CreateAsync();
        var path=Path.Combine(test.Local,"说明.txt");var kept=await File.ReadAllBytesAsync(path);
        await test.Manager.DisconnectAsync(test.Mount.Id);
        string task;
        using(var held=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
        {
            task=test.Manager.Queue(test.Mount.Id,"removeCached");var result=await WebDavTests.Finished(test.Manager,task);
            Assert.Equal("PartialFailure",result.State);Assert.Contains(result.Results,item=>item.Path==path&&item.Status=="Failed");
            Assert.Equal(MountRemovalMode.PreserveCachedFiles,Assert.Single(test.Repository.LoadDefinitions()).RemovalMode);
            Assert.Throws<IOException>(()=>test.Manager.Queue(test.Mount.Id,"clearAndRemove"));
        }
        await test.RestartAsync();Assert.True(File.Exists(path));
        Assert.Equal("PartialFailure",test.Manager.GetOperation(task)!.State);
        Assert.True(test.Manager.RetryUpload(task));
        var retry=test.Manager.Operations.Single(item=>item.Operation=="removeCached"&&item.Id!=task);
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,retry.Id)).State);
        Assert.Equal(kept,await File.ReadAllBytesAsync(path));
    }
    [Fact(Timeout=120000)]
    public async Task StaleStampWrongIdentityAndDiscardingCompleteContentAreRejectedByNativeGuard()
    {
        await using var test=await ClearRemovalTests.Fixture.CreateAsync();
        var entry=test.Repository.EntriesAtPaths(test.Mount.Id,new[]{"说明.txt"}).Single();
        var path=Path.Combine(test.Local,entry.LocalPath);var kept=await File.ReadAllBytesAsync(path);
        await test.Manager.DisconnectAsync(test.Mount.Id);
        await using(var session=new CloudMountSession(test.Mount,null,test.Repository,test.Logger,blockHydration:true))
        {
            session.Connect();await session.BeginClearAsync();var stamp=CloudFilesNative.ReadNative(path,ranges:true).Stamp;
            Assert.Throws<System.Runtime.InteropServices.COMException>(()=>session.Runtime.Invoke("preserveCachedLocal",session.EntryArgs(entry,new{discard=true,proofStamp=stamp.Key})));
            Assert.Throws<System.Runtime.InteropServices.COMException>(()=>session.Runtime.Invoke("preserveCachedLocal",session.EntryArgs(entry with{Id="foreign"},new{discard=false,proofStamp=stamp.Key})));
            File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddHours(-1));
            Assert.Throws<System.Runtime.InteropServices.COMException>(()=>session.Runtime.Invoke("preserveCachedLocal",session.EntryArgs(entry,new{discard=false,proofStamp=stamp.Key})));
            Assert.NotNull(CloudFilesNative.Inspect(path));Assert.Equal(kept,await File.ReadAllBytesAsync(path));
        }
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Mount.Id,"removeCached"))).State);
    }
    [Fact(Timeout=120000)]
    public async Task ProtectedPartialIsKeptUntilCompleteLocalContentIsSupplied()
    {
        await using var test=await ClearRemovalTests.Fixture.CreateAsync();await test.Manager.DisconnectAsync(test.Mount.Id);
        var entry=test.Repository.EntriesAtPaths(test.Mount.Id,new[]{"范围读取.bin"}).Single();
        test.Repository.SaveEntry(test.Mount.Id,entry with{Protected=true});
        var result=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Mount.Id,"removeCached"));
        Assert.Equal("PartialFailure",result.State);Assert.True(File.Exists(Path.Combine(test.Local,entry.LocalPath)));
        Assert.Single(test.Repository.LoadDefinitions());Assert.True(test.Repository.EntryById(test.Mount.Id,entry.Id)!.Protected);
        // 保护标志不能通过普通SaveEntry清除；用户提供完整本地正文后可以保留该真实正文，而不是删除部分修改。
        var recovered=System.Text.Encoding.UTF8.GetBytes("complete local recovery");
        await File.WriteAllBytesAsync(Path.Combine(test.Local,entry.LocalPath),recovered);
        var retry=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Mount.Id,"removeCached"));
        Assert.True(retry.State=="Completed",retry.Error+"\n"+string.Join("\n",retry.Results.Where(item=>item.Status=="Failed").Select(item=>item.Path+": "+item.Message)));
        Assert.Equal(recovered,await File.ReadAllBytesAsync(Path.Combine(test.Local,entry.LocalPath)));
    }
    [Fact(Timeout=120000)]
    public async Task CancelAfterFirstItemRetainsModeAndRequiresExplicitRestart()
    {
        await using var test=await ClearRemovalTests.Fixture.CreateAsync();await test.Manager.DisconnectAsync(test.Mount.Id);
        var first=0;test.Manager.RemovalItemForTests=(id,_)=>{if(Interlocked.Increment(ref first)==1)test.Manager.Cancel(id);};
        var result=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Mount.Id,"removeCached"));
        test.Manager.RemovalItemForTests=null;Assert.Equal("Canceled",result.State);
        Assert.Equal(MountRemovalMode.PreserveCachedFiles,Assert.Single(test.Repository.LoadDefinitions()).RemovalMode);
        await test.RestartAsync();Assert.Single(test.Repository.LoadDefinitions());
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Mount.Id,"removeCached"))).State);
    }
    [Fact(Timeout=120000)]
    public async Task UnknownUploadReceiptPreventsForgettingRootOrDeletingAnyFile()
    {
        await using var test=await ClearRemovalTests.Fixture.CreateAsync();await test.Manager.DisconnectAsync(test.Mount.Id);
        var partial=Path.Combine(test.Local,"范围读取.bin");
        test.Repository.SaveUpload(new("unknown",test.Mount.Id,"范围读取.bin","范围读取.bin",false,"stamp",1,"OutcomeUnknown",DateTimeOffset.UtcNow));
        var result=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Mount.Id,"removeCached"));
        Assert.Equal("Failed",result.State);Assert.True(File.Exists(partial));Assert.NotNull(test.Repository.Upload("unknown"));
        Assert.False(Assert.Single(test.Repository.LoadDefinitions()).RemovalPending);
        test.Repository.SaveUpload(test.Repository.Upload("unknown")! with{State="Completed"});
        Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Mount.Id,"removeCached"))).State);
    }
}
