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
        public Area(IFileSourceFactory? factory=null)
        {
            Directory.CreateDirectory(_area);Repository=new(Path.Combine(_area,"data"),definitions:Store);Logger=new(Path.Combine(_area,"log.db"));
            foreach(var name in new[]{"A","B"}){var local=Path.Combine(_area,name);Directory.CreateDirectory(local);Store.Items.Add(new(Guid.NewGuid(),name,local,false,RootIdentity:MountPathGuard.FileIdentity(local),Source:new(FileSourceKind.WebDav,"https://example.test/dav/"),RegistrationStage:MountRegistrationStage.NotRegistered));}
            Manager=new(Repository,Logger,factory){LegacyPreviewsForTests=true};
        }
        public async ValueTask DisposeAsync(){await Manager.DisposeAsync();await Logger.DisposeAsync();Directory.Delete(_area,true);}
    }

    [Fact]
    public void DensitySaveFailureKeepsPublishedLayoutAndIndependentMessageCloseState()
    {
        var saved=false;var fail=false;var writes=0;var notifications=0;
        var preference=new ApplicationPreferencesService(()=>true,_=>{},readCompact:()=>false,writeCompact:value=>{writes++;if(fail)throw new IOException("save");saved=value;});preference.LayoutChanged+=()=>notifications++;
        using var vm=new SettingsViewModel(new(new AppearanceStore()),preferences:preference);vm.DensitySelection="Compact";Assert.True(saved);Assert.True(preference.CompactLayout);Assert.Equal("Compact",vm.DensitySelection);Assert.Equal(1,notifications);
        vm.AppearanceMessage.IsOpen=false;vm.DensitySelection="Compact";Assert.False(vm.AppearanceMessage.IsOpen);Assert.Equal(1,writes);
        fail=true;vm.DensitySelection="Comfortable";Assert.Equal("Compact",vm.DensitySelection);Assert.True(saved);Assert.Equal(1,notifications);Assert.Equal(PageMessageSeverity.Error,vm.AppearanceMessage.Severity);Assert.True(vm.AppearanceMessage.IsOpen);Assert.Equal(2,writes);
        vm.DensitySelection="unknown";Assert.Equal(2,writes);
    }
    [Fact(Timeout=20000)]
    public async Task ImmediatePreferenceFailureRollsBackAndDoesNotSaveOtherDraftFields()
    {
        await using var area=new Area();var id=area.A.Id;var other=area.B.Id;using var vm=new MountsViewModel(area.Manager,area.Ui);
        area.Store.FailSave=true;await vm.SetAutoConnectAsync(id,true);area.Ui.Drain();Assert.False(area.Manager.TaskDefinitions[id].AutoConnect);Assert.Equal(PageMessageSeverity.Error,vm.StatusMessage.Severity);
        vm.StatusMessage.IsOpen=false;await vm.SetAutoConnectAsync(id,true);Assert.True(vm.StatusMessage.IsOpen);Assert.False(area.Manager.TaskDefinitions[id].AutoConnect);
        area.Store.FailSave=false;await vm.SetAutoConnectAsync(id,true);area.Ui.Drain();Assert.True(area.Manager.TaskDefinitions[id].AutoConnect);Assert.False(area.Manager.TaskDefinitions[other].AutoConnect);
        await area.Manager.SetCapacityAsync(id,false,5_000_000_000);area.Ui.Drain();vm.UpdateCapacityDraft(id,false,"未提交内容","GB");
        await vm.SetCapacityAutomaticAsync(id,true);area.Ui.Drain();var saved=area.Manager.TaskDefinitions[id];Assert.True(saved.CapacityAutoDetectEnabled);Assert.Equal(5_000_000_000,saved.MaximumCapacityBytes);Assert.Equal("未提交内容",vm.CapacitySettingsDraft(saved).Text);
        vm.UpdateDraft(id,true,88,999);await vm.SetRemoteDetectionEnabledAsync(id,false);area.Ui.Drain();saved=area.Manager.TaskDefinitions[id];Assert.False(saved.RemoteAutoDetectEnabled);Assert.Equal(60,saved.RemoteIncrementalSeconds);Assert.Equal(300,saved.RemoteFullSeconds);Assert.Equal(88,vm.Draft(saved).Incremental);Assert.Equal(999,vm.Draft(saved).Full);
        area.Store.FailSave=true;await vm.SetCapacityAutomaticAsync(id,false);area.Ui.Drain();Assert.True(area.Manager.TaskDefinitions[id].CapacityAutoDetectEnabled);Assert.True(vm.CapacitySettingsDraft(area.Manager.TaskDefinitions[id]).Automatic);
    }
    [Fact(Timeout=20000)]
    public async Task InitialSwitchOptionsAreSavedBeforeConnectionAndRetainedAfterConnectionFailure()
    {
        var factory=new ReconnectFactory();factory.Release.SetResult();await using var area=new Area(factory);using var vm=new MountsViewModel(area.Manager,area.Ui);
        var path=Path.Combine(Path.GetDirectoryName(area.A.LocalPath)!,"created");Directory.CreateDirectory(path);
        var result=await vm.SaveConnectionAsync(null,"initial",path,false,new(FileSourceKind.WebDav,"https://example.test/dav/"),new(),initial:new(ThumbnailsEnabled:true,RemoteAutoDetectEnabled:false,RemoteIncrementalSeconds:90,RemoteFullSeconds:600,MaximumCapacityBytes:7_000_000_000));
        var saved=area.Manager.TaskDefinitions[result.Mount.Id];Assert.True(saved.ThumbnailsEnabled);Assert.False(saved.RemoteAutoDetectEnabled);Assert.Equal(90,saved.RemoteIncrementalSeconds);Assert.Equal(600,saved.RemoteFullSeconds);Assert.Equal(7_000_000_000,saved.MaximumCapacityBytes);Assert.False(saved.AutoConnect);Assert.Contains(result.Mount.Id,factory.Calls);Assert.Contains("连接未完成",result.Message);Assert.Equal(PageMessageSeverity.Error,result.Severity);
    }
    [Theory]
    [InlineData(0,false)]
    [InlineData(2,false)]
    [InlineData(2,true)]
    public async Task Saved_roots_wait_for_startup_inspection_and_publish_completion(int count,bool failSave)
    {
        await using var area=new Area();if(count==0)area.Store.Items.Clear();
        for(var index=0;index<area.Store.Items.Count;index++)area.Store.Items[index]=area.Store.Items[index] with{RegistrationStage=MountRegistrationStage.Unknown};
        area.Store.FailSave=failSave;await using var manager=new MountManager(area.Repository,area.Logger);using var vm=new MountsViewModel(manager,area.Ui);
        Assert.Equal(count,vm.Items.Count);
        foreach(var row in vm.Items){Assert.Equal("检查中",row.Snapshot.Status);Assert.False(row.CanEdit);Assert.False(row.Actions.CanConnect);Assert.False(row.Actions.CanRemove);}
        await manager.StartAsync();area.Ui.Drain();
        foreach(var row in vm.Items){Assert.NotEqual("检查中",row.Snapshot.Status);Assert.False(row.Snapshot.IsConnected);if(failSave){Assert.Equal("需要恢复",row.Snapshot.Status);Assert.NotEmpty(row.Snapshot.Detail);}}
        if(count>0)Assert.Equal(0,area.Ui.Pending);
    }
    private static object Runtime(MountManager manager,Guid id)=>typeof(MountManager).GetMethod("Root",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(manager,[id])!;
    private static void RuntimeField(MountManager manager,Guid id,string field,object value)
    {var root=Runtime(manager,id);root.GetType().GetField(field)!.SetValue(root,value);}
    private sealed class ReconnectFactory : IFileSourceFactory
    {
        public readonly ConcurrentQueue<Guid> Calls=new();
        public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IFileSource> CreateAsync(MountDefinition mount,MountRepository repository,bool recovery,CancellationToken token)
        {Calls.Enqueue(mount.Id);if(Calls.Count==1){Started.TrySetResult();await Release.Task.WaitAsync(token);}throw new IOException("reconnect failed");}
    }
    [Fact]
    public async Task Runtime_reconnect_is_empty_without_failures_and_user_disconnect_clears_the_banner()
    {
        await using var area=new Area();using var vm=new MountsViewModel(area.Manager,area.Ui);
        Assert.False(vm.HasRuntimeFailedMounts);Assert.False(vm.ReconnectRuntimeFailedCommand.CanExecute(null));await area.Manager.ReconnectRuntimeFailedAsync();
        RuntimeField(area.Manager,area.A.Id,"RuntimeFailed",true);await area.Manager.ClearCompletedOperationsAsync();area.Ui.Drain();
        Assert.True(vm.HasRuntimeFailedMounts);Assert.True(vm.ReconnectRuntimeFailedCommand.CanExecute(null));
        await area.Manager.DisconnectAsync(area.A.Id);area.Ui.Drain();Assert.False(vm.HasRuntimeFailedMounts);Assert.False(vm.ReconnectRuntimeFailedCommand.CanExecute(null));
    }
    [Fact(Timeout=30000)]
    public async Task Runtime_reconnect_includes_manual_mounts_runs_sequentially_and_continues_after_failure()
    {
        var factory=new ReconnectFactory();await using var area=new Area(factory);using var vm=new MountsViewModel(area.Manager,area.Ui);
        foreach(var mount in new[]{area.A,area.B}){Assert.False(mount.AutoConnect);RuntimeField(area.Manager,mount.Id,"RuntimeFailed",true);}
        var work=vm.ReconnectRuntimeFailedAsync();
        try
        {
            await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.Single(factory.Calls);Assert.True(vm.DialogBusy);Assert.False(vm.ReconnectRuntimeFailedCommand.CanExecute(null));
            await vm.ReconnectRuntimeFailedAsync();Assert.Single(factory.Calls);
        }
        finally{factory.Release.TrySetResult();await work;}
        Assert.Equal(new[]{area.A.Id,area.B.Id}.Order(),factory.Calls.Order());Assert.All(area.Manager.Mounts,mount=>Assert.Equal("异常",mount.Status));Assert.True(vm.HasRuntimeFailedMounts);Assert.False(vm.DialogBusy);
    }
    [Theory]
    [InlineData("RemovalRequested")][InlineData("RemovalPending")][InlineData("Connecting")]
    public async Task Runtime_reconnect_skips_removing_and_connecting_roots(string state)
    {
        var factory=new ReconnectFactory();using var connecting=new CancellationTokenSource();await using var area=new Area(factory);
        RuntimeField(area.Manager,area.A.Id,"RuntimeFailed",true);
        if(state=="RemovalPending")RuntimeField(area.Manager,area.A.Id,"Definition",area.A with{RemovalPending=true});
        else RuntimeField(area.Manager,area.A.Id,state,state=="Connecting"?(object)connecting:true);
        await area.Manager.ReconnectRuntimeFailedAsync();Assert.Empty(factory.Calls);
    }
    [Fact]
    public async Task User_disconnect_while_failure_cleanup_is_pending_prevents_batch_reconnection()
    {
        var factory=new ReconnectFactory();await using var area=new Area(factory);var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeField(area.Manager,area.A.Id,"RuntimeFailed",true);RuntimeField(area.Manager,area.A.Id,"RuntimeFailureCleanup",cleanup.Task);
        var work=area.Manager.ReconnectRuntimeFailedAsync();Assert.False(work.IsCompleted);
        try{await area.Manager.DisconnectAsync(area.A.Id);}
        finally{cleanup.TrySetResult();await work;}
        Assert.Empty(factory.Calls);Assert.False(area.Manager.HasRuntimeFailedMounts);
    }
    [Fact]
    public async Task ExclusionDraftsPreserveCustomRowsAcrossRefreshAndFailedSave()
    {
        await using var area=new Area();using var vm=new MountsViewModel(area.Manager,area.Ui);var a=area.A;var b=area.B;
        Assert.Empty(vm.ExclusionDraft(a.Id,FileSourceKind.WebDav));Assert.Equal(4,vm.ExclusionDraft(null,FileSourceKind.WebDav).Count);
        vm.AddExclusionRule(a.Id,FileSourceKind.WebDav);var row=vm.ExclusionDraft(a.Id,FileSourceKind.WebDav).Single();row.Text="custom";row.MatchIndex=1;
        vm.AddDefaultExclusionRules(a.Id,FileSourceKind.WebDav);vm.AddDefaultExclusionRules(a.Id,FileSourceKind.WebDav);Assert.Equal(5,vm.ExclusionDraft(a.Id,FileSourceKind.WebDav).Count);
        vm.SelectedMountId=b.Id;vm.RefreshNow();vm.SelectedMountId=a.Id;Assert.Same(row,vm.ExclusionDraft(a.Id,FileSourceKind.WebDav)[0]);
        area.Store.FailSave=true;await vm.SaveExclusionsAsync(a.Id);Assert.Null(area.Manager.Mounts.Single(x=>x.Definition.Id==a.Id).Definition.ExclusionRules);Assert.Same(row,vm.ExclusionDraft(a.Id,FileSourceKind.WebDav)[0]);
        area.Store.FailSave=false;await vm.SaveExclusionsAsync(a.Id);Assert.Equal(5,area.Manager.Mounts.Single(x=>x.Definition.Id==a.Id).Definition.ExclusionRules!.Count);Assert.Null(area.Manager.Mounts.Single(x=>x.Definition.Id==b.Id).Definition.ExclusionRules);
        vm.AddExclusionRule(a.Id,FileSourceKind.WebDav);await vm.SaveExclusionsAsync(a.Id);Assert.Equal(5,area.Manager.Mounts.Single(x=>x.Definition.Id==a.Id).Definition.ExclusionRules!.Count);
        var draft=vm.ExclusionDraft(a.Id,FileSourceKind.WebDav);vm.Dispose();vm.AddExclusionRule(a.Id,FileSourceKind.WebDav);Assert.Equal(6,draft.Count);
    }
    [Fact]
    public async Task CapacityAndDetectionSettingsDescribeSavedSourceAndKeepFallback()
    {
        await using var area=new Area();var manual=area.A with{MaximumCapacityBytes=10_000_000_000};var capacity=new RemoteCapacitySnapshot(new("scope",7_500_000_000,2_500_000_000,10_000_000_000));
        var mount=new MountSnapshot(manual,"已连接","",Capacity:capacity,Detection:new("RFC 6578 增量检测"));
        Assert.Equal("可用7.5GB/总10.0GB，基于用户输入",MountsViewModel.CapacitySummary(mount));
        Assert.Equal("可用7.5GB/总10.0GB，基于服务器信息",MountsViewModel.CapacitySummary(mount with{Definition=manual with{CapacityAutoDetectEnabled=true}}));
        Assert.Equal("可用—/总不限，基于用户输入",MountsViewModel.CapacitySummary(mount with{Definition=area.A,Capacity=null}));
        Assert.DoesNotContain("RFC 6578 增量检测",MountsViewModel.DetectionSummary(mount));
        Assert.Contains("本次已停用",MountsViewModel.DetectionSummary(mount with{Detection=new("完整对账（RFC 6578 响应异常，本次已停用）",Error:"invalid href")}));
        Assert.Contains("invalid href",MountsViewModel.DetectionSummary(mount with{Detection=new("完整对账",Error:"invalid href")}));
        Assert.Contains("保存失败",MountsViewModel.CapacitySummary(mount with{Capacity=capacity with{Error="保存失败"}}));
    }
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public async Task Missing_thumbnail_entry_hides_controls_without_rewriting_saved_preference(bool packaged,bool queryFailed)
    {
        await using var area=new Area();var other=area.B;var saved=area.A with{ThumbnailsEnabled=true};area.Store.SaveDefinition(saved);
        await using var manager=new MountManager(area.Repository,area.Logger);
        using var vm=new MountsViewModel(manager,area.Ui,thumbnailCapability:ShellThumbnailCapability.From(packaged,false,queryFailed));
        var row=vm.Items.Single(x=>x.Id==saved.Id);vm.SelectedMountId=saved.Id;vm.RefreshNow();vm.SelectedMountId=other.Id;vm.RefreshNow();
        Assert.False(vm.CanConfigureThumbnails);Assert.False(row.CanConfigureThumbnails);Assert.True(row.ThumbnailsEnabled);Assert.True(vm.ThumbnailDraft(saved));Assert.Equal(saved,area.Store.Items.Single(x=>x.Id==saved.Id));
        Assert.Empty(manager.Operations);Assert.False(string.IsNullOrWhiteSpace(vm.ThumbnailCapability.Reason));
    }
    [Fact]
    public async Task Explicit_packaged_candidate_capability_can_edit_only_selected_thumbnail_preference()
    {
        await using var area=new Area();var a=area.A;var b=area.B;using var vm=new MountsViewModel(area.Manager,area.Ui,thumbnailCapability:ShellThumbnailCapability.From(true,true));
        vm.SelectedMountId=b.Id;var row=vm.Items.Single(x=>x.Id==a.Id);Assert.True(vm.CanConfigureThumbnails);Assert.True(row.CanConfigureThumbnails);
        await vm.SetThumbnailAsync(a.Id,true);Assert.True(vm.FindMount(a.Id)!.Definition.ThumbnailsEnabled);Assert.False(vm.FindMount(b.Id)!.Definition.ThumbnailsEnabled);Assert.Equal(b.Id,vm.SelectedMountId);
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
    [Fact]
    public async Task Mount_rows_are_stable_and_id_commands_do_not_follow_changed_selection()
    {
        await using var area=new Area();await area.Manager.StartAsync();using var vm=new MountsViewModel(area.Manager,area.Ui);var a=area.A;var b=area.B;
        vm.SelectedMountId=a.Id;var first=vm.Items.Single(x=>x.Id==a.Id);Assert.True(first.IsSelected);
        vm.SelectedMountId=b.Id;vm.RefreshNow();Assert.Same(first,vm.Items.Single(x=>x.Id==a.Id));Assert.False(first.IsSelected);Assert.True(vm.Items.Single(x=>x.Id==b.Id).IsSelected);
        vm.UpdateCapacityDraft(a.Id,false,"3","GB");await vm.SaveCapacityAsync(a.Id);Assert.Equal(b.Id,vm.SelectedMountId);Assert.Equal(3_000_000_000,area.Manager.Mounts.Single(x=>x.Definition.Id==a.Id).Definition.MaximumCapacityBytes);Assert.Null(area.Manager.Mounts.Single(x=>x.Definition.Id==b.Id).Definition.MaximumCapacityBytes);
        Assert.Equal(vm.FindMount(a.Id)!.Detail.Length>0?vm.FindMount(a.Id)!.Detail:"当前没有诊断信息。",vm.Diagnostic(a.Id));Assert.Equal(b.Id,vm.SelectedMountId);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var work=vm.RunMountAsync(a.Id,()=>release.Task);Assert.False(first.CanEdit);Assert.True(vm.Items.Single(x=>x.Id==b.Id).CanEdit);release.SetResult();await work;Assert.True(first.CanEdit);
    }
    [Fact]
    public async Task Mount_shortcuts_preserve_unsaved_intervals_and_failure_keeps_actual_toggle_state()
    {
        await using var area=new Area();using var vm=new MountsViewModel(area.Manager,area.Ui);var a=area.A;var b=area.B;
        vm.UpdateDraft(a.Id,true,91,601);vm.SelectedMountId=b.Id;
        await vm.SetRemoteDetectionEnabledAsync(a.Id,false);var saved=vm.FindMount(a.Id)!.Definition;
        Assert.False(saved.RemoteAutoDetectEnabled);Assert.Equal(a.RemoteIncrementalSeconds,saved.RemoteIncrementalSeconds);Assert.Equal(91,vm.Draft(saved).Incremental);Assert.False(vm.Draft(saved).Enabled);
        Assert.True(vm.FindMount(b.Id)!.Definition.RemoteAutoDetectEnabled);Assert.Equal(b.Id,vm.SelectedMountId);
        area.Store.FailSave=true;await vm.SetRemoteDetectionEnabledAsync(a.Id,true);Assert.False(vm.FindMount(a.Id)!.Definition.RemoteAutoDetectEnabled);Assert.False(vm.Draft(saved).Enabled);
        await vm.SetThumbnailAsync(a.Id,true);Assert.False(vm.FindMount(a.Id)!.Definition.ThumbnailsEnabled);
        area.Store.FailSave=false;await vm.SetThumbnailAsync(a.Id,true);Assert.True(vm.FindMount(a.Id)!.Definition.ThumbnailsEnabled);Assert.False(vm.FindMount(b.Id)!.Definition.ThumbnailsEnabled);
    }
    [Fact]
    public async Task Configuration_validation_failure_releases_busy_and_does_not_replace_saved_source()
    {
        await using var area=new Area();using var vm=new MountsViewModel(area.Manager,area.Ui);var original=area.A;
        await Assert.ThrowsAsync<ArgumentException>(()=>vm.SaveConnectionAsync(original.Id,original.Name,original.LocalPath,original.AutoConnect,new(FileSourceKind.WebDav,"file:///invalid"),new()));
        Assert.False(vm.IsBusy(original.Id));Assert.Equal(original.Source,vm.FindMount(original.Id)!.Definition.Source);Assert.Equal(2,vm.Items.Count);Assert.Null(MountPathGuard.ReadRoot(original.LocalPath));
    }
    [Fact]
    public async Task Task_history_clear_modes_refresh_buttons_and_preserve_pending_states()
    {
        await using var area=new Area();
        foreach(var state in new[]{"Completed","Failed","PartialFailure","Canceled","Running","Waiting","Conflict","OutcomeUnknown"})area.Repository.SaveOperation(new(state,"refresh",state,0,0,[],null));
        using var vm=new TasksViewModel(area.Manager,area.Ui);Assert.True(vm.CanClearCompleted);Assert.True(vm.CanClearFailed);Assert.True(vm.CanClearAll);
        await vm.ClearFailedAsync();Assert.False(vm.CanClearFailed);Assert.True(vm.CanClearCompleted);Assert.DoesNotContain(vm.Operations,row=>row.State is "Failed" or "PartialFailure");
        await vm.ClearCompletedAsync();Assert.False(vm.CanClearCompleted);await vm.ClearAllAsync();Assert.False(vm.CanClearAll);Assert.Equal(3,vm.Operations.Count);Assert.All(vm.Operations,row=>Assert.Contains(row.State,new[]{"Waiting","Conflict","OutcomeUnknown"}));
        vm.Dispose();area.Repository.SaveOperation(new("late","refresh","Failed",0,0,[],null));await vm.ClearFailedAsync();Assert.Contains(area.Repository.LoadOperations(),row=>row.Id=="late");
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
    [Fact]
    public void SettingsResetHasIndependentFeedbackAndBlocksDuplicateEntry()
    {
        using var vm=new SettingsViewModel(new AppearanceSettingsService(new AppearanceStore()));vm.AppearanceMessage.Show("appearance");vm.ResetMessage.Show("reset");vm.ResetMessage.IsOpen=false;Assert.True(vm.AppearanceMessage.IsOpen);
        Assert.True(vm.TryBeginReset());Assert.False(vm.CanEditSettings);Assert.False(vm.TryBeginReset());vm.EndReset();Assert.True(vm.CanReset);vm.ResetMessage.Show("reset");Assert.True(vm.ResetMessage.IsOpen);
        vm.Dispose();vm.EndReset();Assert.False(vm.CanReset);Assert.False(vm.TryBeginReset());
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
