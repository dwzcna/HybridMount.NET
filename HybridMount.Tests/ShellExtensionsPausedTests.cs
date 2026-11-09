using HybridMount.Services.Mounting;
using HybridMount.ViewModels;
using global::Windows.Storage.Provider;

namespace HybridMount.Tests;

/// <summary>本版生产默认暂停与保留实现的显式测试开关分离；不加载正式配置或访问真实服务。</summary>
public sealed class ShellExtensionsPausedTests
{
    [Fact]
    public async Task Production_cache_gate_precedes_paths_disk_and_rendering()
    {
        var rendered=0;var mount=new MountDefinition(Guid.NewGuid(),"paused","\0",ThumbnailsEnabled:true);
        var entry=new MountEntry("entry","photo.png","photo.png","version",false,Materialized:true);
        await using var cache=new ExePreviewCache("\0",null,null,(_,_,_,_)=>{Interlocked.Increment(ref rendered);throw new InvalidOperationException();},()=>false);
        cache.Queue(mount,entry,true);cache.Queue(mount,entry,false);
        Assert.Null(cache.Get(mount,entry,64));Assert.Equal("disabled",cache.State(mount,entry));Assert.Equal(0,rendered);
        Assert.Contains("待处理=0",cache.Describe(mount.Id));
    }
    [Fact(Timeout=60000)]
    public async Task Paused_public_requests_do_not_hydrate_or_read_remote_even_with_saved_preference()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);
        var saved=test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true};test.Repository.SaveDefinition(saved);
        await test.Manager.DisposeAsync();test.Manager=new MountManager(test.Repository,test.Logger);
        await test.Manager.ConnectAsync(test.Id);await test.Manager.SetSchedulingForTestsAsync(test.Id,false);
        var path=Path.Combine(test.Local,"文件.bin");var before=CloudFilesNative.Inspect(path)!.Value;var requests=test.Dav.Server.Requests.Count;
        for(var i=0;i<5;i++)
        {
            Assert.Equal("disabled",test.Manager.RequestPreview(path,64));Assert.Equal("disabled",test.Manager.PreviewState(path));Assert.Null(test.Manager.GetPreview(path,64));
        }
        Assert.Equal("disabled",test.Manager.RequestPreview("\0",64));Assert.Null(test.Manager.GetPreview("\0",64));
        await Assert.ThrowsAsync<NotSupportedException>(()=>test.Manager.SetThumbnailsEnabledAsync(test.Id,false));
        Assert.True(test.Repository.LoadDefinitions().Single().ThumbnailsEnabled);Assert.True(test.Manager.Mounts.Single().Definition.ThumbnailsEnabled);
        var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.OnDiskSize,after.Info.OnDiskSize);
        Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
    }
    [Fact(Timeout=60000)]
    public async Task Default_ui_hides_controls_and_keeps_thumbnail_draft_and_mount_selection()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);
        var saved=test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true};test.Repository.SaveDefinition(saved);
        await test.Manager.DisposeAsync();test.Manager=new MountManager(test.Repository,test.Logger);
        using var vm=new MountsViewModel(test.Manager,new InlineDispatcher());vm.SelectedMountId=test.Id;
        Assert.False(vm.CanConfigureThumbnails);Assert.False(vm.Items.Single().CanConfigureThumbnails);Assert.True(vm.ThumbnailDraft(saved));
        Assert.True(vm.Items.Single().ThumbnailsEnabled);Assert.False(ShellExtensionPolicy.CustomContextMenusEnabled);Assert.Contains("非打包",vm.ThumbnailCapability.Reason);Assert.False(ShellExtensionPolicy.CustomThumbnailsEnabled);Assert.Equal(test.Id,vm.SelectedMountId);
    }
    [Fact(Timeout=60000)]
    public async Task New_descriptor_allows_native_pinning_without_policy_identity_or_source_changes()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var mount=test.Manager.Mounts.Single().Definition;
        var before=CloudRootPolicy.Read(mount.LocalPath);var gets=test.Dav.Server.Requests.Count;
        var descriptor=await CloudRootRegistration.CreateDescriptorAsync(mount);
        Assert.True(descriptor.AllowPinning);Assert.Equal(StorageProviderHydrationPolicy.Partial,descriptor.HydrationPolicy);
        Assert.Equal(mount.LocalPath,descriptor.Path.Path);Assert.Equal(CloudRootRegistration.Id(mount),descriptor.Id);
        // GetCurrentSyncRoots受身份过滤；真实原生菜单由独立MSIX场景核验，不把不可枚举当作注册失败。
        var after=CloudRootPolicy.Read(mount.LocalPath);Assert.Equal(before.FileId,after.FileId);Assert.Equal(before.Identity,after.Identity);Assert.Equal(before.Hydration,after.Hydration);Assert.Equal(gets,test.Dav.Server.Requests.Count);
    }
    private sealed class InlineDispatcher:IUiDispatcher
    {public bool TryEnqueue(Action action){action();return true;}}
}
