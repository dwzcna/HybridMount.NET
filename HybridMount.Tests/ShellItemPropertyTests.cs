using HybridMount.Services.Mounting;
using Windows.Storage;
using Windows.Storage.Provider;
namespace HybridMount.Tests;

/// <summary>在隔离真实NTFS／CFAPI根核验生产属性链，无正文水合、额外网络、代次变化或同步身份改变。</summary>
public sealed class ShellItemPropertyTests
{
    [Theory(Timeout=60000)]
    [InlineData("ordinary")][InlineData("placeholder")][InlineData("pinned")][InlineData("directory")][InlineData("dirty")]
    public async Task ProductionPublisherPreservesStateAndClearDoesNotEnqueue(string kind)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);
        var path=Path.Combine(test.Local,kind is "ordinary" or "directory"?"failed-local":"文件.bin");
        if(kind=="directory"){Directory.CreateDirectory(path);await test.Manager.ScanUploadChangesAsync(test.Id);}
        if(kind=="dirty"){await File.ReadAllBytesAsync(path);await File.WriteAllTextAsync(path,"changed");await test.Manager.ScanUploadChangesAsync(test.Id);}
        if(kind=="ordinary"){await File.WriteAllTextAsync(path,"local failed bytes");await test.Manager.ScanUploadChangesAsync(test.Id);}
        if(kind=="pinned")Assert.Equal("Completed",(await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]))).State);
        if(kind=="placeholder")
        {
            var factory=typeof(CloudRootRegistration).GetMethod("CreateDescriptorAsync",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
            var legacy=await (Task<StorageProviderSyncRootInfo>)factory.Invoke(null,[test.Repository.LoadDefinitions().Single()])!;
            Assert.Empty(legacy.StorageProviderItemPropertyDefinitions);StorageProviderSyncRootManager.Register(legacy);
        }
        var root=MountPathGuard.ReadRoot(test.Local);var rootIdentity=MountPathGuard.FileIdentity(test.Local);
        var native=Native(path);var before=LocalFileState.Read(path);var existing=test.Repository.Uploads(test.Id).FirstOrDefault(x=>x.LocalPath==Path.GetFileName(path));
        if(existing is null){existing=new(Guid.NewGuid().ToString("N"),test.Id,Path.GetFileName(path),Path.GetFileName(path),false,before.Key,1,"Failed",DateTimeOffset.UtcNow,Error:"precise failure");test.Repository.SaveUpload(existing);}
        else test.Repository.UpdateUpload(existing.Id,x=>x with{State="Failed",Error="precise failure"});
        var original=test.Repository.Upload(existing.Id)!;var network=test.Dav.Server.Requests.Count;
        await test.Manager.PublishUploadIssuesAsync();var published=test.Repository.Upload(existing.Id)!;
        Assert.True(published.Attention!.ShellState=="Published",published.Attention.ShellError);Assert.Equal(original.Generation,published.Generation);
        Assert.Equal(root,MountPathGuard.ReadRoot(test.Local));Assert.Equal(rootIdentity,MountPathGuard.FileIdentity(test.Local));
        Assert.Equal(network,test.Dav.Server.Requests.Count);Assert.Equal(before.Identity,LocalFileState.Read(path).Identity);
        var after=Native(path);Assert.Equal(native?.Info.PinState,after?.Info.PinState);Assert.Equal(native?.Info.InSyncState,after?.Info.InSyncState);Assert.Equal(native?.Info.OnDiskSize,after?.Info.OnDiskSize);
        test.Repository.UpdateUpload(existing.Id,x=>x with{State="Canceled",CancelRequested=true});await test.Manager.PublishUploadIssuesAsync();
        var cleared=test.Repository.Upload(existing.Id)!;Assert.True(cleared.Attention!.ShellState=="Cleared",cleared.Attention.ShellError);Assert.Equal(network,test.Dav.Server.Requests.Count);
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.Equal(original.Generation,test.Repository.Upload(existing.Id)!.Generation);
    }
    [Fact(Timeout=60000)]
    public async Task EditAfterPropertyHandleReleaseCannotBeAbsorbed()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"window.txt");
        await File.WriteAllTextAsync(path,"A");await test.Manager.ScanUploadChangesAsync(test.Id);var row=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="window.txt");
        test.Repository.UpdateUpload(row.Id,x=>x with{State="Failed",Error="initial"});
        test.Manager.IssuePropertyReleasedForTests=async _=>await File.WriteAllTextAsync(path,"B");
        await test.Manager.PublishUploadIssuesAsync();test.Manager.IssuePropertyReleasedForTests=null;
        var stored=test.Repository.Upload(row.Id)!;Assert.NotEqual(LocalFileState.Read(path).Key,stored.ObservedStamp);
        await test.Manager.ScanUploadChangesAsync(test.Id);Assert.True(test.Repository.Upload(row.Id)!.Generation>row.Generation);
    }
    [Theory(Timeout=60000)]
    [InlineData(false)][InlineData(true)]
    public async Task InterruptedPropertyJournalRecoversWithoutAbsorbingLaterContent(bool edited)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"interrupted.txt");await File.WriteAllTextAsync(path,"A");await test.Manager.ScanUploadChangesAsync(test.Id);
        var row=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="interrupted.txt");test.Repository.UpdateUpload(row.Id,x=>x with{State="Failed",Error="original"});
        test.Manager.IssuePropertyWrittenForTests=_=>throw new IOException("simulated interruption after metadata write");await test.Manager.PublishUploadIssuesAsync();
        Assert.Equal("Publishing",test.Repository.Upload(row.Id)!.Attention!.ShellState);Assert.NotNull(test.Repository.Upload(row.Id)!.Attention!.ShellOriginalStamp);
        await test.Manager.DisposeAsync();if(edited)await File.WriteAllTextAsync(path,"B");
        test.Manager=new(test.Repository,test.Logger);await test.Manager.SetSchedulingForTestsAsync(test.Id,false);await test.Manager.ConnectAsync(test.Id);
        await test.Manager.ScanUploadChangesAsync(test.Id);var recovered=test.Repository.Upload(row.Id)!;
        Assert.Equal(edited?row.Generation+1:row.Generation,recovered.Generation);
        if(!edited){Assert.Equal("Failed",recovered.State);await test.Manager.PublishUploadIssuesAsync();Assert.Equal("Published",test.Repository.Upload(row.Id)!.Attention!.ShellState);}
    }
    [Fact(Timeout=30000)]
    public async Task ReplacedFileIsNotMarkedAndPublishingFailureDoesNotChangeUpload()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"replaced.txt");await File.WriteAllTextAsync(path,"A");await test.Manager.ScanUploadChangesAsync(test.Id);
        var row=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="replaced.txt");test.Repository.UpdateUpload(row.Id,x=>x with{State="Failed",Error="original failure"});
        await test.Manager.DisconnectAsync(test.Id);File.Delete(path);await File.WriteAllTextAsync(path,"B");var next=LocalFileState.Read(path);var requests=test.Dav.Server.Requests.Count;
        await test.Manager.PublishUploadIssuesAsync();var after=test.Repository.Upload(row.Id)!;Assert.Equal("Failed",after.State);Assert.Equal(row.Generation,after.Generation);Assert.Equal("original failure",after.Attention!.Reason);Assert.Contains("身份已变化",after.Attention.ShellError);
        Assert.Equal(next.Key,LocalFileState.Read(path).Key);Assert.Equal(requests,test.Dav.Server.Requests.Count);
        var focus=test.Manager.LocateUploadIssues([path]);Assert.Equal(test.Id,focus.MountId);Assert.Equal("replaced.txt",Assert.Single(focus.Paths));
        Assert.Throws<IOException>(()=>test.Manager.LocateUploadIssues([test.Dav.Area]));
    }
    [Fact(Timeout=30000)]
    public async Task MetadataProtectionBlocksEditorAndRenameWithoutHydration()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"文件.bin");var requests=test.Dav.Server.Requests.Count;
        using(var handle=CloudFilesNative.CreateFile(path,0x10180,1,0,3,0x02200000,0))
        {
            Assert.False(handle.IsInvalid);Assert.Equal(0,Native(path)?.Info.OnDiskSize);
            Assert.ThrowsAny<IOException>(()=>{using var writer=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete);});
            Assert.ThrowsAny<IOException>(()=>File.Move(path,path+".other"));
        }
        Assert.Equal(0,Native(path)?.Info.OnDiskSize);Assert.Equal(requests,test.Dav.Server.Requests.Count);
    }
    // 断开期间系统可能隐藏重解析标签，直接查询CFAPI，不能把标签隐藏误判为普通文件。
    private static (CloudFilesNative.PlaceholderInfo Info,byte[] Identity)? Native(string path)
    {
        using var h=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0);var b=System.Runtime.InteropServices.Marshal.AllocHGlobal(8192);
        try
        {
            if(CloudFilesNative.CfGetPlaceholderInfo(h,1,b,8192,out _)<0)return null;
            var info=System.Runtime.InteropServices.Marshal.PtrToStructure<CloudFilesNative.PlaceholderInfo>(b);var identity=new byte[info.IdentityLength];
            System.Runtime.InteropServices.Marshal.Copy(b+60,identity,0,identity.Length);return(info,identity);
        }
        finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(b);}
    }
}
