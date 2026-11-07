using HybridMount.Services.Mounting;
using Windows.Storage;
using Windows.Storage.Provider;
namespace HybridMount.Tests;

/// <summary>已捕获的空白状态向量回归；真实NTFS根验证无正文水合、无PUT、无身份或代次变化。</summary>
public sealed class ShellCustomStateRepairTests
{
    [Fact]
    public void OnlyOwnedBrokenEntryCanBeRemovedWithoutGuessingForeignValues()
    {
        var broken=new ShellCustomState(["1"],[],[]);var clear=broken.Replace(1,"","");Assert.True(clear.Changed);Assert.Empty(clear.Properties);
        var replaced=broken.Replace(1,"失败","file:///icon.png");Assert.Single(replaced.Properties);Assert.Equal("失败",replaced.Properties[0].Value);
        Assert.Throws<IOException>(()=>new ShellCustomState(["1","42"],[],[]).Replace(1,"",""));
        Assert.False(new ShellCustomState(["42"],[],[]).Replace(1,"","").Changed);
    }
    [Fact]
    public void ForeignCompleteStateIsPreservedForPublishAndClear()
    {
        var original=new ShellCustomState(["42","1"],["其他状态","旧失败"],["other-icon","our-icon"]);
        var cleared=original.Replace(1,"","");Assert.Equal(new ShellCustomProperty(42,"其他状态","other-icon"),Assert.Single(cleared.Properties));
        var changed=original.Replace(1,"新失败","our-icon");Assert.Equal(cleared.Properties[0],changed.Properties[0]);Assert.Equal("新失败",changed.Properties[1].Value);
        Assert.False(new ShellCustomState([],[],[]).Replace(1,"","").Changed);
    }
    [Fact(Timeout=60000)]
    public async Task ActualPublisherPreservesForeignStateAndRemovesOwnIdentifier()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"foreign-property.txt");await File.WriteAllTextAsync(path,"unchanged content");
        var icon=new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png")).AbsoluteUri;
        await StorageProviderItemProperties.SetAsync(await StorageFile.GetFileFromPathAsync(path),[new(){Id=42,Value="其他状态",IconResource=icon}]);
        await test.Manager.ScanUploadChangesAsync(test.Id);var row=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="foreign-property.txt");test.Repository.UpdateUpload(row.Id,x=>x with{State="Failed",Error="precise failure"});
        var network=test.Dav.Server.Requests.Count;await test.Manager.PublishUploadIssuesAsync();Assert.Equal("Published",test.Repository.Upload(row.Id)!.Attention!.ShellState);
        var published=CloudCustomStateReader.Read(path);Assert.Equal(new[]{"42","1"},published.Ids);Assert.Equal(2,published.Values.Count);Assert.Equal(2,published.Icons.Count);
        test.Repository.UpdateUpload(row.Id,x=>x with{State="Canceled",CancelRequested=true});await test.Manager.PublishUploadIssuesAsync();var cleared=CloudCustomStateReader.Read(path);
        Assert.Equal(new[]{"42"},cleared.Ids);Assert.Equal(new[]{"其他状态"},cleared.Values);Assert.Equal(new[]{icon},cleared.Icons);
        Assert.Equal(network,test.Dav.Server.Requests.Count);Assert.Equal("unchanged content",await File.ReadAllTextAsync(path));Assert.Equal(row.Generation,test.Repository.Upload(row.Id)!.Generation);
    }
    [Fact(Timeout=60000)]
    public async Task RestartRepairsLegacyClearedBlankPropertyWithoutReplayOrIdentityChange()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);var path=Path.Combine(test.Local,"legacy-property.txt");await File.WriteAllTextAsync(path,"keep bytes");await test.Manager.ScanUploadChangesAsync(test.Id);
        var row=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="legacy-property.txt");test.Repository.UpdateUpload(row.Id,x=>x with{State="Failed",Error="old failure"});await test.Manager.PublishUploadIssuesAsync();
        test.Repository.UpdateUpload(row.Id,x=>x with{State="Canceled",CancelRequested=true});await test.Manager.PublishUploadIssuesAsync();
        // 重现旧API调用产生的编号1、文字0、图标0；不在Explorer里打开这个测试条目。
        await StorageProviderItemProperties.SetAsync(await StorageFile.GetFileFromPathAsync(path),[new(){Id=1,Value="",IconResource=""}]);
        var legacy=CloudCustomStateReader.Read(path);Assert.Equal(new[]{"1"},legacy.Ids);Assert.Empty(legacy.Values);Assert.Empty(legacy.Icons);
        test.Repository.UpdateUpload(row.Id,x=>x with{ObservedStamp=LocalFileState.Read(path).Key});await test.Manager.DisposeAsync();var identity=LocalFileState.Read(path).Identity;var network=test.Dav.Server.Requests.Count;
        test.Manager=new(test.Repository,test.Logger);await test.Manager.SetSchedulingForTestsAsync(test.Id,false);await test.Manager.PublishUploadIssuesAsync();
        Assert.Empty(CloudCustomStateReader.Read(path).Ids);var repaired=test.Repository.Upload(row.Id)!;Assert.Equal("Cleared",repaired.Attention!.ShellState);Assert.Equal("Canceled",repaired.State);Assert.Equal(row.Generation,repaired.Generation);
        Assert.Equal(identity,LocalFileState.Read(path).Identity);Assert.Equal(network,test.Dav.Server.Requests.Count);Assert.Equal("keep bytes",await File.ReadAllTextAsync(path));
    }
}
