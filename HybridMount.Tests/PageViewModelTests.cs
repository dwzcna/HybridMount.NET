using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HybridMount.Services.Mounting;
using HybridMount.ViewModels;

namespace HybridMount.Tests;

/// <summary>页面状态归属、重复提交、故障回滚和迟到通知；不创建正式挂载或接触用户配置。</summary>
public sealed class PageViewModelTests
{
    private sealed class Dispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _work=new();
        public bool Reject;
        public int Pending=>_work.Count;
        public bool TryEnqueue(Action callback){if(Reject)return false;_work.Enqueue(callback);return true;}
        public void Drain(){while(_work.TryDequeue(out var callback))callback();}
    }
    private sealed class Definitions : IMountDefinitionStore
    {
        public bool FailSave,FailCacheRead,FailCacheSave;private long _cache=10L*1024*1024*1024;
        public readonly List<MountDefinition> Items=[];
        public long CacheLimitBytes {get=>FailCacheRead?throw new IOException("read"):_cache;set{if(FailCacheSave)throw new IOException("save");_cache=value;}}
        public IReadOnlyList<MountDefinition> LoadDefinitions()=>Items.ToArray();
        public void SaveDefinition(MountDefinition mount){if(FailSave)throw new IOException("save");Items.RemoveAll(x=>x.Id==mount.Id);Items.Add(mount);}
        public void DeleteDefinition(Guid id)=>Items.RemoveAll(x=>x.Id==id);
    }
    private sealed class Area : IAsyncDisposable
    {
        private readonly string _area=Path.Combine(Path.GetTempPath(),"HybridMount.PageVmTests",Guid.NewGuid().ToString("N"));
        public Definitions Store {get;}=new();public MountRepository Repository {get;}
        public MountManager Manager {get;}public LoggerService Logger {get;}public Dispatcher Ui {get;}=new();
        public MountDefinition A=>Store.Items[0];public MountDefinition B=>Store.Items[1];
        public Area()
        {
            Directory.CreateDirectory(_area);Repository=new(Path.Combine(_area,"data"),definitions:Store);Logger=new(Path.Combine(_area,"log.db"));
            foreach(var name in new[]{"A","B"}){var local=Path.Combine(_area,name);Directory.CreateDirectory(local);Store.Items.Add(new(Guid.NewGuid(),name,local,false,RootIdentity:MountPathGuard.FileIdentity(local),Source:new(FileSourceKind.WebDav,"https://example.test/dav/"),RegistrationStage:MountRegistrationStage.NotRegistered));}
            Manager=new(Repository,Logger);
        }
        public async ValueTask DisposeAsync(){await Manager.DisposeAsync();await Logger.DisposeAsync();Directory.Delete(_area,true);}
    }
    [Fact]
    public async Task Capacity_drafts_survive_selection_and_save_failure_rolls_back_only_selected_mount()
    {
        await using var area=new Area();using var vm=new MountsViewModel(area.Manager,area.Ui);var a=area.A;var b=area.B;
        vm.SelectedMountId=a.Id;vm.UpdateCapacityDraft(a.Id,false,"1.25","TB");vm.SelectedMountId=b.Id;vm.UpdateCapacityDraft(b.Id,true,"2","GB");vm.RefreshNow();vm.SelectedMountId=a.Id;
        Assert.Equal("1.25",vm.CapacitySettingsDraft(a).Text);Assert.True(vm.CapacitySettingsDraft(b).Automatic);
        await vm.SaveSelectedCapacityAsync();var saved=area.Manager.Mounts.Single(x=>x.Definition.Id==a.Id).Definition;Assert.Equal(1_250_000_000_000,saved.MaximumCapacityBytes);
        area.Store.FailSave=true;vm.UpdateCapacityDraft(a.Id,false,"8","GB");await vm.SaveSelectedCapacityAsync();
        Assert.Equal(saved,area.Manager.Mounts.Single(x=>x.Definition.Id==a.Id).Definition);Assert.Equal("1250",vm.CapacitySettingsDraft(saved).Text);Assert.Equal("2",vm.CapacitySettingsDraft(b).Text);
        area.Store.FailSave=false;vm.UpdateCapacityDraft(a.Id,false,"-1","GB");await vm.SaveSelectedCapacityAsync();Assert.Equal(saved,area.Manager.Mounts.Single(x=>x.Definition.Id==a.Id).Definition);
        await area.Manager.DisposeAsync();vm.UpdateCapacityDraft(a.Id,false,"9","GB");await vm.SaveSelectedCapacityAsync();Assert.Equal(saved,area.Store.Items.Single(x=>x.Id==a.Id));
    }
    private sealed class AppearanceStore : IAppearancePreferenceStore
    {
        public AppearancePreferences Value=AppearancePreferences.Default;public bool Fail;public int Writes;
        public AppearancePreferences Read()=>Fail?throw new IOException("read"):Value;
        public void Write(AppearancePreferences value){Writes++;if(Fail)throw new IOException("write");Value=value;}
    }
    private sealed class Startup : IStartupService
    {
        public int Sets,Gets;public readonly TaskCompletionSource<StartupState> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StartupState> GetAsync(){Gets++;return Task.FromResult(new StartupState(false,true,"disabled"));}
        public Task<StartupState> SetAsync(bool value){Sets++;return Completion.Task;}
    }
    private static string Root()
    {for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)if(File.Exists(Path.Combine(directory.FullName,"Directory.Build.props")))return directory.FullName;throw new IOException("需要源码仓库。");}
    [Fact]
    public void Every_page_owns_exactly_its_named_viewmodel_and_viewmodels_have_no_view_dependency()
    {
        var root=Root();
        foreach(var name in new[]{"Home","Mounts","Tasks","Logs","Settings"})
        {
            var page=File.ReadAllText(Path.Combine(root,"HybridMount","Pages",name+"Page.xaml.cs"));
            var declarations=Regex.Matches(page,@"public\s+(\w+ViewModel)\s+ViewModel\b");Assert.Single(declarations);Assert.Equal(name+"ViewModel",declarations[0].Groups[1].Value);
            Assert.DoesNotContain("App.Current.Mounts.",page);Assert.DoesNotContain("new AppearanceSettingsViewModel",page);
            var vm=File.ReadAllText(Path.Combine(root,"HybridMount","ViewModels",name+"ViewModel.cs"));
            Assert.DoesNotContain("Microsoft.UI",vm);Assert.DoesNotContain("MainWindow",vm);Assert.DoesNotContain("App.Current",vm);
            foreach(var other in new[]{"Home","Mounts","Tasks","Logs","Settings"}.Where(x=>x!=name))Assert.DoesNotContain(other+"ViewModel",vm);
        }
        Assert.False(File.Exists(Path.Combine(root,"HybridMount","ViewModels","AppearanceSettingsViewModel.cs")));
        var app=File.ReadAllText(Path.Combine(root,"HybridMount","App.xaml.cs"));Assert.DoesNotContain("SettingsViewModel",app);Assert.Contains("Preferences.CloseToTray",app);
    }
    [Fact]
    public async Task Mount_drafts_and_selection_survive_refresh_and_each_mount_has_independent_busy_guard()
    {
        await using var area=new Area();using var vm=new MountsViewModel(area.Manager,area.Ui);
        var a=area.A.Id;var b=area.B.Id;vm.SelectedMountId=a;vm.UpdateDraft(a,false,91,601);vm.SelectedMountId=b;vm.UpdateDraft(b,true,92,602);vm.RefreshNow();vm.SelectedMountId=a;
        Assert.Equal(91,vm.Draft(vm.RequireSelected().Definition).Incremental);Assert.Equal(92,vm.Draft(area.B).Incremental);
        var wait=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var aCalls=0;var bCalls=0;
        var first=vm.RunMountAsync(a,async()=>{aCalls++;await wait.Task;});await vm.RunMountAsync(a,()=>{aCalls++;return Task.CompletedTask;});
        await vm.RunMountAsync(b,()=>{bCalls++;return Task.CompletedTask;});Assert.True(vm.IsBusy(a));Assert.False(vm.IsBusy(b));
        wait.SetResult();await first;Assert.Equal(1,aCalls);Assert.Equal(1,bCalls);Assert.Equal(a,vm.SelectedMountId);Assert.False(area.Manager.IsStopping);
    }
    [Fact]
    public async Task Mount_save_failure_discards_only_its_own_draft_and_restores_persisted_options()
    {
        await using var area=new Area();using var vm=new MountsViewModel(area.Manager,area.Ui);var original=area.A;
        vm.SelectedMountId=original.Id;vm.UpdateDraft(original.Id,false,99,999);vm.UpdateDraft(area.B.Id,false,88,888);area.Store.FailSave=true;
        await vm.SaveSelectedDetectionAsync();Assert.Equal(original.RemoteIncrementalSeconds,vm.Draft(original).Incremental);Assert.Equal(88,vm.Draft(area.B).Incremental);
        await vm.SetSelectedThumbnailAsync(true);Assert.False(vm.ThumbnailDraft(original));Assert.False(area.Manager.Mounts.Single(x=>x.Definition.Id==original.Id).Definition.ThumbnailsEnabled);
        Assert.Contains("save",vm.Status);Assert.Null(MountPathGuard.ReadRoot(original.LocalPath));
    }
    [Fact]
    public async Task Mount_dialogs_are_serial_and_dispose_blocks_late_results_without_stopping_background_manager()
    {
        await using var area=new Area();var vm=new MountsViewModel(area.Manager,area.Ui);var wait=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;
        var first=vm.RunDialogAsync(async()=>{calls++;await wait.Task;vm.Status="late";});await vm.RunDialogAsync(()=>{calls++;return Task.CompletedTask;});
        var status=vm.Status;var notifications=0;vm.PropertyChanged+=(_,_)=>notifications++;vm.Dispose();wait.SetResult();await first;
        Assert.Equal(1,calls);Assert.Equal(status,vm.Status);Assert.Equal(0,notifications);Assert.False(area.Manager.IsStopping);
    }
    [Fact]
    public async Task Snapshot_dispatch_coalesces_events_recovers_after_rejection_and_ignores_disposed_work()
    {
        await using var area=new Area();var vm=new MountsViewModel(area.Manager,area.Ui);var refreshed=0;vm.Refreshed+=()=>refreshed++;
        area.Ui.Reject=true;await area.Manager.ClearCompletedOperationsAsync();Assert.Equal(0,area.Ui.Pending);
        area.Ui.Reject=false;await area.Manager.ClearCompletedOperationsAsync();await area.Manager.ClearCompletedOperationsAsync();Assert.Equal(1,area.Ui.Pending);
        area.Ui.Drain();Assert.Equal(1,refreshed);await area.Manager.ClearCompletedOperationsAsync();vm.Dispose();area.Ui.Drain();Assert.Equal(1,refreshed);
        await area.Manager.ClearCompletedOperationsAsync();Assert.Equal(0,area.Ui.Pending);Assert.False(area.Manager.IsStopping);
    }
    [Fact]
    public async Task Task_copy_captures_original_selection_and_can_only_submit_once_per_dialog()
    {
        await using var area=new Area();var mount=area.A.Id;
        foreach(var id in new[]{"first","second"}){var item=new LocalUpload(id,mount,id+".txt",id+".txt",false,"stamp",1,"Conflict",DateTimeOffset.UtcNow);area.Repository.SaveUpload(item);area.Repository.SaveOperation(item.ToSnapshot());}
        using var vm=new TasksViewModel(area.Manager,area.Ui);vm.SelectedJobId="first";Assert.True(vm.BeginCopy());Assert.False(vm.BeginCopy());vm.CopyPath="新副本.txt";vm.SelectedJobId="second";vm.SubmitCopy();vm.SubmitCopy();vm.EndDialog();
        var copy=Assert.Single(area.Repository.Uploads(mount),x=>x.IsCopy);Assert.Equal("first.txt",copy.LocalPath);Assert.Equal("新副本.txt",copy.SourcePath);Assert.Equal(copy.Id,vm.SelectedJobId);
        area.Ui.Drain();Assert.Equal(copy.Id,vm.SelectedJobId);Assert.Equal("Conflict",area.Repository.Upload("first")!.State);
    }
    [Fact]
    public async Task Task_storage_fault_is_reported_without_escaping_ui_command_or_sticking_dialog_guard()
    {
        await using var area=new Area();area.Repository.SaveOperation(new("history","upload","Completed",1,1,[],null));
        using var vm=new TasksViewModel(area.Manager,area.Ui);vm.SelectedJobId="history";
        var directory=area.Repository.DataDirectory;var held=directory+"-held";Directory.Move(directory,held);Directory.CreateDirectory(Path.Combine(directory,"mounts.db"));
        try{Assert.Null(vm.BeginResults());Assert.Null(Record.Exception(vm.CancelSelected));Assert.Null(Record.Exception(vm.RetrySelected));Assert.NotEmpty(vm.Status);}
        finally{Directory.Delete(directory,true);Directory.Move(held,directory);}
        Assert.NotNull(vm.BeginResults());vm.EndDialog();
    }
    [Fact]
    public async Task Settings_startup_is_not_submitted_twice_and_dispose_waits_without_late_notifications()
    {
        var startup=new Startup();var vm=new SettingsViewModel(new(new AppearanceStore()),startup);var work=vm.ChangeStartupAsync(true);await vm.ChangeStartupAsync(true);Assert.Equal(1,startup.Sets);
        var notifications=0;vm.PropertyChanged+=(_,_)=>notifications++;var stop=vm.DisposeAsync().AsTask();Assert.False(stop.IsCompleted);startup.Completion.SetResult(new(true,true,"enabled"));await work;await stop;
        Assert.Equal(0,notifications);Assert.False(vm.StartupSelection);Assert.False(vm.CanEditStartup);Assert.Equal(0,startup.Gets);
    }
    [Fact]
    public void Settings_appearance_reentrant_notifications_do_not_duplicate_save_and_failures_keep_value()
    {
        var store=new AppearanceStore();using var vm=new SettingsViewModel(new(store));
        vm.PropertyChanged+=(_,args)=>{if(args.PropertyName==nameof(vm.ThemeSelection))vm.ThemeSelection="Light";};vm.ThemeSelection="Dark";Assert.Equal(1,store.Writes);Assert.Equal("Dark",vm.ThemeSelection);
        store.Fail=true;vm.ThemeSelection="Light";Assert.Equal("Dark",vm.ThemeSelection);vm.ReloadAppearance();Assert.Equal("Dark",vm.ThemeSelection);Assert.Equal(2,store.Writes);
    }
    [Fact]
    public async Task Settings_cache_draft_survives_refresh_and_failed_save_restores_last_good_value()
    {
        await using var area=new Area();using var vm=new SettingsViewModel(new(new AppearanceStore()),mounts:area.Manager);vm.CacheLimitGiB=17;vm.ReloadAppearance();Assert.Equal(17,vm.CacheLimitGiB);
        area.Store.FailCacheSave=true;vm.SaveCacheLimit();Assert.Equal(10,vm.CacheLimitGiB);Assert.Equal(10L*1024*1024*1024,area.Store.CacheLimitBytes);
        area.Store.FailCacheSave=false;vm.CacheLimitGiB=double.NaN;vm.SaveCacheLimit();Assert.Equal(10,vm.CacheLimitGiB);vm.CacheLimitGiB=12;vm.SaveCacheLimit();Assert.Equal(12L*1024*1024*1024,area.Store.CacheLimitBytes);
    }
    [Fact]
    public async Task Settings_unreadable_cache_cannot_overwrite_formal_value()
    {
        await using var area=new Area();area.Store.FailCacheRead=true;using var vm=new SettingsViewModel(new(new AppearanceStore()),mounts:area.Manager);Assert.False(vm.CanEditCache);vm.CacheLimitGiB=99;vm.SaveCacheLimit();area.Store.FailCacheRead=false;Assert.Equal(10L*1024*1024*1024,area.Store.CacheLimitBytes);
    }
    [Fact]
    public void Window_lifecycle_preference_is_independent_of_viewmodel_and_failed_save_keeps_previous()
    {
        var writes=0;var fail=false;var preference=new ApplicationPreferencesService(()=>false,value=>{writes++;if(fail)throw new IOException("save");});Assert.False(preference.CloseToTray);
        using(var vm=new SettingsViewModel(new(new AppearanceStore()),preferences:preference)){vm.CloseToTraySelection=true;Assert.True(preference.CloseToTray);fail=true;vm.CloseToTraySelection=false;Assert.True(vm.CloseToTraySelection);}
        Assert.True(preference.CloseToTray);Assert.Equal(2,writes);
    }
    [Fact]
    public async Task Log_filters_remain_and_queued_notifications_do_not_update_disposed_viewmodel()
    {
        await using var area=new Area();var vm=new LogsViewModel(area.Logger,area.Ui);area.Logger.Queue("test","Warning","中文 log");await vm.RefreshAsync();area.Ui.Drain();var resets=0;vm.Entries.CollectionChanged+=(_,args)=>{if(args.Action==System.Collections.Specialized.NotifyCollectionChangedAction.Reset)resets++;};vm.Query="中文";vm.Severity="Warning";await vm.RefreshAsync();Assert.Single(vm.Entries);Assert.Equal(0,resets);
        Assert.True(vm.TryBeginDialog());Assert.False(vm.TryBeginDialog());vm.EndDialog();area.Logger.Queue("test","Warning","中文 late");await area.Logger.ReadAsync();Assert.True(area.Ui.Pending>0);
        vm.Dispose();area.Ui.Drain();Assert.Single(vm.Entries);Assert.Equal("中文",vm.Query);Assert.Equal("Warning",vm.Severity);await vm.ClearAsync();Assert.Equal(2,(await area.Logger.ReadAsync()).Count);
    }
}
