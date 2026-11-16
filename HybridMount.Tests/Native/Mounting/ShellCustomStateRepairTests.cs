using HybridMount.Infrastructure.Files;
using HybridMount.Infrastructure.Windows.CloudFiles;
using HybridMount.Models.Mounting;
using HybridMount.Tests.Integration.Mounting;
using global::Windows.Storage;
using global::Windows.Storage.Provider;
namespace HybridMount.Tests.Native.Mounting;

/// <summary>当前属性清除与外部状态保护回归；真实NTFS根验证无正文水合、无PUT、无身份或代次变化。</summary>
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
        await NativeTestFiles.SetPropertiesAsync(path,new ShellCustomProperty(42,"其他状态",icon));
        await test.Manager.ScanUploadChangesAsync(test.Id);var row=test.Repository.Uploads(test.Id).Single(x=>x.LocalPath=="foreign-property.txt");test.Repository.UpdateUpload(row.Id,x=>x with{State="Failed",Error="precise failure"});
        var network=test.Dav.Server.Requests.Count;await test.Manager.PublishUploadIssuesAsync();Assert.Equal("Published",test.Repository.Upload(row.Id)!.Attention!.ShellState);
        var published=CloudCustomStateReader.Read(path);Assert.Equal(new[]{"42","1"},published.Ids);Assert.Equal(2,published.Values.Count);Assert.Equal(2,published.Icons.Count);
        test.Repository.UpdateUpload(row.Id,x=>x with{State="Canceled",CancelRequested=true});await test.Manager.PublishUploadIssuesAsync();var cleared=CloudCustomStateReader.Read(path);
        Assert.Equal(new[]{"42"},cleared.Ids);Assert.Equal(new[]{"其他状态"},cleared.Values);Assert.Equal(new[]{icon},cleared.Icons);
        Assert.Equal(network,test.Dav.Server.Requests.Count);Assert.Equal("unchanged content",await File.ReadAllTextAsync(path));Assert.Equal(row.Generation,test.Repository.Upload(row.Id)!.Generation);
    }
}
