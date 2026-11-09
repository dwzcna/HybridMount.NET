using Microsoft.Win32;
using HybridMount.ViewModels;
namespace HybridMount.Tests;

/// <summary>合并后的统一外观入口；真实隔离 HKCU 与注入故障分开，不读取正式配置。</summary>
public sealed class AppearanceSettingsTests
{
    private sealed class Store : IAppearancePreferenceStore
    {
        public AppearancePreferences Value=new(AppearanceBackdrop.MicaBase,AppearanceTheme.Dark);
        public bool FailRead,FailWrite;public int Writes;
        public AppearancePreferences Read()=>FailRead?throw new IOException("SECRET read failure"):Value;
        public void Write(AppearancePreferences value){Writes++;if(FailWrite)throw new IOException("SECRET write failure");Value=value;}
    }
    [Fact]
    public void First_read_does_not_create_registry_and_success_restarts_with_existing_keys_and_other_preferences()
    {
        var path=@"Software\HybridMount.NET.Tests\Appearance-"+Guid.NewGuid().ToString("N");
        try
        {
            var service=new AppearanceSettingsService(path);Assert.True(service.IsLoaded);Assert.Equal(AppearancePreferences.Default,service.Current);
            using(var missing=Registry.CurrentUser.OpenSubKey(path))Assert.Null(missing);
            using(var seed=Registry.CurrentUser.CreateSubKey(path,true)){seed.SetValue("CloseToTray",0,RegistryValueKind.DWord);seed.SetValue("StartupEnabled",1,RegistryValueKind.DWord);seed.SetValue("AppearanceSettings","old-json",RegistryValueKind.String);}
            var saved=new AppearancePreferences(AppearanceBackdrop.AcrylicThin,AppearanceTheme.Dark);Assert.True(service.TrySave(saved,out var error));Assert.Null(error);Assert.Equal(saved,new AppearanceSettingsService(path).Current);
            using var key=Registry.CurrentUser.OpenSubKey(path)!;Assert.Equal(4,key.GetValue("AppearanceBackdrop"));Assert.Equal(2,key.GetValue("AppearanceTheme"));Assert.Equal(RegistryValueKind.DWord,key.GetValueKind("AppearanceBackdrop"));Assert.Null(key.GetValue("AppearanceSettings"));Assert.Equal(0,key.GetValue("CloseToTray"));Assert.Equal(1,key.GetValue("StartupEnabled"));
        }
        finally{Registry.CurrentUser.DeleteSubKeyTree(path,false);}
    }
    [Fact]
    public void Invalid_registry_values_fall_back_per_field_without_rewriting_values_or_schema()
    {
        var path=@"Software\HybridMount.NET.Tests\Appearance-"+Guid.NewGuid().ToString("N");
        try
        {
            using var key=Registry.CurrentUser.CreateSubKey(path,true);key.SetValue("AppearanceBackdrop",2L,RegistryValueKind.QWord);key.SetValue("AppearanceTheme",2,RegistryValueKind.DWord);key.SetValue("AppearanceSettings","legacy",RegistryValueKind.String);
            var service=new AppearanceSettingsService(path);Assert.Equal(new(AppearanceBackdrop.None,AppearanceTheme.Dark),service.Current);Assert.Equal(RegistryValueKind.QWord,key.GetValueKind("AppearanceBackdrop"));Assert.Null(key.GetValue("SchemaVersion"));Assert.Equal("legacy",key.GetValue("AppearanceSettings"));
            key.SetValue("AppearanceBackdrop",4,RegistryValueKind.DWord);key.SetValue("AppearanceTheme",99,RegistryValueKind.DWord);Assert.True(service.Reload());Assert.Equal(new(AppearanceBackdrop.AcrylicThin,AppearanceTheme.System),service.Current);Assert.Equal(99,key.GetValue("AppearanceTheme"));Assert.Null(key.GetValue("SchemaVersion"));
        }
        finally{Registry.CurrentUser.DeleteSubKeyTree(path,false);}
    }
    [Fact]
    public void Unreadable_store_never_saves_defaults_and_recovery_reveals_original_snapshot()
    {
        var store=new Store{FailRead=true};var service=new AppearanceSettingsService(store);using var vm=new SettingsViewModel(service);Assert.False(service.IsLoaded);Assert.False(vm.CanEditAppearance);
        vm.ThemeSelection="Light";Assert.False(service.TrySave(AppearancePreferences.Default,out var failure));Assert.NotNull(failure);Assert.Equal(0,store.Writes);Assert.DoesNotContain("SECRET",service.ErrorStatus!);
        store.FailRead=false;Assert.True(service.Reload());Assert.Equal("Dark",vm.ThemeSelection);Assert.Equal("MicaBase",vm.BackdropSelection);Assert.True(vm.CanEditAppearance);Assert.Equal(0,store.Writes);
    }
    [Fact]
    public void Notifications_only_follow_success_and_faulty_subscriber_does_not_skip_following_subscriber()
    {
        var store=new Store();var service=new AppearanceSettingsService(store);var changes=0;service.Changed+=(_,_)=>throw new IOException("display");service.Changed+=(_,_)=>changes++;
        var saved=new AppearancePreferences(AppearanceBackdrop.MicaAltBaseAlt,AppearanceTheme.Light);Assert.True(service.TrySave(saved,out _));Assert.Equal(1,changes);
        store.FailWrite=true;Assert.False(service.TrySave(new(AppearanceBackdrop.AcrylicBase,AppearanceTheme.Dark),out var failure));Assert.DoesNotContain("SECRET",failure!);Assert.Equal(saved,service.Current);Assert.Equal(saved,store.Value);Assert.Equal(1,changes);
        Assert.False(service.TrySave(new((AppearanceBackdrop)99,AppearanceTheme.Dark),out _));Assert.Equal(2,store.Writes);Assert.Equal(1,changes);
        store.FailRead=true;Assert.False(service.Reload());Assert.Equal(saved,service.Current);Assert.Equal(1,changes);store.FailRead=false;Assert.True(service.Reload());Assert.Equal(2,changes);
    }
    [Fact]
    public void Recreated_page_state_uses_same_service_and_disposed_state_has_no_late_notifications()
    {
        var store=new Store{Value=AppearancePreferences.Default};var service=new AppearanceSettingsService(store);var first=new SettingsViewModel(service);first.ThemeSelection="Dark";
        var oldNotifications=0;first.PropertyChanged+=(_,_)=>oldNotifications++;first.Dispose();var second=new SettingsViewModel(service);Assert.Equal("Dark",second.ThemeSelection);
        second.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(second.ThemeSelection))second.ThemeSelection="Dark";};second.BackdropSelection="MicaAltBaseAlt";Assert.Equal(2,store.Writes);Assert.Equal(0,oldNotifications);
        Assert.True(service.TrySave(new(AppearanceBackdrop.MicaAltBaseAlt,AppearanceTheme.Light),out _));Assert.Equal("Light",second.ThemeSelection);Assert.Equal(3,store.Writes);
        var afterDispose=0;second.PropertyChanged+=(_,_)=>afterDispose++;second.Dispose();Assert.True(service.TrySave(AppearancePreferences.Default,out _));Assert.Equal(0,afterDispose);Assert.Equal(0,oldNotifications);
    }
    [Fact]
    public void Production_entry_is_unified_and_default_store_is_internal_in_same_file()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Directory.Build.props")))root=root.Parent;Assert.NotNull(root);
        var source=Path.Combine(root.FullName,"HybridMount.NET");Assert.False(File.Exists(Path.Combine(source,"Services","AppearancePreferenceStoreService.cs")));
        var merged=File.ReadAllText(Path.Combine(source,"Services","AppearanceSettingsService.cs"));Assert.Contains("internal sealed class RegistryAppearancePreferenceStore",merged);
        Assert.Contains("Appearance=new AppearanceSettingsService();",File.ReadAllText(Path.Combine(source,"App.xaml.cs")));Assert.NotNull(typeof(AppearanceSettingsService).GetConstructor(Type.EmptyTypes));
        Assert.True(typeof(AppearanceSettingsService).Assembly.GetType("HybridMount.Services.RegistryAppearancePreferenceStore")!.IsNotPublic);Assert.Null(typeof(AppearanceSettingsService).Assembly.GetType("HybridMount.Services.AppearancePreferenceStoreService"));
    }
}
