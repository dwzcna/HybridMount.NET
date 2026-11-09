using HybridMount.Services.Mounting;
using Windows.Foundation.Metadata;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace HybridMount.Tests;

public sealed class SyncRootIconRefreshTests
{
    [Theory]
    [InlineData(@"C:\WindowsApps\HybridMount_0.0.9.30\HybridMount.NET.exe,0")]
    [InlineData("")]
    public void Stale_or_empty_icon_registers_a_copy_with_current_icon(string icon)
    {
        var existing=new StorageProviderSyncRootInfo{Id="same-root",IconResource=icon};
        StorageProviderSyncRootInfo? registered=null;var calls=0;
        Refresh(existing,info=>{registered=info;calls++;});
        Assert.Equal(1,calls);Assert.NotNull(registered);Assert.NotSame(existing,registered);
        Assert.Equal(existing.Id,registered.Id);Assert.Equal(CloudRootRegistration.ExpectedIconResource,registered.IconResource);
        Assert.Equal(icon,existing.IconResource);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Current_icon_ignores_path_case_and_does_not_register(bool changeCase)
    {
        var expected=CloudRootRegistration.ExpectedIconResource;
        var existing=new StorageProviderSyncRootInfo{IconResource=changeCase?expected.ToUpperInvariant():expected};
        Refresh(existing,_=>Assert.Fail("Unexpected registration"));
    }
    [Fact]
    public void Foreign_mount_throws_before_reading_or_registering()
    {
        var error=Assert.Throws<IOException>(()=>CloudRootRegistration.RefreshExistingIcon(Guid.NewGuid(),Guid.NewGuid(),
            ()=>throw new InvalidOperationException("Must not read"),_=>Assert.Fail("Must not register"),_=>Assert.Fail("Must not swallow ownership failure")));
        Assert.Equal("同步根属于其他挂载。",error.Message);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_or_register_failure_is_logged_and_caller_continues(bool failRead)
    {
        var id=Guid.NewGuid();var error=new IOException("refresh failed");Exception? logged=null;var continued=false;
        var existing=new StorageProviderSyncRootInfo{IconResource="old.exe,0"};
        CloudRootRegistration.RefreshExistingIcon(id,id,()=>failRead?throw error:existing,_=>throw error,e=>logged=e);
        continued=true;
        Assert.True(continued);Assert.Same(error,logged);Assert.Equal("old.exe,0",existing.IconResource);
    }
    [Fact]
    public void Logger_failure_does_not_fail_the_mount()
    {
        var id=Guid.NewGuid();
        CloudRootRegistration.RefreshExistingIcon(id,id,()=>throw new IOException("unavailable"),
            _=>Assert.Fail("Must not register"),_=>throw new InvalidOperationException("logger unavailable"));
    }
    [Fact]
    public async Task Clone_preserves_every_supported_descriptor_property_and_custom_state()
    {
        var source=new StorageProviderSyncRootInfo
        {
            Id="preserved-id",Path=await StorageFolder.GetFolderFromPathAsync(AppContext.BaseDirectory),
            DisplayNameResource="preserved-name",IconResource="old.exe,0",Version="old-version",
            Context=CryptographicBuffer.CreateFromByteArray([1,7,9]),AllowPinning=false,
            HydrationPolicy=StorageProviderHydrationPolicy.Full,HydrationPolicyModifier=(StorageProviderHydrationPolicyModifier)1,
            PopulationPolicy=StorageProviderPopulationPolicy.Full,InSyncPolicy=StorageProviderInSyncPolicy.FileCreationTime,
            HardlinkPolicy=(StorageProviderHardlinkPolicy)1,ProtectionMode=(StorageProviderProtectionMode)1,
            ProviderId=Guid.NewGuid(),RecycleBinUri=new Uri("https://example.invalid/recycle"),ShowSiblingsAsGroup=true
        };
        source.StorageProviderItemPropertyDefinitions.Add(new(){Id=77,DisplayNameResource="custom state"});
        var fallback=ApiInformation.IsPropertyPresent("Windows.Storage.Provider.StorageProviderSyncRootInfo","FallbackFileTypeInfo");
        if(fallback)source.FallbackFileTypeInfo.Add(new(".custom","custom.ico,0"));
        var copy=CloudRootRegistration.CloneWithCurrentIcon(source);
        Assert.NotSame(source,copy);Assert.Equal("old.exe,0",source.IconResource);
        Assert.Equal(CloudRootRegistration.ExpectedIconResource,copy.IconResource);
        foreach(var property in typeof(StorageProviderSyncRootInfo).GetProperties())
        {
            if(property.Name is "IconResource" or "StorageProviderItemPropertyDefinitions" or "FallbackFileTypeInfo")continue;
            Assert.Equal(property.GetValue(source),property.GetValue(copy));
        }
        Assert.Equal(source.StorageProviderItemPropertyDefinitions.ToArray(),copy.StorageProviderItemPropertyDefinitions.ToArray());
        Assert.NotSame(source.StorageProviderItemPropertyDefinitions,copy.StorageProviderItemPropertyDefinitions);
        if(fallback)Assert.Equal(source.FallbackFileTypeInfo.ToArray(),copy.FallbackFileTypeInfo.ToArray());
        Assert.Equal(77,Assert.Single(copy.StorageProviderItemPropertyDefinitions).Id);
    }
    [Fact]
    public void Expected_icon_is_under_current_deployment_directory()
        =>Assert.Equal(Path.Combine(AppContext.BaseDirectory,"HybridMount.NET.exe")+",0",CloudRootRegistration.ExpectedIconResource);
    private static void Refresh(StorageProviderSyncRootInfo existing,Action<StorageProviderSyncRootInfo> register)
    {
        var id=Guid.NewGuid();Exception? error=null;
        CloudRootRegistration.RefreshExistingIcon(id,id,()=>existing,register,e=>error=e);
        Assert.Null(error);
    }
}
