using System.Text.Json;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>隔离回环DAV和NTFS根，核对排除标记与Shell原生状态，不访问正式挂载。</summary>
public sealed class FileExclusionShellTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory(Timeout=60000)]
    [InlineData(0u)][InlineData(1u)][InlineData(2u)][InlineData(3u)]
    public async Task DeferredMarkerRestartsAndRecoversInterruptedRestore(uint previous)
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
        var definition=test.Repository.LoadDefinitions().Single();var path=Path.Combine(test.Local,"文件.bin");
        using(var attributes=CloudFilesNative.CreateFile(path,0x180,7,0,3,0x02200000,0))CloudFilesNative.Check(CloudFilesNative.SetPinIntent(attributes,previous,0,0));
        var area=Path.Combine(test.Repository.DataDirectory,"deferred-proof");var store=new FileExclusionMarkerStore(area,test.Id);var before=CloudFilesNative.Inspect(path)!.Value;
        store.Reconcile(definition,"文件.bin",true);Assert.Equal(1u,CloudShellState.Read(path).ProviderState);Assert.Contains("原生标记等待完整内容 1",store.Describe());
        store=new(area,test.Id);Assert.False(store.Reconcile(definition,"文件.bin",true).Changed);
        // 恢复阶段先落盘再模拟失败，重建后仍按OwnedPin核对本功能拥有的值。
        if(previous is 1 or 3){store.SetPinForTests=(_,_)=>unchecked((int)0x80070005);Assert.ThrowsAny<Exception>(()=>store.Reconcile(definition,"文件.bin",false));}
        // 保持生产30秒失败退避；用过期时间模拟下次启动后的可重试窗口。
        var journal=Path.Combine(area,"ExclusionMarkers",test.Id.ToString("N")+".json");
        var marks=JsonSerializer.Deserialize<Dictionary<string,FileExclusionMarkerStore.Mark>>(await File.ReadAllTextAsync(journal))!;
        foreach(var id in marks.Keys.ToArray())marks[id]=marks[id] with{AttemptUtc=DateTimeOffset.UtcNow.AddMinutes(-1)};
        await File.WriteAllTextAsync(journal,JsonSerializer.Serialize(marks));
        store=new(area,test.Id);store.Reconcile(definition,"文件.bin",false);
        var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(previous,after.Info.PinState);Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(before.Info.OnDiskSize,after.Info.OnDiskSize);
        Assert.Empty(CloudCustomStateReader.Read(path).Ids);Assert.Equal(0,after.Info.OnDiskSize);Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method is "GET" or "PUT");
    }
    [Theory(Timeout=60000)]
    [InlineData(0,0,false)][InlineData(3,0,false)][InlineData(3,1,false)][InlineData(3,2,false)]
    [InlineData(0,0,true)][InlineData(3,0,true)][InlineData(3,1,true)][InlineData(3,2,true)]
    public async Task InitialExcludedRemoteFilesKeepContentAndNativeState(int size,int hydration,bool initialRules)
    {
        await using var test=new UploadFlowTests.Fixture();
        test.Local=Path.Combine(test.Dav.Area,"view");Directory.CreateDirectory(test.Local);test.Registry=@"Software\HybridMount.NET.Tests\"+Guid.NewGuid().ToString("N");
        test.Repository=new(Path.Combine(test.Dav.Area,"data"),test.Registry);test.Logger=new(Path.Combine(test.Dav.Area,"logs.db"));test.Manager=new(test.Repository,test.Logger);
        var bytes=Enumerable.Range(0,size).Select(x=>(byte)x).ToArray();test.Dav.Put("initial.bak",bytes);
        var mount=await test.Manager.AddAsync("排除Shell对照",test.Local,true,source:test.Dav.Options,credentials:new("user","app-password"),autoUpload:false,exclusions:initialRules?FileExclusionRules.Defaults():[]);test.Id=mount.Id;
        await test.Manager.SetSchedulingForTestsAsync(test.Id,false);await test.Manager.SetPinIntentMonitoringForTestsAsync(test.Id,false);
        var path=Path.Combine(test.Local,"initial.bak");
        if(hydration==1)Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
        if(hydration==2){var job=await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"keepOnDevice",[path]));if(initialRules)Assert.Contains(job.Results,x=>x.Status=="Skipped");}
        // 已排除的文件禁止固定；该组合应保持冷状态，不能为通过状态断言私自水合。
        var cold=hydration==0||initialRules&&hydration==2;
        await test.Manager.ScanUploadChangesAsync(test.Id);
        var before=CloudFilesNative.Inspect(path)!.Value;var identity=LocalFileState.Read(path).Identity;
        void Log(string stage)=>output.WriteLine(stage+": "+JsonSerializer.Serialize(new{Shell=CloudShellState.Read(path),Native=CloudFilesNative.Inspect(path)?.Info,Custom=CloudCustomStateReader.Read(path),Uploads=test.Repository.Uploads(test.Id),Entry=test.Repository.Entries(test.Id)},new JsonSerializerOptions{IncludeFields=true}));
        Log("before");var requests=test.Dav.Server.Requests.Count(x=>x.Method=="GET");
        await test.Manager.SetFileExclusionRulesAsync(test.Id,FileExclusionRules.Defaults());Log("marked");
        await test.Manager.ScanUploadChangesAsync(test.Id);Log("scan");
        Assert.Equal(cold?1u:9u,CloudShellState.Read(path).ProviderState);
        Assert.Equal(0u,CloudShellState.Read(path).TransferStatus!.Value&8);
        var marked=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(cold?0u:3u,marked.Info.PinState);Assert.Equal(before.Identity,marked.Identity);Assert.Equal(before.Info.InSyncState,marked.Info.InSyncState);
        Assert.Equal(before.Info.OnDiskSize,marked.Info.OnDiskSize);Assert.Equal(requests,test.Dav.Server.Requests.Count(x=>x.Method=="GET"));Assert.Empty(test.Repository.Uploads(test.Id));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path));Log("read");await test.Manager.ScanUploadChangesAsync(test.Id);
        Assert.Equal(3u,CloudFilesNative.Inspect(path)!.Value.Info.PinState);Assert.Equal(9u,CloudShellState.Read(path).ProviderState);
        await test.Manager.DisconnectAsync(test.Id);await test.Manager.ConnectAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,false);Log("reconnected");
        Assert.Equal(9u,CloudShellState.Read(path).ProviderState);Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method is "PUT" or "MOVE" or "DELETE");
    }
}
