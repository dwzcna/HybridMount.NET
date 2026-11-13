using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Services.Mounting;

namespace HybridMount.ViewModels;

/// <summary>设置页唯一状态源；拥有外观、启动、托盘及缓存草稿，不保存窗口或控件。</summary>
public sealed partial class SettingsViewModel : ObservableObject,IDisposable,IAsyncDisposable
{
    private readonly AppearanceSettingsService _settings;
    private readonly global::HybridMount.Services.Localization.UiLanguageService _languages;private readonly IDisposable _languageSubscription;private bool _savingLanguage;
    private readonly IStartupService? _startup;
    private readonly ApplicationPreferencesService? _preferences;
    private readonly MountManager? _mounts;
    private readonly LoggerService? _logger;
    private bool _busy,_saving,_disposed,_notifyingSelections,_trimming,_cacheLoaded,_savingCache,_savingClose,_notifyingStartup,_savingDensity;
    private StartupState _startupState=new(false,true,global::HybridMount.Services.Localization.UiText.Text("Text_02A8658F6601"));
    /// <summary>三个卡片各自保留关闭状态，不互相覆盖消息。</summary>
    public PageStatusMessage StatusMessage {get;}=new();
    public PageStatusMessage AppearanceMessage {get;}=new();
    public PageStatusMessage CacheMessage {get;}=new();
    /// <summary>还原反馈独立于其余卡片；待确认及退出时冻结页面编辑，仍允许关闭提示。</summary>
    public PageStatusMessage ResetMessage {get;}=new();
    private bool _resetting;
    public bool CanEditSettings=>!_disposed&&!_resetting;
    public bool CanReset=>CanEditSettings;
    public bool TryBeginReset(){if(!CanReset)return false;_resetting=true;OnPropertyChanged(nameof(CanEditSettings));OnPropertyChanged(nameof(CanReset));return true;}
    public Task DrainSettingsOperationsAsync()=>Task.WhenAll(_startupWork,_cacheWork,_cloudWork);
    public void EndReset(){if(!_disposed){_resetting=false;OnPropertyChanged(nameof(CanEditSettings));OnPropertyChanged(nameof(CanReset));}}
    private void Publish(PageStatusMessage message,string text,string property,bool reopen=true,PageMessageSeverity severity=PageMessageSeverity.Information)
    {if(!_disposed&&(reopen?message.Show(text,severity):message.Update(text,severity)))OnPropertyChanged(property);}
    private double _cacheLimitGiB,_savedCacheLimitGiB;
    private Task _startupWork=Task.CompletedTask,_cacheWork=Task.CompletedTask;
    /// <summary>生产页面注入应用服务；仅传外观服务时可隔离验证外观，不接触系统启动或注册表。</summary>
    public SettingsViewModel(AppearanceSettingsService appearance,IStartupService? startup=null,ApplicationPreferencesService? preferences=null,MountManager? mounts=null,LoggerService? logger=null,global::HybridMount.Services.Localization.UiLanguageService? languages=null,CloudOperationSettings? cloud=null)
    {
        _languages=languages??global::HybridMount.Services.Localization.UiText.Current;_languageSubscription=global::HybridMount.Services.Localization.UiText.Observe(this,static model=>model.RefreshLanguage());
        _settings=appearance;_startup=startup;_preferences=preferences;_mounts=mounts;_logger=logger;
        AppearanceMessage.Update(appearance.ErrorStatus??preferences?.LayoutErrorStatus??"",PageMessageSeverity.Error);StatusMessage.Update(preferences?.ErrorStatus??"",PageMessageSeverity.Error);if(preferences is not null)preferences.LayoutChanged+=DensityChanged;
        try{if(mounts is not null){_savedCacheLimitGiB=_cacheLimitGiB=mounts.CacheLimitBytes/(double)(1024L*1024*1024);_cacheLoaded=true;}}
        catch(Exception error){CacheMessage.Update(global::HybridMount.Services.Localization.UiText.Text("Text_A2A57F95410A"),PageMessageSeverity.Error);_logger?.Queue("缓存","Warning",error.GetType().Name);}
        if(_languages.Error is {} languageError)AppearanceMessage.Update(languageError,PageMessageSeverity.Error);
        _cloud=cloud;_cloudConcurrency=(cloud?.Current??CloudOperationSettings.Default).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if(cloud?.ReadFailed==true)CloudMessage.Update(global::HybridMount.Services.Localization.UiText.Text("CloudReadFailed"),PageMessageSeverity.Error);
        appearance.Changed+=SettingsChanged;
    }
    /// <summary>语言保存成功后立即更新；失败仅回滚选择，保留原语言和其他卡片状态。</summary>
    public bool CanEditLanguage=>!_disposed&&!_savingLanguage&&_languages.IsLoaded;
    public string LanguageSelection
    {
        get=>_languages.Current.Choice;
        set{if(_disposed||_savingLanguage||value==LanguageSelection||!CanEditLanguage)return;_savingLanguage=true;try{var saved=_languages.TrySave(value,out var error);var message=saved?global::HybridMount.Services.Localization.UiText.Message("Text_2A1924653192"):error!;if(AppearanceMessage.Show(message,saved?PageMessageSeverity.Success:PageMessageSeverity.Error))OnPropertyChanged(nameof(AppearanceStatus));}finally{_savingLanguage=false;OnPropertyChanged(nameof(LanguageSelection));OnPropertyChanged(nameof(CanEditLanguage));}}
    }
    private void RefreshLanguage(){if(_disposed)return;OnPropertyChanged(nameof(LanguageSelection));OnPropertyChanged(nameof(CanEditLanguage));OnPropertyChanged(nameof(StartupDetail));OnPropertyChanged(nameof(AppearanceStatus));OnPropertyChanged(nameof(Status));OnPropertyChanged(nameof(CacheStatus));OnPropertyChanged(nameof(CloudRecommendation));}
    public bool CanEditDensity=>!_disposed&&_preferences is not null&&!_savingDensity;
    public string DensitySelection
    {
        get=>_preferences?.CompactLayout==true?"Compact":"Comfortable";
        set
        {
            if(!CanEditDensity||value==DensitySelection||value is not("Compact" or "Comfortable"))return;_savingDensity=true;
            try{var success=_preferences!.TrySetCompactLayout(value=="Compact",out var error);Publish(AppearanceMessage,success?global::HybridMount.Services.Localization.UiText.Text("Text_D2ACA8610E97"):error??global::HybridMount.Services.Localization.UiText.Text("Text_4465E830BDF6"),nameof(AppearanceStatus),severity:success?PageMessageSeverity.Success:PageMessageSeverity.Error);}
            finally{_savingDensity=false;OnPropertyChanged(nameof(DensitySelection));OnPropertyChanged(nameof(CanEditDensity));}
        }
    }
    private void DensityChanged(){if(!_disposed&&!_savingDensity)OnPropertyChanged(nameof(DensitySelection));}
    public string VersionText=>"HybridMount.NET "+typeof(SettingsViewModel).Assembly.GetName().Version;
    public bool CanEditStartup=>!_disposed&&!_busy&&_startup is not null&&_startupState.CanToggle;
    public string StartupDetail=>global::HybridMount.Services.Localization.UiText.Legacy(_startupState.Detail);
    public string Status {get=>StatusMessage.Text;private set=>Publish(StatusMessage,value,nameof(Status));}
    public bool CloseToTraySelection
    {
        get=>_preferences?.CloseToTray??true;
        set
        {
            if(_disposed||_savingClose||_preferences is null||value==CloseToTraySelection)return;
            _savingClose=true;
            try{var success=_preferences.TrySetCloseToTray(value,out var error);Publish(StatusMessage,success?value?global::HybridMount.Services.Localization.UiText.Text("Text_584FE9F9F916"):global::HybridMount.Services.Localization.UiText.Text("Text_15BA0E43029F"):error??global::HybridMount.Services.Localization.UiText.Text("Text_F3F84AE474C2"),nameof(Status),severity:success?PageMessageSeverity.Success:PageMessageSeverity.Error);if(error is not null)_logger?.Queue("设置","Warning","关闭行为偏好保存失败，原设置保持。");OnPropertyChanged();}
            finally{_savingClose=false;}
        }
    }
    public bool StartupSelection {get=>_startupState.Enabled;set{if(!_notifyingStartup&&value!=StartupSelection&&CanEditStartup)_=ChangeStartupAsync(value);}}
    public Task RefreshStartupAsync()=>StartStartup(null);
    public Task ChangeStartupAsync(bool enabled)=>StartStartup(enabled);
    private Task StartStartup(bool? enabled)
    {
        if(_disposed||_notifyingStartup||_busy||_startup is null)return Task.CompletedTask;
        if(enabled is not null&&!_startupState.CanToggle)return Task.CompletedTask;
        _busy=true;NotifyStartup();return _startupWork=UpdateStartupAsync(enabled);
    }
    private async Task UpdateStartupAsync(bool? enabled)
    {
        try
        {
            var actual=enabled is {} value?await _startup!.SetAsync(value):await _startup!.GetAsync();
            if(!_disposed){_startupState=actual;if(enabled is not null)Publish(StatusMessage,actual.Detail,nameof(Status),severity:actual.Enabled==enabled?PageMessageSeverity.Success:PageMessageSeverity.Warning);}
        }
        catch(Exception error)
        {
            if(_disposed)return;
            Publish(StatusMessage,enabled is null?global::HybridMount.Services.Localization.UiText.Text("Text_CC0C4EB91412"):global::HybridMount.Services.Localization.UiText.Text("Text_302C30CAC1DA"),nameof(Status),enabled is not null,PageMessageSeverity.Error);_logger?.Queue("启动","Error",error.GetType().Name);
            if(enabled is not null)try{var actual=await _startup!.GetAsync();if(!_disposed)_startupState=actual;}catch(Exception query){if(!_disposed)_logger?.Queue("启动","Warning",query.GetType().Name);}
        }
        finally{_busy=false;if(!_disposed)NotifyStartup();}
    }
    private void NotifyStartup()
    {
        if(_notifyingStartup||_disposed)return;_notifyingStartup=true;
        try{OnPropertyChanged(nameof(StartupSelection));OnPropertyChanged(nameof(CanEditStartup));OnPropertyChanged(nameof(StartupDetail));}
        finally{_notifyingStartup=false;}
    }
    /// <summary>缓存输入属于页面草稿，切换页面不重读覆盖；成功保存后才推进服务中的限制。</summary>
    public double CacheLimitGiB {get=>_cacheLimitGiB;set{if(!_disposed)SetProperty(ref _cacheLimitGiB,value);}}
    public string CacheStatus {get=>CacheMessage.Text;private set=>Publish(CacheMessage,value,nameof(CacheStatus));}
    public bool CanEditCache=>!_disposed&&_cacheLoaded&&!_trimming&&_mounts is not null&&!_mounts.IsStopping;
    public void SaveCacheLimit()
    {
        if(!CanEditCache||_savingCache)return;_savingCache=true;
        try
        {
            if(!double.IsFinite(CacheLimitGiB)||CacheLimitGiB<0)throw new ArgumentException(global::HybridMount.Services.Localization.UiText.Text("Text_CF6F4B5AB212"));
            _mounts!.CacheLimitBytes=checked((long)(CacheLimitGiB*1024*1024*1024));_savedCacheLimitGiB=CacheLimitGiB;Publish(CacheMessage,global::HybridMount.Services.Localization.UiText.Text("Text_405EE9C5A7CC"),nameof(CacheStatus),severity:PageMessageSeverity.Success);
        }
        catch(Exception error){CacheLimitGiB=_savedCacheLimitGiB;Publish(CacheMessage,error.Message,nameof(CacheStatus),severity:PageMessageSeverity.Error);_logger?.QueueException("缓存","Warning","缓存操作失败",error);}
        finally{_savingCache=false;}
    }
    public Task TrimCacheAsync()
    {
        if(!CanEditCache)return Task.CompletedTask;
        _trimming=true;OnPropertyChanged(nameof(CanEditCache));return _cacheWork=TrimCoreAsync();
    }
    private async Task TrimCoreAsync()
    {
        try{var result=await Task.Run(async()=>{var freed=await _mounts!.EnforceCacheLimitAsync().ConfigureAwait(false);return (Freed:freed,Usage:await _mounts.GetCacheUsageAsync().ConfigureAwait(false));});if(!_disposed)CacheStatus=global::HybridMount.Services.Localization.UiText.Text("Text_9A4D7A27AB0E",result.Freed/(1024d*1024),result.Usage/(1024d*1024));}
        catch(Exception error){if(!_disposed)Publish(CacheMessage,error.Message,nameof(CacheStatus),severity:PageMessageSeverity.Error);_logger?.QueueException("缓存","Warning","缓存操作失败",error);}
        finally{_trimming=false;if(!_disposed)OnPropertyChanged(nameof(CanEditCache));}
    }
    public bool CanEditAppearance => !_disposed && _settings.IsLoaded && !_saving;
    public string AppearanceStatus {get=>AppearanceMessage.Text;private set=>Publish(AppearanceMessage,value,nameof(AppearanceStatus));}
    /// <summary>ComboBox稳定Tag；未知值和加载期间的空选择不回写默认。</summary>
    public string BackdropSelection
    {
        get => _settings.Current.Backdrop.ToString();
        set
        {
            // <remarks>Source→Target→Source同步回填相同值直接结束；通知／保存期间拒绝重入，不再次通知。</remarks>
            if (value == BackdropSelection || _disposed || _saving || _notifyingSelections) return;
            if (CanEditAppearance && AppearancePreferences.TryBackdrop(value, out var backdrop))
                Save(_settings.Current with { Backdrop = backdrop });
            else NotifySelections();
        }
    }
    public string ThemeSelection
    {
        get => _settings.Current.Theme.ToString();
        set
        {
            if (value == ThemeSelection || _disposed || _saving || _notifyingSelections) return;
            if (CanEditAppearance && AppearancePreferences.TryTheme(value, out var theme))
                Save(_settings.Current with { Theme = theme });
            else NotifySelections();
        }
    }
    private void Save(AppearancePreferences candidate)
    {
        _saving = true;
        try
        {
            OnPropertyChanged(nameof(CanEditAppearance));
            var success=_settings.TrySave(candidate,out var error);Publish(AppearanceMessage,success?global::HybridMount.Services.Localization.UiText.Text("Text_BCB9E6D7CDB7"):error??global::HybridMount.Services.Localization.UiText.Text("Text_18807DECF3A4"),nameof(AppearanceStatus),severity:success?PageMessageSeverity.Success:PageMessageSeverity.Error);if(error is not null)_logger?.Queue("外观","Warning","外观设置保存失败，原设置保持。");
        }
        finally { _saving = false; NotifySelections(); }
    }
    /// <summary>进入设置页重读正式偏好；失败保留有效选择并展示固定错误。</summary>
    public void ReloadAppearance()
    {
        if (_disposed || _saving || _notifyingSelections) return;
        _saving = true;
        try { if (!_settings.Reload()) Publish(AppearanceMessage,_settings.ErrorStatus ?? global::HybridMount.Services.Localization.UiText.Text("Text_E443ECCB0E9F"),nameof(AppearanceStatus),false,PageMessageSeverity.Error); }
        finally { _saving = false; NotifySelections(); }
    }
    private void SettingsChanged(object? sender, EventArgs args)
    { if (!_disposed && !_saving) NotifySelections(); }
    /// <summary>统一有限回滚通知；xBind同步回填期间只读getter，不再次写入或触发通知。</summary>
    /// <remarks>标记覆盖Source→Target→Source闭环；异常也在finally清除，不永久锁住编辑。</remarks>
    private void NotifySelections()
    {
        if (_disposed || _notifyingSelections) return;
        _notifyingSelections = true;
        try
        {
            OnPropertyChanged(nameof(BackdropSelection));
            OnPropertyChanged(nameof(ThemeSelection));
            OnPropertyChanged(nameof(CanEditAppearance));
        }
        finally { _notifyingSelections = false; }
    }

    /// <summary>真正退出先停止页面通知；系统已发出的操作安全完成后再释放日志等服务。</summary>
    public void Dispose(){_languageSubscription.Dispose();if(_disposed)return;_disposed=true;StatusMessage.Dispose();AppearanceMessage.Dispose();CacheMessage.Dispose();ResetMessage.Dispose();CloudMessage.Dispose();_settings.Changed-=SettingsChanged;if(_preferences is not null)_preferences.LayoutChanged-=DensityChanged;}
    public async ValueTask DisposeAsync(){Dispose();await Task.WhenAll(_startupWork,_cacheWork,_cloudWork);}
}
